using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WilcoATC.Common;
using WilcoATC.Diagnostics;
using WilcoATC.Formatting;
using Microsoft.FlightSimulator.SimConnect;

namespace WilcoATC.Sim;

/// <summary>
/// Couche SimConnect complète : connexion, reconnexion automatique, boucle de
/// réception de messages, définition/abonnement des SimVars, détection de
/// changement des fréquences COM, contexte de vol et aéroport le plus proche.
///
/// THREADING — tout ce qui touche SimConnect (création, ReceiveMessage, callbacks,
/// dispose) vit sur UN thread dédié (« SimConnect-Pump »). Les résultats sont
/// publiés via des événements .NET ; le ViewModel marshalle vers le thread UI.
/// On pompe en mode « event-based » : SimConnect signale un WaitHandle, on appelle
/// ReceiveMessage(). L'UI n'est jamais bloquée.
/// </summary>
public sealed class SimConnectService : ISimConnectService
{
    private const uint WM_USER_SIMCONNECT = 0x0402; // requis par le constructeur, non utilisé (on pompe via WaitHandle)
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private SimConnect? _sim;

    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private volatile bool _connectionLost;

    private readonly AutoResetEvent _receiveSignal = new(false);
    private readonly AutoResetEvent _stopSignal = new(false);
    // Actions à exécuter SUR LE THREAD DE POMPE (ex. envoi d'un event au simu), file thread-safe.
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly AutoResetEvent _actionSignal = new(false);

    private RadioData? _lastRadio; // pour la détection de changement radio (journal)

    // Cache des aéroports fourni par SubscribeToFacilities(AIRPORT) : ICAO -> coordonnées.
    // Écrit sur le thread de pompage, LU AUSSI DE L'EXTÉRIEUR -> protégé par _airportsGate.
    private readonly Dictionary<string, (double Lat, double Lon)> _airports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Protège <see cref="_airports"/>. Ce cache était jusqu'ici écrit ET lu sur le seul
    /// thread de pompage, donc sans verrou. Il est désormais consulté depuis l'extérieur
    /// (position d'un terrain, pour situer le trafic en finale), et un dictionnaire lu
    /// pendant qu'on l'écrit peut lever ou boucler.
    /// </summary>
    private readonly object _airportsGate = new();

    // Fréquences COM live (facility data). L'état est lu/écrit UNIQUEMENT sur le thread de
    // pompage (les demandes venues d'autres threads passent par la file _pending) -> pas de
    // verrou. _facilityDefReady reste faux si le SDK du simulateur ne gère pas la facility
    // data : la couche appelante retombe alors proprement sur le CSV.
    private bool _facilityDefReady;
    private uint _nextFacReq = 1000;   // ids de requête facility, hors plage des REQUEST fixes (0..4)
    private readonly Dictionary<uint, string> _facIcao = new();
    private readonly Dictionary<uint, List<SimComFrequency>> _facAccum = new();

    public ConnectionState State { get; private set; } = ConnectionState.Waiting;
    public string? StatusDetail { get; private set; } = "Waiting for the simulator…";

    public event Action<ConnectionState, string?>? StateChanged;
    public event Action<RadioSnapshot>? RadioSnapshotReceived;
    public event Action<RadioChange>? RadioChanged;
    public event Action<ContextSnapshot>? ContextReceived;
    public event Action<AircraftSnapshot>? AircraftReceived;
    public event Action<WeatherSnapshot>? WeatherReceived;
    public event Action<AirportFacilityFrequencies>? AirportFrequenciesReceived;

    // ------------------------------------------------------------------ cycle de vie

