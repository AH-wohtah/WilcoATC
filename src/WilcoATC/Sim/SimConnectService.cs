using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FreqWatch.Common;
using FreqWatch.Formatting;
using Microsoft.FlightSimulator.SimConnect;

namespace FreqWatch.Sim;

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
    // Lu et écrit uniquement sur le thread de pompage -> pas de verrou nécessaire.
    private readonly Dictionary<string, (double Lat, double Lon)> _airports = new(StringComparer.OrdinalIgnoreCase);

    public ConnectionState State { get; private set; } = ConnectionState.Waiting;
    public string? StatusDetail { get; private set; } = "En attente du simulateur…";

    public event Action<ConnectionState, string?>? StateChanged;
    public event Action<RadioSnapshot>? RadioSnapshotReceived;
    public event Action<RadioChange>? RadioChanged;
    public event Action<ContextSnapshot>? ContextReceived;
    public event Action<AircraftSnapshot>? AircraftReceived;

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
            catch (COMException ex) { Log($"TransmitClientEvent (beacon) : {ex.Message}"); }
        });
        _actionSignal.Set();
    }

    // Exécute les ordres en attente (envoi d'events) SUR LE THREAD DE POMPE.
    private void DrainPending()
    {
        while (_pending.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Log($"Action en attente échouée : {ex.Message}"); }
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
            HandleMissingDependency("DLL SimConnect introuvable ou incompatible : " + ex.Message);
        }
        finally
        {
            Teardown();
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
                    SetState(ConnectionState.Waiting, "En attente du simulateur…");
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
                Log($"ReceiveMessage a échoué : {ex.Message}");
                _connectionLost = true;
            }

            if (_connectionLost)
            {
                _connectionLost = false;
                Teardown();
                SetState(ConnectionState.Waiting, "Connexion perdue. Nouvelle tentative…");
            }
        }
    }

    private bool TryConnect()
    {
        try
        {
            _sim = new SimConnect("FreqWatch", IntPtr.Zero, WM_USER_SIMCONNECT, _receiveSignal, 0);

            _sim.OnRecvOpen += OnRecvOpen;
            _sim.OnRecvQuit += OnRecvQuit;
            _sim.OnRecvException += OnRecvException;
            _sim.OnRecvSimobjectData += OnRecvSimobjectData;
            _sim.OnRecvAirportList += OnRecvAirportList;
            return true;
        }
        catch (COMException)
        {
            _sim = null; // simu pas (encore) lancé
            return false;
        }
        catch (DllNotFoundException)
        {
            HandleMissingDependency("SimConnect.dll (natif) introuvable à côté de l'exécutable.");
            return false;
        }
        catch (BadImageFormatException)
        {
            HandleMissingDependency("SimConnect.dll a une architecture incompatible (compiler en x64).");
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
            _airports.Clear();

            // Aéroport le plus proche : on s'abonne au cache d'aéroports du simu.
            // Best-effort : si l'API facilities n'est pas dispo, le reste fonctionne.
            try
            {
                _sim!.SubscribeToFacilities(SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT, REQUEST.AirportList);
            }
            catch (COMException ex)
            {
                Log($"SubscribeToFacilities indisponible : {ex.Message}");
            }

            // Mappe l'event beacon (sert à déclencher l'auto-pushback GSX). Best-effort.
            try { _sim!.MapClientEventToSimEvent(EVENT.BeaconSet, "BEACON_LIGHTS_SET"); }
            catch (COMException ex) { Log($"MapClientEvent beacon indisponible : {ex.Message}"); }

            SetState(ConnectionState.Connected, $"Connecté à {data.szApplicationName}");
        }
        catch (COMException ex)
        {
            Log($"Échec de configuration des données : {ex.Message}");
            _connectionLost = true;
        }
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        Log("Le simulateur s'est fermé (OnRecvQuit).");
        _connectionLost = true; // teardown différé hors callback (voir RunPump)
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var ex = (SIMCONNECT_EXCEPTION)data.dwException;
        Log($"Exception SimConnect : {ex} (sendID={data.dwSendID}, index={data.dwIndex})");
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        switch ((REQUEST)data.dwRequestID)
        {
            case REQUEST.RadioData:  HandleRadioData((RadioData)data.dwData[0]); break;
            case REQUEST.Context:    HandleContextData((ContextData)data.dwData[0]); break;
            case REQUEST.AircraftId: HandleAircraftId((AircraftIdData)data.dwData[0]); break;
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
                _airports[ap.Ident] = (ap.Latitude, ap.Longitude);
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
        sim.RegisterDataDefineStruct<ContextData>(DEFINITION.Context);

        // --- IDENTITÉ AVION : variables CHAÎNE. Unité = null pour les strings. ---
        sim.AddToDataDefinition(DEFINITION.AircraftId, "TITLE",     null, SIMCONNECT_DATATYPE.STRING256, 0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftId, "ATC TYPE",  null, SIMCONNECT_DATATYPE.STRING64,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftId, "ATC MODEL", null, SIMCONNECT_DATATYPE.STRING64,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.AddToDataDefinition(DEFINITION.AircraftId, "ATC ID",    null, SIMCONNECT_DATATYPE.STRING32,  0f, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<AircraftIdData>(DEFINITION.AircraftId);
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

        ContextReceived?.Invoke(new ContextSnapshot(
            d.Latitude, d.Longitude, d.AltitudeMslFeet, d.AltitudeAglFeet,
            d.HeadingTrueDeg, d.IasKnots, d.GroundSpeedKnots, d.VerticalSpeedFpm,
            d.OnGround > 0.5, d.ParkingBrake > 0.5, TransponderFormatter.ToCode(d.TransponderBcd),
            icao, distMeters));
    }

    private string? FindNearestAirport(double lat, double lon, out double bestMeters)
    {
        string? best = null;
        bestMeters = double.MaxValue;
        foreach (var kv in _airports)
        {
            double d = Geo.DistanceMeters(lat, lon, kv.Value.Lat, kv.Value.Lon);
            if (d < bestMeters) { bestMeters = d; best = kv.Key; }
        }
        return best;
    }

    private void HandleAircraftId(AircraftIdData d)
    {
        AircraftReceived?.Invoke(new AircraftSnapshot(
            AircraftFormatter.Clean(d.Title),
            AircraftFormatter.Clean(d.AtcType),
            AircraftFormatter.Clean(d.AtcModel),
            AircraftFormatter.Clean(d.AtcId)));
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
                    _sim.Dispose();
                }
                catch { /* on jette l'objet de toute façon */ }
                _sim = null;
            }
            _lastRadio = null;
            _airports.Clear();
        }
    }

    private void SetState(ConnectionState state, string? detail)
    {
        State = state;
        StatusDetail = detail;
        StateChanged?.Invoke(state, detail);
    }

    private void HandleMissingDependency(string detail)
    {
        _sim = null;
        SetState(ConnectionState.MissingDependency, detail);
    }

    private static void Log(string message)
        => System.Diagnostics.Debug.WriteLine($"[FreqWatch/Sim] {message}");
}