    public void Start()
    {
        if (_pumpThread is not null) return;
        _stopRequested = false;
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "SimConnect-Pump" };
        _pumpThread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        _stopSignal.Set();
        _pumpThread?.Join(TimeSpan.FromSeconds(2));
        _pumpThread = null;
    }

    public void Dispose()
    {
        Stop();
        _receiveSignal.Dispose();
        _stopSignal.Dispose();
        _actionSignal.Dispose();
    }

    /// <summary>
    /// Allume/éteint le phare anticollision (beacon) — utilisé pour déclencher l'auto-pushback
    /// GSX. Thread-safe : l'ordre est mis en file et exécuté sur le thread de pompage.
    /// </summary>
    // ------------------------------------------------------------------ intercepteur (IA)

    public event Action<uint>? InterceptorCreated;
    public event Action<FormationSnapshot>? FormationTick;
    public event Action<NearbyAircraft>? NearbyAircraftSeen;
    public event Action<NearbyAircraftState>? NearbyAircraftStateSeen;

    /// <summary>
    /// Relève l'ÉTAT DE VOL des appareils environnants. Requête distincte de l'identité, mais
    /// portant sur le même ensemble : le rapprochement se fait par identifiant d'objet.
    /// </summary>
    public void RequestNearbyAircraftState(uint radiusMeters)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try
            {
                _sim.RequestDataOnSimObjectType(
                    REQUEST.NearbyState, DEFINITION.NearbyState,
                    radiusMeters, SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT);
            }
            catch (COMException ex) { Log($"relevé de l'état du trafic : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    /// <summary>
    /// Relève les appareils présents dans un rayon donné. La réponse arrive appareil par
    /// appareil via <see cref="NearbyAircraftSeen"/> ; s'il n'y a personne, rien n'arrive —
    /// et c'est en soi une information : le trafic du simulateur est éteint.
    ///
    /// Relevé PONCTUEL, jamais un abonnement : le trafic change lentement, et interroger le
    /// monde entier à répétition ne rapporterait rien de plus.
    /// </summary>
    /// <summary>
    /// Position d'un aéroport du cache du simulateur. Sert à situer le trafic par rapport au
    /// terrain — sans elle, on ne saurait dire d'un appareil qu'il est « à six milles en
    /// finale ». Faux tant que le simulateur n'a pas envoyé ce terrain : il ne publie que la
    /// bulle chargée autour de l'avion, ce qui suffit largement ici.
    /// </summary>
    public bool TryGetAirportPosition(string icao, out double lat, out double lon)
    {
        lat = lon = 0;
        if (string.IsNullOrWhiteSpace(icao)) return false;

        lock (_airportsGate)
        {
            if (!_airports.TryGetValue(icao, out var pos)) return false;
            (lat, lon) = (pos.Lat, pos.Lon);
            return true;
        }
    }

    /// <summary>
    /// Aéroports connus du simulateur autour de l'avion, avec leurs coordonnées. C'est la
    /// matière première des plans de vol injectés : sans terrains voisins, impossible de faire
    /// venir un appareil de quelque part.
    /// </summary>
    public IReadOnlyList<(string Icao, double Lat, double Lon)> NearbyAirports()
    {
        lock (_airportsGate)
            return _airports.Select(kv => (kv.Key, kv.Value.Lat, kv.Value.Lon)).ToList();
    }

    /// <summary>
    /// Fait naître un appareil GARÉ à un terrain. Le simulateur lui choisit lui-même un poste
    /// de stationnement libre — nous n'avons ni la carte des parkings ni à l'inventer.
    /// </summary>
    public void CreateParkedAircraft(string title, string tailNumber, string airportIcao)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try
            {
                _sim.AICreateParkedATCAircraft(title, tailNumber, airportIcao, REQUEST.CreateTraffic);
            }
            catch (COMException ex) { Log($"AICreateParkedATCAircraft « {title} » : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    /// <summary>
    /// Fait naître un appareil SUR UN PLAN DE VOL. Le simulateur le pilote intégralement :
    /// roulage, décollage, croisière, approche, atterrissage. Nous n'écrivons plus jamais sa
    /// position — c'est précisément ce qui le rend fluide, là où un appareil poussé de
    /// l'extérieur ne peut pas l'être.
    /// </summary>
    /// <param name="flightPlanPathNoExtension">Chemin du .PLN, SANS l'extension.</param>
    /// <param name="planPosition">
    /// Où le placer sur son plan : partie entière = numéro de point, partie décimale =
    /// progression vers le suivant. Zéro le fait partir du début.
    /// </param>
    public void CreateEnrouteAircraft(string title, string tailNumber, int flightNumber,
                                      string flightPlanPathNoExtension, double planPosition,
                                      bool touchAndGo = false)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try
            {
                _sim.AICreateEnrouteATCAircraft(title, tailNumber, flightNumber,
                                                flightPlanPathNoExtension, planPosition,
                                                touchAndGo, REQUEST.CreateTraffic);
            }
            catch (COMException ex) { Log($"AICreateEnrouteATCAircraft « {title} » : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    /// <summary>Retire un appareil injecté. Sans effet si l'identifiant est inconnu.</summary>
    public void RemoveAircraft(uint objectId)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try { _sim.AIRemoveObject(objectId, REQUEST.RemoveTraffic); }
            catch (COMException ex) { Log($"AIRemoveObject (trafic) : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    /// <summary>Identifiant d'un appareil de trafic créé.</summary>
    public event Action<uint>? TrafficAircraftCreated;

    public void RequestNearbyAircraft(uint radiusMeters)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try
            {
                _sim.RequestDataOnSimObjectType(
                    REQUEST.NearbyAircraft, DEFINITION.NearbyAircraft,
                    radiusMeters, SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT);
            }
            catch (COMException ex) { Log($"relevé des avions autour : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    public void StartFormationUpdates() => SetFormationStream(true);
    public void StopFormationUpdates() => SetFormationStream(false);

    private void SetFormationStream(bool on)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try
            {
                // SIM_FRAME = une fois par image du simulateur. NEVER coupe l'abonnement :
                // on ne laisse pas tourner un flux aussi dense après l'escorte.
                _sim.RequestDataOnSimObject(
                    REQUEST.Formation, DEFINITION.Formation, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    on ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                Log(on ? "flux de formation démarré" : "flux de formation arrêté");
            }
            catch (COMException ex) { Log($"flux de formation : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    public void CreateInterceptor(string title, string tailNumber,
                                  double lat, double lon, double altitudeFeet,
                                  double headingTrueDeg, double airspeedKnots)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try
            {
                var pos = new SIMCONNECT_DATA_INITPOSITION
                {
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = altitudeFeet,
                    Pitch = 0,
                    Bank = 0,
                    Heading = headingTrueDeg,
                    OnGround = 0,
                    Airspeed = (uint)Math.Max(0, airspeedKnots),
                };

                _sim.AICreateNonATCAircraft(title, tailNumber, pos, REQUEST.CreateInterceptor);
                Log($"intercepteur demandé : « {title} » ({tailNumber})");
            }
            // Un titre inconnu lève une exception SimConnect : on la journalise sans casser
            // la boucle. L'appelant s'apercevra qu'aucun identifiant n'est arrivé.
            catch (COMException ex) { Log($"AICreateNonATCAircraft : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    public void MoveInterceptor(uint objectId, double lat, double lon, double altitudeFeet,
                                double pitchDeg, double bankDeg, double headingTrueDeg,
                                double airspeedKnots,
                                double velocityEastFps, double velocityUpFps, double velocityNorthFps)
    {
        // APPEL DIRECT quand on est DÉJÀ sur le thread de pompage — c'est le cas normal, le
        // placement étant déclenché par le flux de formation, qui arrive sur ce thread. Passer
        // par la file ferait attendre une image de plus à chaque position, et suffit à rendre
        // le vol saccadé. Depuis un autre thread, on repasse par la file comme il se doit.
        if (Thread.CurrentThread == _pumpThread) { WriteInterceptorPosition(); return; }

        _pending.Enqueue(WriteInterceptorPosition);
        _actionSignal.Set();

        void WriteInterceptorPosition()
        {
            if (_sim is null) return;
            try
            {
                var data = new AiPositionData
                {
                    Latitude = lat,
                    Longitude = lon,
                    AltitudeFeet = altitudeFeet,
                    PitchDeg = pitchDeg,
                    BankDeg = bankDeg,
                    HeadingTrue = headingTrueDeg,
                    OnGround = 0,
                    AirspeedTrue = airspeedKnots,
                    VelocityEast = velocityEastFps,
                    VelocityUp = velocityUpFps,
                    VelocityNorth = velocityNorthFps,
                };

                _sim.SetDataOnSimObject(DEFINITION.AiPosition, objectId,
                                        SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
            }
            catch (COMException ex) { Log($"SetDataOnSimObject (intercepteur) : {ex.Message}"); }
        }
    }

    public void RemoveInterceptor(uint objectId)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            // On dégèle avant de retirer : un objet gelé qu'on supprime laisse parfois le
            // simulateur avec un gel orphelin, et le prochain appareil créé naîtrait figé.
            FreezeObject(objectId, false);
            try { _sim.AIRemoveObject(objectId, REQUEST.RemoveInterceptor); }
            catch (COMException ex) { Log($"AIRemoveObject : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    private void OnRecvAssignedObjectId(SimConnect sender, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        // Trafic injecté : le simulateur le pilote seul, on ne le gèle SURTOUT PAS — le geler
        // reviendrait à lui retirer exactement ce qui fait son intérêt.
        if ((REQUEST)data.dwRequestID == REQUEST.CreateTraffic)
        {
            Log($"[injection] appareil créé, objet {data.dwObjectID}");
            TrafficAircraftCreated?.Invoke(data.dwObjectID);
            return;
        }

        if ((REQUEST)data.dwRequestID != REQUEST.CreateInterceptor) return;
        Log($"intercepteur créé, objet {data.dwObjectID}");

        // GEL IMMÉDIAT : tant que le simulateur intègre sa propre physique sur cet appareil,
        // il se bat contre nos écritures de position — d'où des déplacements erratiques.
        // Gelé, il ne bouge plus que là où on le pose.
        FreezeObject(data.dwObjectID, true);

        InterceptorCreated?.Invoke(data.dwObjectID);
    }

    /// <summary>
    /// Gèle (ou dégèle) la mécanique du vol d'un objet. On ne gèle QUE L'ASSIETTE.
    ///
    /// Tout geler — position, altitude, assiette — supprime aussi le mouvement propre de
    /// l'appareil : plus de vitesse, plus d'animation, plus d'interpolation entre nos
    /// écritures. Il ne reste qu'un modèle qu'on déplace, et c'est exactement l'effet
    /// « image qui glisse ». La position et l'altitude restent donc libres : c'est le vecteur
    /// vitesse qu'on lui donne qui l'emmène, et nos corrections par image le maintiennent en
    /// place. Seule l'assiette est figée, sans quoi il partirait en vrille en la subissant.
    /// </summary>
    private void FreezeObject(uint objectId, bool frozen)
    {
        if (_sim is null) return;
        uint value = frozen ? 1u : 0u;

        foreach (var ev in new[] { EVENT.FreezeAttitude })
        {
            try
            {
                _sim.TransmitClientEvent(objectId, ev, value,
                                         NOTIFY_GROUP.Priority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
            catch (COMException ex) { Log($"gel {ev} : {ex.Message}"); }
        }
        Log($"objet {objectId} {(frozen ? "gelé" : "dégelé")}");
    }

    public void SetBeaconLight(bool on)
    {
        _pending.Enqueue(() =>
        {
            if (_sim is null) return;
            try
            {
                _sim.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENT.BeaconSet, on ? 1u : 0u,
                    NOTIFY_GROUP.Priority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
            catch (COMException ex) { Log($"TransmitClientEvent (beacon): {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    /// <summary>
    /// Demande les fréquences COM d'un aéroport au simulateur (facility data). Thread-safe :
    /// la requête est mise en file et exécutée sur le thread de pompage. Ignorée si non
    /// connecté ou si la facility data n'est pas disponible (vieux SDK) -> repli CSV en amont.
    /// </summary>
    public void RequestAirportFrequencies(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return;
        string code = icao.Trim().ToUpperInvariant();

        _pending.Enqueue(() =>
        {
            if (_sim is null || State != ConnectionState.Connected || !_facilityDefReady) return;
            try
            {
                uint id = _nextFacReq++;
                _facIcao[id] = code;
                _facAccum[id] = new List<SimComFrequency>();
                // region vide : recherche par ICAO seul. La réponse arrive en nœuds
                // AIRPORT + FREQUENCY via OnRecvFacilityData, close par OnRecvFacilityDataEnd.
                _sim.RequestFacilityData(DEFINITION.AirportFreqs, (REQUEST)id, code, "");
            }
            catch (COMException ex) { Log($"RequestFacilityData({code}): {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    // Exécute les ordres en attente (envoi d'events) SUR LE THREAD DE POMPE.
    private void DrainPending()
    {
        while (_pending.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Log($"Pending action failed: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ boucle de pompage

    private void PumpLoop()
    {
        // Garde-fou : DLL managée ou native manquante/incompatible -> message clair, pas de crash.
        try
        {
            RunPump();
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                      or DllNotFoundException
                                      or BadImageFormatException
                                      or TypeInitializationException)
        {
            HandleMissingDependency("SimConnect DLL missing or incompatible: " + ex.Message);
        }
        catch (Exception ex)
        {
            // Filet général : un thread de fond qui meurt en silence emporte tout le
            // processus avec lui. On veut au minimum une trace exploitable.
            FileLog.Exception("boucle de pompage SimConnect", ex);
        }
        finally
        {
            // ATTENTION : Teardown() manipule le type MIXTE SimConnect. Quand c'est
            // justement lui qu'on ne peut pas charger, sa compilation JIT relève la même
            // exception — et une exception levée depuis un « finally » échappe à TOUS les
            // catch ci-dessus et tue l'application. C'est exactement le scénario « je lance
            // l'app et rien ne se passe ». D'où ce second filet, indispensable.
            try { Teardown(); }
            catch (Exception ex) { FileLog.Exception("libération de SimConnect", ex); }
        }
    }

    private void RunPump()
    {
        var handles = new WaitHandle[] { _stopSignal, _receiveSignal, _actionSignal };

        while (!_stopRequested)
        {
            if (_sim is null)
            {
                if (!TryConnect())
                {
                    if (State == ConnectionState.MissingDependency) break;
                    SetState(ConnectionState.Waiting, "Waiting for the simulator…");
                    if (_stopSignal.WaitOne(RetryInterval)) break;
                    continue;
                }
            }

            int idx = WaitHandle.WaitAny(handles, TimeSpan.FromMilliseconds(500));
            if (idx == 0) break;      // arrêt demandé

            DrainPending();           // exécute les ordres en attente (envoi d'events) sur ce thread

            if (idx != 1) continue;   // pas un message SimConnect -> reboucle

            try
            {
                _sim?.ReceiveMessage();
            }
            catch (COMException ex)
            {
                Log($"ReceiveMessage failed: {ex.Message}");
                _connectionLost = true;
            }

            if (_connectionLost)
            {
                _connectionLost = false;
                Teardown();
                SetState(ConnectionState.Waiting, "Connection lost. Retrying…");
            }
        }
    }

    private bool TryConnect()
    {
        try
        {
            _sim = new SimConnect("WilcoATC", IntPtr.Zero, WM_USER_SIMCONNECT, _receiveSignal, 0);

            _sim.OnRecvOpen += OnRecvOpen;
            _sim.OnRecvQuit += OnRecvQuit;
            _sim.OnRecvException += OnRecvException;
            _sim.OnRecvSimobjectData += OnRecvSimobjectData;
            _sim.OnRecvSimobjectDataBytype += OnRecvSimobjectDataBytype;
            _sim.OnRecvAirportList += OnRecvAirportList;
            _sim.OnRecvFacilityData += OnRecvFacilityData;
            _sim.OnRecvFacilityDataEnd += OnRecvFacilityDataEnd;
            _sim.OnRecvAssignedObjectId += OnRecvAssignedObjectId;
            return true;
        }
        catch (COMException)
        {
            _sim = null; // simu pas (encore) lancé
            return false;
        }
        catch (DllNotFoundException)
        {
            HandleMissingDependency("Native SimConnect.dll not found next to the executable.");
            return false;
        }
        catch (BadImageFormatException)
        {
            HandleMissingDependency("SimConnect.dll has the wrong architecture (build as x64).");
            return false;
        }
    }

    // ------------------------------------------------------------------ callbacks SimConnect

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        try
        {
            SetupDataDefinitions();
            RequestData();
            _lastRadio = null;   // journalise l'état initial après (re)connexion
            lock (_airportsGate) _airports.Clear();

            // Aéroport le plus proche : on s'abonne au cache d'aéroports du simu.
            // Best-effort : si l'API facilities n'est pas dispo, le reste fonctionne.
            try
            {
                _sim!.SubscribeToFacilities(SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT, REQUEST.AirportList);
            }
            catch (COMException ex)
            {
                Log($"SubscribeToFacilities unavailable: {ex.Message}");
            }

            // Fréquences COM live : définit une fois la structure « facility data » à interroger.
            // Best-effort : sur un SDK trop ancien qui ne connaît pas la facility data, on log et
            // on laisse _facilityDefReady à faux (la couche station retombe alors sur le CSV).
            SetupFacilityDefinition();

            // Mappe l'event beacon (sert à déclencher l'auto-pushback GSX). Best-effort.
            try { _sim!.MapClientEventToSimEvent(EVENT.BeaconSet, "BEACON_LIGHTS_SET"); }
            catch (COMException ex) { Log($"MapClientEvent beacon unavailable: {ex.Message}"); }

            // Gel de la physique d'un objet IA (escorte). Best-effort également : sans ces
            // events, l'escorte reste pilotable mais tressaute — le simulateur la fait
            // tomber et se redresser entre deux écritures de position.
            try
            {
                _sim!.MapClientEventToSimEvent(EVENT.FreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
                _sim!.MapClientEventToSimEvent(EVENT.FreezeAltitude, "FREEZE_ALTITUDE_SET");
                _sim!.MapClientEventToSimEvent(EVENT.FreezeAttitude, "FREEZE_ATTITUDE_SET");
            }
            catch (COMException ex) { Log($"MapClientEvent freeze unavailable: {ex.Message}"); }

            SetState(ConnectionState.Connected, $"Connected to {data.szApplicationName}");
        }
        catch (COMException ex)
        {
            Log($"Failed to set up data definitions: {ex.Message}");
            _connectionLost = true;
        }
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        Log("The simulator closed (OnRecvQuit).");
        _connectionLost = true; // teardown différé hors callback (voir RunPump)
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var ex = (SIMCONNECT_EXCEPTION)data.dwException;
        Log($"SimConnect exception: {ex} (sendID={data.dwSendID}, index={data.dwIndex})");
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        switch ((REQUEST)data.dwRequestID)
        {
            case REQUEST.RadioData:  HandleRadioData((RadioData)data.dwData[0]); break;
            case REQUEST.Context:    HandleContextData((ContextData)data.dwData[0]); break;
            case REQUEST.AircraftId: HandleAircraftId((AircraftIdData)data.dwData[0]); break;
            case REQUEST.AircraftPerf: HandleAircraftPerf((AircraftPerfData)data.dwData[0]); break;
            case REQUEST.Weather:    HandleWeatherData((WeatherData)data.dwData[0]); break;
            case REQUEST.Formation:  HandleFormationData((FormationData)data.dwData[0]); break;
        }
    }

    /// <summary>
    /// Réponse à un relevé PAR TYPE D'OBJET : le simulateur envoie un message PAR APPAREIL
    /// trouvé dans le rayon demandé, numérotés de 1 à <c>dwoutof</c>. Le joueur lui-même en
    /// fait partie — il porte l'identifiant d'objet réservé et on l'écarte, sans quoi son
    /// propre avion viendrait polluer le relevé du trafic.
    /// </summary>
    private void OnRecvSimobjectDataBytype(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
    {
        // Le joueur figure dans le relevé : on l'écarte, sinon son propre appareil viendrait
        // se compter dans le trafic — et l'ATC finirait par s'autoriser lui-même à atterrir.
        if (data.dwObjectID == SimConnect.SIMCONNECT_OBJECT_ID_USER) return;

        switch ((REQUEST)data.dwRequestID)
        {
            case REQUEST.NearbyAircraft:
            {
                var a = (NearbyAircraftData)data.dwData[0];
                string title = a.Title?.Trim() ?? "";
                if (title.Length == 0) return;

                NearbyAircraftSeen?.Invoke(new NearbyAircraft(
                    data.dwObjectID, title, a.AtcType?.Trim() ?? "", a.AtcModel?.Trim() ?? "",
                    a.AtcId?.Trim() ?? "", a.AtcAirline?.Trim() ?? "", a.AtcFlightNumber?.Trim() ?? ""));
                return;
            }

            case REQUEST.NearbyState:
            {
                var s = (NearbyStateData)data.dwData[0];
                NearbyAircraftStateSeen?.Invoke(new NearbyAircraftState(
                    data.dwObjectID, s.Latitude, s.Longitude,
                    s.AltitudeFeet, s.AltitudeAglFeet, s.HeadingTrue,
                    s.GroundSpeedKnots, s.VerticalSpeedFpm, s.OnGround > 0.5));
                return;
            }
        }
    }

    // Le simu envoie les aéroports de son cache (bulle de chargement autour de l'avion),
    // par paquets. On accumule ICAO -> coordonnées ; l'ensemble évolue avec le vol.
    private void OnRecvAirportList(SimConnect sender, SIMCONNECT_RECV_AIRPORT_LIST data)
    {
        foreach (var entry in data.rgData)
        {
            var ap = (SIMCONNECT_DATA_FACILITY_AIRPORT)entry;
            if (!string.IsNullOrWhiteSpace(ap.Ident))
                lock (_airportsGate) _airports[ap.Ident] = (ap.Latitude, ap.Longitude);
        }
    }

    // Réponse « facility data » : chaque nœud FREQUENCY est une fréquence COM. Le nœud AIRPORT
    // parent est ignoré (on n'en veut que les enfants). Tout arrive sur le thread de pompe.
    private void OnRecvFacilityData(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (data.Type != (uint)SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY) return;
        if (!_facAccum.TryGetValue(data.UserRequestId, out var acc)) return;

        var f = (FacilityFrequencyData)data.Data[0];
        acc.Add(new SimComFrequency((f.Name ?? "").Trim(), f.Type, f.FreqHz / 1_000_000.0));
    }

    // Fin d'une réponse : on publie la liste complète des fréquences pour l'ICAO demandé.
    private void OnRecvFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (!_facAccum.Remove(data.RequestId, out var acc)) return;
        _facIcao.Remove(data.RequestId, out var icao);

        // Diagnostic : dump BRUT de ce que le simulateur a renvoyé (AVANT tout filtrage/mapping).
        // Format [name | type | MHz] pour vérifier le mapping TYPE->catégorie et voir ce qui est
        // écarté (NAV 110-117, UHF militaire 200-400). Visible dans Réglages ▸ logs.
        Log($"Facility {icao ?? "?"}: {acc.Count} frequency node(s) from sim [name | type | MHz] — " +
            string.Join(" ; ", acc.Select(f => $"{f.Name} | {f.Type} | {f.Mhz:F3}")));

        AirportFrequenciesReceived?.Invoke(new AirportFacilityFrequencies(icao ?? "", acc));
    }

    // Décrit UNE FOIS par connexion la structure « facility data » à interroger (fréquences COM
    // d'un aéroport) et enregistre les structs de marshalling. Best-effort : sur un SDK trop
    // ancien, la COMException laisse _facilityDefReady à faux et la couche station reste au CSV.
    private void SetupFacilityDefinition()
    {
        _facilityDefReady = false;
        _facIcao.Clear();
        _facAccum.Clear();
        _nextFacReq = 1000;

        try
        {
            var sim = _sim!;
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "OPEN AIRPORT");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "LATITUDE");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "LONGITUDE");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "OPEN FREQUENCY");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "TYPE");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "FREQUENCY");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "NAME");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "CLOSE FREQUENCY");
            sim.AddToFacilityDefinition(DEFINITION.AirportFreqs, "CLOSE AIRPORT");
            sim.RegisterFacilityDataDefineStruct<FacilityAirportData>(SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
            sim.RegisterFacilityDataDefineStruct<FacilityFrequencyData>(SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY);
            _facilityDefReady = true;
        }
        catch (COMException ex)
        {
            Log($"Facility data (live COM freqs) unavailable: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ définitions & requêtes

    private void SetupDataDefinitions()
    {
        var sim = _sim!;

        // --- RADIO : unité "Hz" (FLOAT64). Ordre = struct RadioData. ---
        sim.AddToDataDefinition(DEFINITION.RadioData, "COM ACTIVE FREQUENCY:1",  "Hz",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.RadioData, "COM STANDBY FREQUENCY:1", "Hz",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.RadioData, "COM ACTIVE FREQUENCY:2",  "Hz",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.RadioData, "COM STANDBY FREQUENCY:2", "Hz",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.RadioData, "COM TRANSMIT:1",          "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.RadioData, "COM TRANSMIT:2",          "Bool", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<RadioData>(DEFINITION.RadioData);

        // --- CONTEXTE : position, altitudes, vitesses, cap, squawk. Ordre = struct ContextData. ---
        sim.AddToDataDefinition(DEFINITION.Context, "PLANE LATITUDE",             "degrees",         SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "PLANE LONGITUDE",            "degrees",         SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "PLANE ALTITUDE",             "feet",            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "PLANE ALT ABOVE GROUND",     "feet",            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "PLANE HEADING DEGREES TRUE", "degrees",         SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "AIRSPEED INDICATED",         "knots",           SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "GROUND VELOCITY",            "knots",           SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "VERTICAL SPEED",             "feet per minute", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "SIM ON GROUND",              "Bool",            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "TRANSPONDER CODE:1",         "BCO16",           SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Context, "BRAKE PARKING INDICATOR",    "Bool",            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        // CAMERA STATE : distingue « dans l'avion / en vol » (2-10) du menu principal / carte
        // du monde / chargement (≥ 11). Sert à faire TAIRE l'ATC hors du cockpit.
        sim.AddToDataDefinition(DEFINITION.Context, "CAMERA STATE",               "Number",          SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<ContextData>(DEFINITION.Context);

        // --- IDENTITÉ AVION : variables CHAÎNE. Unité = null pour les strings. ---
        sim.AddToDataDefinition(DEFINITION.AircraftId, "TITLE",     null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftId, "ATC TYPE",  null, SIMCONNECT_DATATYPE.STRING64,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftId, "ATC MODEL", null, SIMCONNECT_DATATYPE.STRING64,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftId, "ATC ID",    null, SIMCONNECT_DATATYPE.STRING32,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<AircraftIdData>(DEFINITION.AircraftId);

        // --- GABARIT AVION : variables NUMÉRIQUES. Ordre = struct AircraftPerfData. ---
        sim.AddToDataDefinition(DEFINITION.AircraftPerf, "ENGINE TYPE",         "Enum",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftPerf, "NUMBER OF ENGINES",   "Number", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftPerf, "MAX GROSS WEIGHT",    "pounds", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftPerf, "IS GEAR RETRACTABLE", "Bool",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftPerf, "DESIGN CRUISE ALT",   "feet",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<AircraftPerfData>(DEFINITION.AircraftPerf);

        // --- MÉTÉO : conditions ambiantes + heure zoulou (ATIS). Ordre = struct WeatherData. ---
        // Définition ISOLÉE des autres, volontairement : si un simulateur ne connaissait pas
        // l'une de ces SimVars, seul l'ATIS en souffrirait — la radio et le contexte de vol,
        // dont dépend tout le reste de l'app, continueraient d'arriver normalement.
        sim.AddToDataDefinition(DEFINITION.Weather, "AMBIENT WIND DIRECTION", "degrees",    SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Weather, "AMBIENT WIND VELOCITY",  "knots",      SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Weather, "AMBIENT TEMPERATURE",    "celsius",    SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Weather, "AMBIENT VISIBILITY",     "meters",     SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Weather, "SEA LEVEL PRESSURE",     "millibars",  SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Weather, "MAGVAR",                 "degrees",    SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Weather, "AMBIENT PRECIP STATE",   "mask",       SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Weather, "ZULU TIME",              "seconds",    SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<WeatherData>(DEFINITION.Weather);

        // ÉCRITURE : position imposée à l'intercepteur. Même ordre que AiPositionData.
        sim.AddToDataDefinition(DEFINITION.AiPosition, "PLANE LATITUDE",             "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "PLANE LONGITUDE",            "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "PLANE ALTITUDE",             "feet",    SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "PLANE PITCH DEGREES",        "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "PLANE BANK DEGREES",         "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "SIM ON GROUND",              "bool",    SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "AIRSPEED TRUE",              "knots",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "VELOCITY WORLD X",           "feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "VELOCITY WORLD Y",           "feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AiPosition, "VELOCITY WORLD Z",           "feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<AiPositionData>(DEFINITION.AiPosition);

        // FORMATION : état du joueur par image, demandé seulement pendant une escorte.
        sim.AddToDataDefinition(DEFINITION.Formation, "PLANE LATITUDE",             "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "PLANE LONGITUDE",            "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "PLANE ALTITUDE",             "feet",    SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "PLANE HEADING DEGREES TRUE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "PLANE PITCH DEGREES",        "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "PLANE BANK DEGREES",         "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "AIRSPEED TRUE",              "knots",   SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "VELOCITY WORLD X",           "feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "VELOCITY WORLD Y",           "feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.Formation, "VELOCITY WORLD Z",           "feet per second", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<FormationData>(DEFINITION.Formation);

        // Identité des appareils environnants — uniquement des chaînes (voir NearbyAircraftData).
        sim.AddToDataDefinition(DEFINITION.NearbyAircraft, "TITLE",             null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyAircraft, "ATC TYPE",          null, SIMCONNECT_DATATYPE.STRING64,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyAircraft, "ATC MODEL",         null, SIMCONNECT_DATATYPE.STRING64,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyAircraft, "ATC ID",            null, SIMCONNECT_DATATYPE.STRING32,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyAircraft, "ATC AIRLINE",       null, SIMCONNECT_DATATYPE.STRING64,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyAircraft, "ATC FLIGHT NUMBER", null, SIMCONNECT_DATATYPE.STRING32,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<NearbyAircraftData>(DEFINITION.NearbyAircraft);

        // État de vol des mêmes appareils (définition séparée — voir NearbyStateData).
        sim.AddToDataDefinition(DEFINITION.NearbyState, "PLANE LATITUDE",            "degrees",         SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyState, "PLANE LONGITUDE",           "degrees",         SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyState, "PLANE ALTITUDE",            "feet",            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyState, "PLANE ALT ABOVE GROUND",    "feet",            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyState, "PLANE HEADING DEGREES TRUE","degrees",         SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyState, "GROUND VELOCITY",           "knots",           SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyState, "VERTICAL SPEED",            "feet per minute", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.NearbyState, "SIM ON GROUND",             "Bool",            SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<NearbyStateData>(DEFINITION.NearbyState);
    }

    private void RequestData()
    {
        var sim = _sim!;

        // RADIO : SIM_FRAME + CHANGED -> envoi uniquement au changement (détection instantanée).
        sim.RequestDataOnSimObject(
            REQUEST.RadioData, DEFINITION.RadioData, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);

        // CONTEXTE : 1 Hz, sans CHANGED (position/vitesses varient en continu).
        sim.RequestDataOnSimObject(
            REQUEST.Context, DEFINITION.Context, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

        // IDENTITÉ : 1 Hz + CHANGED -> envoyée à la connexion puis seulement si l'avion change.
        sim.RequestDataOnSimObject(
            REQUEST.AircraftId, DEFINITION.AircraftId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);

        // GABARIT : même cadence + CHANGED. Ces valeurs sont fixes pour un appareil donné,
        // elles ne bougent qu'au changement d'avion.
        sim.RequestDataOnSimObject(
            REQUEST.AircraftPerf, DEFINITION.AircraftPerf, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);

        // MÉTÉO : toutes les 5 s (intervalle en unités de période). Sans CHANGED : le vent
        // fluctue en permanence, le flag ne filtrerait rien tout en risquant de nous priver
        // du premier envoi.
        sim.RequestDataOnSimObject(
            REQUEST.Weather, DEFINITION.Weather, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 5, 0);
    }

    // ------------------------------------------------------------------ traitement des données

    private void HandleRadioData(RadioData d)
    {
        if (_lastRadio is RadioData prev)
        {
            EmitFreqChange("COM1", "ACTIVE",  prev.Com1ActiveHz,  d.Com1ActiveHz);
            EmitFreqChange("COM1", "STANDBY", prev.Com1StandbyHz, d.Com1StandbyHz);
            EmitFreqChange("COM2", "ACTIVE",  prev.Com2ActiveHz,  d.Com2ActiveHz);
            EmitFreqChange("COM2", "STANDBY", prev.Com2StandbyHz, d.Com2StandbyHz);
            EmitTxChange("COM1", prev.Com1Transmit, d.Com1Transmit);
            EmitTxChange("COM2", prev.Com2Transmit, d.Com2Transmit);
        }
        else
        {
            RadioChanged?.Invoke(new RadioChange("COM1", "ACTIVE", FrequencyFormatter.FormatMHz(d.Com1ActiveHz), RadioChangeKind.Initial));
            RadioChanged?.Invoke(new RadioChange("COM2", "ACTIVE", FrequencyFormatter.FormatMHz(d.Com2ActiveHz), RadioChangeKind.Initial));
        }

        _lastRadio = d;

        RadioSnapshotReceived?.Invoke(new RadioSnapshot(
            d.Com1ActiveHz, d.Com1StandbyHz, d.Com2ActiveHz, d.Com2StandbyHz,
            d.Com1Transmit > 0.5, d.Com2Transmit > 0.5));
    }

    private void EmitFreqChange(string radio, string field, double oldHz, double newHz)
    {
        if (FrequencyFormatter.SameChannel(oldHz, newHz)) return;
        RadioChanged?.Invoke(new RadioChange(radio, field, FrequencyFormatter.FormatMHz(newHz), RadioChangeKind.Frequency));
    }

    private void EmitTxChange(string radio, double oldTx, double newTx)
    {
        bool o = oldTx > 0.5, n = newTx > 0.5;
        if (o == n) return;
        RadioChanged?.Invoke(new RadioChange(radio, "TX", n ? "ON" : "OFF", RadioChangeKind.Transmit));
    }

    private void HandleContextData(ContextData d)
    {
        // Aéroport le plus proche à partir du cache d'aéroports (si peuplé).
        string? icao = FindNearestAirport(d.Latitude, d.Longitude, out double distMeters);

        // « En vol » = caméra dans le monde (cockpit/externe/drone : 2-10). Aux valeurs ≥ 11
        // (carte du monde, menu, écran « prêt à voler », chargement) on n'est PAS aux commandes.
        bool inFlight = d.CameraState >= 2 && d.CameraState <= 10;

        ContextReceived?.Invoke(new ContextSnapshot(
            d.Latitude, d.Longitude, d.AltitudeMslFeet, d.AltitudeAglFeet,
            d.HeadingTrueDeg, d.IasKnots, d.GroundSpeedKnots, d.VerticalSpeedFpm,
            d.OnGround > 0.5, d.ParkingBrake > 0.5, TransponderFormatter.ToCode(d.TransponderBcd),
            icao, distMeters, inFlight));
    }

    private void HandleFormationData(FormationData d)
    {
        // Les angles SimConnect sont en RADIANS pour pitch/bank : la définition les demande
        // en degrés, donc rien à convertir ici. Le signe du tangage suit la convention du
        // simulateur (positif = nez bas), on le retourne pour rester lisible côté escorte.
        FormationTick?.Invoke(new FormationSnapshot(
            d.Latitude, d.Longitude, d.AltitudeFeet,
            d.HeadingTrue, -d.PitchDeg, d.BankDeg, d.AirspeedTrue,
            d.VelocityEast, d.VelocityUp, d.VelocityNorth));
    }

    private void HandleWeatherData(WeatherData d)
    {
        WeatherReceived?.Invoke(new WeatherSnapshot(
            WindDirectionTrueDeg: d.WindDirectionDeg,
            WindSpeedKnots: d.WindSpeedKnots,
            TemperatureC: d.TemperatureC,
            VisibilityMeters: d.VisibilityMeters,
            SeaLevelPressureHpa: d.SeaLevelPressureMb,
            MagneticVariationDeg: d.MagVarDeg,
            Precipitation: DecodePrecip(d.PrecipState),
            ZuluTime: TimeSpan.FromSeconds(Math.Clamp(d.ZuluTimeSeconds, 0, 86_399))));
    }

    // « AMBIENT PRECIP STATE » est un MASQUE du SDK : 2 = aucune, 4 = pluie, 8 = neige.
    // Toute autre valeur -> Unknown, et l'ATIS n'annonce simplement pas de précipitation :
    // se taire vaut mieux qu'annoncer de la neige sur un malentendu.
    private static PrecipKind DecodePrecip(double raw) => (int)Math.Round(raw) switch
    {
        2 => PrecipKind.None,
        4 => PrecipKind.Rain,
        8 => PrecipKind.Snow,
        _ => PrecipKind.Unknown,
    };

    private string? FindNearestAirport(double lat, double lon, out double bestMeters)
    {
        string? best = null;
        bestMeters = double.MaxValue;
        lock (_airportsGate)
        {
            foreach (var kv in _airports)
            {
                double d = Geo.DistanceMeters(lat, lon, kv.Value.Lat, kv.Value.Lon);
                if (d < bestMeters) { bestMeters = d; best = kv.Key; }
            }
        }
        return best;
    }

    // Identité (chaînes) et gabarit (numérique) arrivent par DEUX définitions distinctes,
    // chacune avec son propre flag CHANGED : elles ne tombent donc pas au même instant. On
    // garde le dernier état de chacune et on ré-émet l'instantané COMPLET dès que l'une
    // bouge — sinon un abonné pourrait ne voir que la moitié de l'avion.
    // Lus/écrits uniquement sur le thread de pompage -> pas de verrou.
    private AircraftIdData? _lastAircraftId;
    private AircraftPerfData? _lastAircraftPerf;

    private void HandleAircraftId(AircraftIdData d)
    {
        _lastAircraftId = d;
        EmitAircraft();
    }

    private void HandleAircraftPerf(AircraftPerfData d)
    {
        _lastAircraftPerf = d;
        EmitAircraft();
    }

    private void EmitAircraft()
    {
        if (_lastAircraftId is not AircraftIdData id) return; // l'identité fait foi
        var p = _lastAircraftPerf;

        AircraftReceived?.Invoke(new AircraftSnapshot(
            AircraftFormatter.Clean(id.Title),
            AircraftFormatter.Clean(id.AtcType),
            AircraftFormatter.Clean(id.AtcModel),
            AircraftFormatter.Clean(id.AtcId),
            Engine: p is null ? EngineKind.Unknown : ToEngineKind(p.Value.EngineType),
            EngineCount: (int)(p?.NumberOfEngines ?? 0),
            MaxGrossWeightLbs: p?.MaxGrossWeightLbs ?? 0,
            GearRetractable: (p?.GearRetractable ?? 0) > 0.5,
            DesignCruiseAltFeet: p?.DesignCruiseAlt ?? 0));
    }

    // Valeur hors nomenclature -> Unknown plutôt qu'un cast aveugle : le classement retombe
    // alors sur la masse seule, ce qui reste exploitable.
    private static EngineKind ToEngineKind(double raw)
    {
        int v = (int)Math.Round(raw);
        return Enum.IsDefined(typeof(EngineKind), v) && v >= 0 ? (EngineKind)v : EngineKind.Unknown;
    }

    // ------------------------------------------------------------------ utilitaires

    private void Teardown()
    {
        lock (_gate)
        {
            if (_sim is not null)
            {
                try
                {
                    _sim.OnRecvOpen -= OnRecvOpen;
                    _sim.OnRecvQuit -= OnRecvQuit;
                    _sim.OnRecvException -= OnRecvException;
                    _sim.OnRecvSimobjectData -= OnRecvSimobjectData;
                    _sim.OnRecvAirportList -= OnRecvAirportList;
                    _sim.OnRecvFacilityData -= OnRecvFacilityData;
                    _sim.OnRecvFacilityDataEnd -= OnRecvFacilityDataEnd;
                    _sim.Dispose();
                }
                catch { /* on jette l'objet de toute façon */ }
                _sim = null;
            }
            _lastRadio = null;
            _lastAircraftId = null;
            _lastAircraftPerf = null;
            lock (_airportsGate) _airports.Clear();
            _facilityDefReady = false;
            _facIcao.Clear();
            _facAccum.Clear();
        }
    }

    private void SetState(ConnectionState state, string? detail)
    {
        // On ne journalise que les CHANGEMENTS : sinon « en attente du simulateur »
        // remplirait le fichier à chaque tentative de reconnexion.
        if (state != State) Log($"état : {state}" + (detail is null ? "" : $" — {detail}"));

        State = state;
        StatusDetail = detail;
        StateChanged?.Invoke(state, detail);
    }

    private void HandleMissingDependency(string detail)
    {
        // Cette méthode ne DOIT mentionner aucun type SimConnect : quand c'est justement lui
        // qui manque, sa seule compilation JIT relèverait l'exception — et on perdrait le
        // message qu'on essaie précisément d'écrire. Le nettoyage du handle est donc isolé
        // dans une méthode à part, appelée sous garde.
        FileLog.Write("DÉPENDANCE MANQUANTE : " + detail);
        FileLog.Write("    -> vérifier que SimConnect.dll et Microsoft.FlightSimulator.SimConnect.dll");
        FileLog.Write("       sont à côté de WilcoATC.exe, et que l'exécutable est bien en x64.");
        SetState(ConnectionState.MissingDependency, detail);

        try { ReleaseSimHandle(); }
        catch (Exception ex) { FileLog.Write("    (handle SimConnect non libérable : " + ex.GetType().Name + ")"); }
    }

    /// <summary>Isole la seule écriture du champ typé SimConnect (cf. HandleMissingDependency).</summary>
    private void ReleaseSimHandle()
    {
        lock (_gate) _sim = null;
    }

    private static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[WilcoATC/Sim] {message}");
        FileLog.Write("[Sim] " + message);
    }
}
