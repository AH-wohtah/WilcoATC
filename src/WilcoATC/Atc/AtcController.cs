using WilcoATC.Atc.Brain;
using WilcoATC.Atc.Context;
using WilcoATC.Atc.Enroute;
using WilcoATC.Atc.GroundServices;
using WilcoATC.Atc.Localization;
using WilcoATC.Atc.Planning;
using WilcoATC.Atc.Understanding;
using WilcoATC.Atc.Vatsim;
using WilcoATC.Audio;
using WilcoATC.Common;
using WilcoATC.Formatting;
using WilcoATC.Localization;
using WilcoATC.Settings;
using WilcoATC.Sim;
using WilcoATC.Stations;

namespace WilcoATC.Atc;

/// <summary>
/// Cœur de la boucle vocale. S'abonne à la couche SimConnect (SANS la modifier),
/// agrège un <see cref="FlightSnapshot"/>, et déclenche des transmissions :
///  - AUTO : quand le joueur se cale sur la fréquence d'une station connue (résolue
///    via <see cref="IStationResolver"/>) — un contact initial, une seule fois par station ;
///  - MANUEL : bouton / touche de test.
///
/// Une seule transmission à la fois (SemaphoreSlim). Un petit délai aléatoire évite
/// l'effet « réponse instantanée robotique ». Rien ne bloque l'UI (tout est async).
/// </summary>
public sealed class AtcController : IAtcController
{
    private readonly ISimConnectService _sim;
    private readonly IStationResolver _stations;
    private readonly IAtcLineGenerator _generator;
    private readonly ITtsEngine _tts;
    private readonly VoiceBus _voice;
    private readonly VoicePicker _picker;
    private readonly SettingsService _settings;
    private readonly IIntentRecognizer _intents;
    private readonly AtcBrain _brain;
    private readonly FlightContextProvider _flightContext;
    private readonly CallsignFormatter _callsigns;
    private readonly IGroundServices _groundServices;
    private readonly FlightPlanStore _plans;
    private readonly ControllerSequencer _sequencer = new();
    private readonly VatsimClient _vatsim = new();

    /// <summary>Pistes publiées : leurs SEUILS localisent le point d'arrêt (cf. <see cref="CheckHoldingPoint"/>).</summary>
    private readonly RunwayRepository _runways;

    /// <summary>
    /// Secteurs de contrôle en-route (ACC). Vide tant que l'utilisateur ne les a pas
    /// installés : les replis habituels (données aéroport, VATSIM, fréquence saisie)
    /// s'appliquent alors comme avant.
    /// </summary>
    private readonly EnrouteSectorRepository _sectors;

    /// <summary>
    /// Fait venir un chasseur quand le pilote ne répond plus. Optionnel : null en l'absence
    /// de simulateur (bancs d'essai), et sans effet tant que l'option est coupée.
    /// </summary>
    private readonly Intercept.InterceptDirector? _intercept;

    private readonly Random _rng = new();

    private RadioSnapshot? _radio;
    private ContextSnapshot? _context;
    private AircraftSnapshot? _aircraft;
    private string? _lastStationKey;
    private string? _lastStationName; // station courante -> détermine la VOIX du contrôleur

    /// <summary>
    /// Décide de la langue du contrôleur. On lui pousse deux choses : le TERRAIN courant
    /// (d'où la langue du pays) et la LANGUE DU PILOTE dès qu'on la reconnaît.
    /// </summary>
    private readonly LanguageResolver _language;
    private ControllerType _lastStationController = ControllerType.Unknown; // -> caractère de la voix

    // Anti-rebond du changement de fréquence (on ne salue pas en tournant le bouton).
    private const double FrequencySettleSeconds = 3;
    private string? _pendingFreqKey;
    private DateTime _pendingSince;

    // Séquence des contrôleurs : amorcée au premier instantané (cf. RunSequencer).
    private bool _sequencerSeeded;

    // Rayon de recherche d'une fréquence Centre : un secteur en-route couvre large.
    private const double CenterSearchRadiusKm = 250;

    // Cache de l'aéroport contrôlé le plus proche (balayage coûteux, résultat lent à bouger).
    private const double NearestCacheSeconds = 15;
    private string? _nearestIcao;
    private DateTime _nearestAt;
    private bool _nearestIncludedSmall;

    // « Aux commandes » : vrai seulement quand la caméra est dans le cockpit / en vol (pas au
    // menu principal ni sur la carte du monde). L'ATC ne parle JAMAIS hors de ce contexte.
    private bool _inFlightSession;

    /// <summary>
    /// Le pilote a-t-il ENGAGÉ son arrivée ? Vrai dès qu'il se signale en approche, annonce
    /// le terrain en vue, ou demande une descente. Tant que c'est faux, l'ATC n'offre ni
    /// approche ni autorisation d'atterrissage : ce sont des réponses, pas des initiatives.
    /// Écrit depuis la boucle pilote, lu depuis les événements du simulateur -> volatile.
    /// </summary>
    private volatile bool _pilotAskedForArrival;

    // ATC proactif : suivi des transitions de phase + état d'autorisation de décollage.
    private FlightPhase _lastDirectorPhase = FlightPhase.Unknown;
    private bool _takeoffCleared;

    /// <summary>
    /// L'atterrissage a-t-il déjà été autorisé, sur report de finale ? Empêche l'annonce
    /// automatique — déclenchée au TOUCHER, donc toujours en retard — de redonner une
    /// clairance que le pilote a déjà reçue et collationnée.
    /// </summary>
    private bool _landingCleared;

    /// <summary>
    /// UNE URGENCE EST-ELLE DÉCLARÉE ? Vrai depuis « mayday » ou « pan pan », faux après
    /// annulation, atterrissage ou nouveau vol.
    ///
    /// Ce n'est pas qu'une phrase de plus : c'est un CHANGEMENT DE RÉGIME du contrôleur.
    /// Un pilote qui gère un feu moteur n'a ni le temps ni les mains libres pour collationner
    /// une clairance, et le harceler serait au mieux ridicule, au pire dangereux. Le
    /// collationnement cesse donc d'être réclamé, et l'escalade « panne radio » — qui finit
    /// par faire décoller une patrouille — est désarmée : le silence d'un appareil en détresse
    /// n'a rien d'une perte de communication.
    /// </summary>
    private volatile bool _emergency;

    /// <summary>Détresse (mayday) plutôt qu'urgence (pan pan) : seule la détresse dégage la fréquence.</summary>
    private volatile bool _distress;

    /// <summary>
    /// Le vol est-il en détresse ? Lu par le trafic et l'ambiance, qui se taisent alors : sur
    /// une vraie fréquence, le contrôleur impose le silence à tout le monde et garde le canal
    /// pour l'appareil en difficulté.
    /// </summary>
    public bool RadioSilenceForDistress => _distress;
    private bool _forceCleared; // override DEBUG (Mode Test) : traite le décollage comme autorisé
    private readonly HashSet<string> _announced = new();

    public event Action<string>? TransmissionText;
    public event Action<string>? StatusChanged;
    public event Action<string>? PilotTranscript;
    public event Action<RecognizedIntent>? IntentRecognized;
    public event Action<AtcDecision>? DecisionMade;
    public event Action<FlightPhaseDebug>? PhaseChanged;
    public event Action<bool>? ExpectingReadbackChanged;

    // Collationnement : mots que l'ATC vient de dire, requêtes déjà accordées (par vol).
    private readonly object _stateLock = new();
    private bool _expectingReadback;
    private string _lastAtcWords = "";

    /// <summary>Quand l'ATC a prononcé <see cref="_lastAtcWords"/> — voir <see cref="ReadbackRelevance"/>.</summary>
    private DateTime _lastAtcWordsUtc = DateTime.MinValue;

    /// <summary>
    /// Demande du pilote à l'origine de l'instruction en attente de collationnement. Le
    /// contrôleur n'accuse pas une relecture de la même façon selon ce qu'il vient
    /// d'autoriser : après une clairance de départ, il enchaîne sur le passage au Sol.
    /// </summary>
    private PilotIntent _awaitedReadbackOf = PilotIntent.Unknown;

    /// <summary>
    /// L'instruction en attente est-elle un PASSAGE DE MAIN (« rappelez prêt au repoussage
    /// avec le Sol sur 110.100 ») ? Le pilote la relit — c'est même pour cela qu'on l'attend —
    /// mais le contrôleur, lui, ne répond plus rien : il a fini avec ce vol. Sans ce drapeau,
    /// « Brussels Ground on 110.100, Beeline 3633, thank you » se voyait répondre un
    /// « readback correct » de trop, à l'infini si le pilote relisait encore.
    /// </summary>
    private bool _awaitedReadbackIsHandoff;

    /// <summary>
    /// Durée pendant laquelle une instruction reste collationnable. Assez longue pour qu'une
    /// relecture tardive compte encore — le pilote peut être occupé — et assez courte pour
    /// qu'elle ne contamine pas la suite du vol.
    ///
    /// CINQ MINUTES, et non deux : avec vingt secondes de silence avant chaque relance, la
    /// séquence complète — clairance puis trois rappels — approche déjà la centaine de
    /// secondes. À deux minutes, un pilote qui répond enfin après la dernière relance
    /// trouvait la fenêtre presque close, et son collationnement était traité comme une
    /// requête nouvelle. Les deux durées doivent bouger ensemble.
    /// </summary>
    private static readonly TimeSpan ReadbackRelevance = TimeSpan.FromMinutes(5);

    /// <summary>Dernière parole du pilote. C'est elle qui interrompt l'escalade militaire.</summary>
    private DateTime _lastPilotUtc = DateTime.MinValue;
    private readonly HashSet<PilotIntent> _granted = new();

    public bool Enabled
    {
        get => _settings.Current.AtcEnabled;
        set { _settings.Current.AtcEnabled = value; _settings.Save(); }
    }

    public bool TestMode
    {
        get => _settings.Current.TestMode;
        set { _settings.Current.TestMode = value; _settings.Save(); }
    }

    public AtcController(
        ISimConnectService sim, IStationResolver stations, IAtcLineGenerator generator,
        ITtsEngine tts, VoiceBus voice, VoicePicker picker, SettingsService settings,
        IIntentRecognizer intents, AtcBrain brain, FlightContextProvider flightContext,
        CallsignFormatter callsigns, IGroundServices groundServices, FlightPlanStore plans,
        LanguageResolver language, EnrouteSectorRepository sectors,
        RunwayRepository runways, Intercept.InterceptDirector? intercept = null)
    {
        _runways = runways;
        _language = language;
        _sectors = sectors;
        _intercept = intercept;
        _sim = sim;
        _stations = stations;
        _generator = generator;
        _tts = tts;
        _voice = voice;
        _picker = picker;
        _settings = settings;
        _intents = intents;
        _brain = brain;
        _flightContext = flightContext;
        _callsigns = callsigns;
        _groundServices = groundServices;
        _plans = plans;
    }

    public void Start()
    {
        _sim.RadioSnapshotReceived += OnRadio;
        _sim.ContextReceived += OnContext;
        // Le vent désigne la piste en service : sans lui, on retomberait sur le cap de l'avion.
        _sim.WeatherReceived += _flightContext.OnWeather;
        _sim.AircraftReceived += OnAircraft;
        _sim.StateChanged += OnState;
        SetStatus("Ready");
    }

    // ------------------------------------------------------------------ événements sim

    private void OnState(ConnectionState state, string? detail)
    {
        if (state != ConnectionState.Connected)
        {
            _inFlightSession = false;
            _lastStationKey = null;      // ré-armer après reconnexion
            _lastStationName = null;
            _lastStationController = ControllerType.Unknown;
            _pendingFreqKey = null;
            _flightContext.Reset();
            _lastDirectorPhase = FlightPhase.Unknown;
            _takeoffCleared = false;
            _pilotAskedForArrival = false;
            _landingCleared = false;
            _emergency = false;
            _distress = false;
            _announced.Clear();
            _sequencer.Reset();
            _sequencerSeeded = false;   // re-déduira la position à la reconnexion
            _nearestAt = default;
            ResetReadbackState();
        }
    }

    private void OnRadio(RadioSnapshot r) { _radio = r; _flightContext.OnRadio(r); CheckAutoContact(); }

    private void OnContext(ContextSnapshot c)
    {
        _context = c;
        _inFlightSession = c.InFlightSession;
        _flightContext.OnContext(c);

        var phase = _flightContext.EffectivePhase;
        PhaseChanged?.Invoke(new FlightPhaseDebug(phase, _flightContext.HasBeenAirborne, c.OnGround));
        RunDirector(phase);
        RunSequencer(c);
        CheckHoldingPoint(c, phase);
        CheckAutoContact();
    }

    // ------------------------------------------------------------------ point d'arrêt -> tour

    /// <summary>
    /// Distance à un seuil de piste en deçà de laquelle on considère le pilote ARRIVÉ au point
    /// d'arrêt. 500 m : c'est l'ordre de grandeur d'un cheminement de raccordement, assez large
    /// pour couvrir les points d'attente déportés des grands terrains, assez serré pour ne pas
    /// se déclencher au milieu du parking.
    /// </summary>
    private const double HoldingPointMeters = 500;

    /// <summary>Au-delà, ce n'est plus un roulage d'approche du point d'arrêt mais un décollage.</summary>
    private const double HoldingPointMaxKnots = 40;

    /// <summary>Clé d'unicité : ce passage de main ne se fait qu'UNE fois par vol.</summary>
    private const string HoldingPointKey = "handoff_tower_holding";

    /// <summary>
    /// LE SOL REND LA MAIN À LA TOUR AU POINT D'ARRÊT. C'est le contrôleur qui le fait, pas le
    /// pilote qui devine : un vrai Sol vous passe la tour quand vous approchez de la piste,
    /// et sans cela le pilote restait sur la fréquence Sol à attendre une autorisation de
    /// décollage que le Sol ne donne jamais.
    ///
    /// Le déclencheur est GÉOGRAPHIQUE (distance au seuil le plus proche) parce qu'aucune
    /// donnée de taxiway n'est embarquée — mais il est encadré : au sol, en roulage de départ,
    /// à vitesse de roulage, et seulement si l'on n'est pas DÉJÀ sur la tour.
    /// </summary>
    private void CheckHoldingPoint(ContextSnapshot c, FlightPhase phase)
    {
        if (!_settings.Current.AtcEnabled || !_inFlightSession) return;
        if (!c.OnGround || phase != FlightPhase.TaxiOut) return;
        if (c.GroundSpeedKnots > HoldingPointMaxKnots) return;
        if (_announced.Contains(HoldingPointKey)) return;

        var ctx = _flightContext.Current();

        // Déjà sur la tour (ou sur un terrain sans Sol, où la tour gère le roulage) : il n'y
        // a personne à qui passer la main.
        if (ctx.Controller == ControllerType.Tower) return;

        var near = _runways.NearestThreshold(ctx.AirportIcao, c.Latitude, c.Longitude);
        if (near is null || near.Value.Meters > HoldingPointMeters) return;

        var (name, freqHz) = TerminalStation(ctx.AirportIcao, ControllerType.Tower);
        if (freqHz <= 0) return;   // pas de tour publiée -> on ne renvoie nulle part

        // Fréquence déjà affichée : le pilote a pris les devants, on ne lui redit pas d'y aller.
        if (_radio is { } r && FrequencyFormatter.SameChannel(r.Com1ActiveHz, freqHz))
        {
            _announced.Add(HoldingPointKey);
            return;
        }

        if (!_announced.Add(HoldingPointKey)) return;

        Diagnostics.FileLog.Write(
            $"[point d'arrêt] {near.Value.End.Ident} à {near.Value.Meters:F0} m -> passage à {name}.");

        _ = SpeakHoldingPointHandoffAsync(ctx, name, freqHz);
    }

    private async Task SpeakHoldingPointHandoffAsync(FlightContext ctx, string name, double freqHz)
    {
        try
        {
            string text = _brain.AnnounceTransfer(ctx, name, freqHz);
            await SpeakRawAsync(text);

            // Passage de main : le pilote collationne la fréquence, et le Sol se tait ensuite.
            SetExpectingReadback(true, text, handoff: true);
        }
        catch (Exception ex) { Diagnostics.FileLog.Exception("passage à la tour au point d'arrêt", ex); }
    }

    // ------------------------------------------------------------------ transferts de fréquence

    private void RunSequencer(ContextSnapshot c)
    {
        if (!_settings.Current.AtcEnabled || !_inFlightSession) return;

        var state = new FlightState(c.OnGround, c.AltitudeAglFeet, c.AltitudeMslFeet,
                                    c.VerticalSpeedFpm, c.GroundSpeedKnots,
                                    DistanceToArrivalNm(c, _plans.Current));

        // Les règles peuvent changer en cours de session (import SimBrief, changement
        // d'avion, réglage modifié) : on ré-aligne la machine à états à chaque instantané
        // plutôt que de figer l'enchaînement au démarrage.
        _sequencer.Rules = _flightContext.Rules;

        // DÉMARRAGE EN VOL : l'app peut être lancée alors qu'on est déjà en croisière. La
        // séquence repartait alors de la Tour de départ et attendait un décollage qui
        // n'arriverait jamais -> plus aucun transfert du vol. On l'amorce sur la position
        // qui correspond à l'état réel, et on annonce au pilote sur quelle fréquence être.
        if (!_sequencerSeeded)
        {
            _sequencerSeeded = true;
            var start = ControllerSequencer.PositionFor(state, _sequencer.Rules);
            _sequencer.StartAt(start);
            if (!c.OnGround) _ = AnnounceTransferAsync(start, initialContact: true);
            return;
        }

        if (_sequencer.Update(state) is { } pos) _ = AnnounceTransferAsync(pos);
    }

    /// <summary>
    /// Distance à l'arrivée en milles nautiques. Avec un plan SimBrief on vise la vraie
    /// destination ; SANS plan on prend l'aéroport contrôlé le plus proche — sinon la
    /// distance restait infinie et les transferts Approche / Tour d'arrivée / Sol
    /// n'étaient JAMAIS déclenchés (le vol s'arrêtait après le Départ).
    /// </summary>
    private double DistanceToArrivalNm(ContextSnapshot c, FlightPlan? plan)
    {
        if (plan is not null && plan.DestinationLat != 0 && plan.DestinationLon != 0)
            return Geo.DistanceMeters(c.Latitude, c.Longitude, plan.DestinationLat, plan.DestinationLon) / 1852.0;

        string? icao = NearestControlledCached(c.Latitude, c.Longitude);
        if (icao is null) return double.MaxValue;

        var pos = _stations.AirportPosition(icao);
        if (pos is null) return double.MaxValue;

        return Geo.DistanceMeters(c.Latitude, c.Longitude, pos.Value.Lat, pos.Value.Lon) / 1852.0;
    }

    // La recherche du plus proche balaye tout le jeu de fréquences : on la garde en cache,
    // elle est appelée à chaque instantané (≈ 1 Hz) alors que le résultat bouge lentement.
    //
    // Le cache est invalidé quand les règles de vol changent : VFR et IFR ne retiennent pas
    // les mêmes terrains, garder la réponse de l'autre régime enverrait le pilote au mauvais
    // endroit jusqu'à l'expiration du délai.
    private string? NearestControlledCached(double lat, double lon)
    {
        bool small = _flightContext.Rules == FlightRules.Vfr;
        var now = DateTime.UtcNow;

        if (_nearestAt != default && small == _nearestIncludedSmall
            && (now - _nearestAt).TotalSeconds < NearestCacheSeconds)
            return _nearestIcao;

        _nearestAt = now;
        _nearestIncludedSmall = small;
        _nearestIcao = _stations.NearestControlledAirportIcao(lat, lon, small);
        return _nearestIcao;
    }

    /// <param name="initialContact">
    /// Vrai pour le message d'accueil quand l'app est lancée en plein vol : dans ce cas il
    /// FAUT donner une fréquence au pilote, quitte à se rabattre sur une autre position.
    /// </param>
    private async Task AnnounceTransferAsync(ControllerPosition pos, bool initialContact = false)
    {
        try
        {
            var ctx = _flightContext.Current();

            // VFR, zone quittée : il n'y a personne à contacter. La tour ne transfère pas,
            // elle LIBÈRE — « frequency change approved, squawk VFR ». Annoncer un transfert
            // vers un contrôleur inexistant serait pire que le silence.
            if (pos == ControllerPosition.VfrEnroute)
            {
                await SpeakRawAsync(_brain.AnnounceLeavingZone(ctx));
                return;
            }

            var (name, freqHz) = await ResolveControllerAsync(pos, _plans.Current, ctx);

            // Fréquence Centre introuvable : on n'invente rien ET on n'annonce pas un
            // transfert sans numéro (« contact center » tout court n'aide personne). On
            // laisse donc le pilote avec le Départ et on marque le Centre comme sauté, ce
            // qui fait enchaîner Départ -> Approche : tous les appels gardent une vraie
            // fréquence, et la chaîne va bien jusqu'à l'arrivée.
            if (pos == ControllerPosition.Center && freqHz <= 0)
            {
                _sequencer.SkipCenter = true;
                _sequencer.StartAt(ControllerPosition.Departure);
                System.Diagnostics.Debug.WriteLine("[WilcoATC/Atc] aucune fréquence Centre -> étape sautée, on reste au Départ.");

                // Sauf s'il s'agit du message d'accueil : là, se taire laisserait le pilote
                // sans interlocuteur au lancement. On annonce le Départ, qui a une vraie
                // fréquence. (Récursion sûre : l'appel ci-dessous ne peut pas repasser ici.)
                if (initialContact) await AnnounceTransferAsync(ControllerPosition.Departure);
                return;
            }

            // RÈGLE GÉNÉRALE : on n'annonce JAMAIS « contactez X » sans numéro. C'était
            // encore le cas pour toutes les positions sauf le Centre — un terrain qui ne
            // publie qu'une fréquence Tour (très courant hors des grands aéroports) donnait
            // « contact Seymour Galapagos Ecological Departure, so long » sans fréquence,
            // c'est-à-dire une instruction inapplicable.
            //
            // Deux issues, et aucune n'est le silence :
            //  • message d'accueil (lancement en vol) : on se rabat sur la Tour de l'aéroport
            //    contrôlé le plus proche, qui a une fréquence par construction ;
            //  • en vol : on garde le pilote où il est, ce que dit la vraie phraséologie.
            if (freqHz <= 0)
            {
                if (initialContact)
                {
                    var fallback = TerminalStation(
                        _stations.NearestControlledAirportIcao(
                            _context?.Latitude ?? 0, _context?.Longitude ?? 0,
                            includeSmallFields: ctx.Rules == FlightRules.Vfr),
                        ControllerType.Tower);

                    if (fallback.FreqHz > 0)
                    {
                        string hello = _brain.AnnounceTransfer(ctx, fallback.Name, fallback.FreqHz);
                        await SpeakRawAsync(hello);
                        SetExpectingReadback(true, hello);
                        return;
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[WilcoATC/Atc] aucune fréquence pour {pos} ({name}) -> le pilote reste sur sa fréquence.");
                await SpeakRawAsync(_brain.AnnounceRemainThisFrequency(ctx));
                return;
            }

            string text = _brain.AnnounceTransfer(ctx, name, freqHz);
            await SpeakRawAsync(text);
            SetExpectingReadback(true, text); // le pilote collationne le transfert
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[WilcoATC/Atc] transfert : " + ex); }
    }

    /// <summary>Portée au-delà de laquelle un Départ ne concerne plus le vol (NM).</summary>
    private const double TerminalRelevanceNm = 60;

    /// <summary>
    /// Portée d'une Approche (NM). Plus large que le Départ : on est légitimement transféré
    /// à l'approche d'assez loin en descente, mais jamais à 300 NM en croisière.
    /// </summary>
    private const double ApproachRelevanceNm = 120;

    /// <summary>
    /// L'avion est-il à portée de ce terrain ? Terrain inconnu ou position indisponible ->
    /// on répond OUI : c'est une garde contre l'absurde, pas un filtre de plus qui ferait
    /// taire l'ATC dès qu'une donnée manque.
    /// </summary>
    private bool IsWithin(string? icao, double maxNm)
    {
        if (string.IsNullOrWhiteSpace(icao) || _context is not { } c) return true;

        var pos = _stations.AirportPosition(icao!);
        if (pos is null) return true;

        double meters = Geo.DistanceMeters(c.Latitude, c.Longitude, pos.Value.Lat, pos.Value.Lon);
        return meters / 1852.0 <= maxNm;
    }

    private async Task<(string Name, double FreqHz)> ResolveControllerAsync(ControllerPosition pos, FlightPlan? plan, FlightContext ctx)
    {
        // Sans plan de vol (pas de SimBrief), on retombe sur l'aéroport CONTRÔLÉ le plus
        // proche (celui qui a une fréquence Tour) plutôt que le « plus proche » brut de
        // SimConnect : ça évite un petit terrain voisin sans fréquence (ex. Melsbroek/EBMB
        // au lieu de Bruxelles/EBBR). Dernier repli : l'ICAO SimConnect.
        string? nearest = _stations.NearestControlledAirportIcao(
            _context?.Latitude ?? 0, _context?.Longitude ?? 0,
            includeSmallFields: ctx.Rules == FlightRules.Vfr);
        // DÉPART = l'aéroport où l'on est RÉELLEMENT (position), pas forcément celui du plan :
        // décoller de RPLL avec un plan EBBR->KJFK ne doit pas renvoyer vers « Brussels Departure ».
        string? depIcao = nearest ?? ctx.AirportIcao ?? plan?.OriginIcao;
        string? arrIcao = plan?.DestinationIcao ?? nearest ?? ctx.AirportIcao;

        // GARDE DE PERTINENCE : une position TERMINALE (Départ / Approche) n'a de sens que
        // près du terrain concerné. Sans elle, un vol de croisière sans plan de vol se voyait
        // renvoyer vers « l'Approche » du terrain SURVOLÉ — celui que NearestControlledAirport
        // désigne, à 300 NM de la destination : une instruction absurde, et impossible à
        // collationner. Hors de portée, on ne propose rien : l'appelant garde alors le pilote
        // sur sa fréquence, ce que dit la vraie phraséologie quand il n'y a personne à qui
        // passer la main.
        if (pos is ControllerPosition.Departure && !IsWithin(depIcao, TerminalRelevanceNm))
            return (pos.ToString(), 0);
        if (pos is ControllerPosition.Approach && !IsWithin(arrIcao, ApproachRelevanceNm))
            return (pos.ToString(), 0);

        return pos switch
        {
            ControllerPosition.Departure =>
                TerminalStation(depIcao, ControllerType.Departure, ControllerType.Approach),
            ControllerPosition.Center =>
                (CenterName(), await CenterFrequencyHzAsync(depIcao ?? arrIcao)),
            ControllerPosition.Approach =>
                TerminalStation(arrIcao, ControllerType.Approach, ControllerType.Departure),
            ControllerPosition.ArrivalTower =>
                TerminalStation(arrIcao, ControllerType.Tower),
            // Beaucoup de terrains n'ont PAS de fréquence Sol : c'est la Tour qui gère le
            // roulage. Se rabattre dessus est la réalité du terrain, pas un pis-aller.
            ControllerPosition.ArrivalGround =>
                TerminalStation(arrIcao, ControllerType.Ground, ControllerType.Tower),
            _ => (pos.ToString(), 0),
        };
    }

    /// <summary>
    /// Station terminale : on essaie les types dans l'ordre et le LIBELLÉ SUIT le type
    /// réellement trouvé — si le Départ n'existe pas et qu'on prend l'Approche, on annonce
    /// « … Approach », pas « … Departure » sur une fréquence d'approche.
    ///
    /// Aucune recherche géographique ici, volontairement : contrairement au Centre (service
    /// de secteur), emprunter la fréquence d'approche d'un AUTRE aéroport et l'annoncer sous
    /// le nom du nôtre serait faux. Rien trouvé -> 0, et l'appelant garde le pilote sur sa
    /// fréquence au lieu d'inventer.
    /// </summary>
    private (string Name, double FreqHz) TerminalStation(string? icao, params ControllerType[] types)
    {
        foreach (var t in types)
        {
            if (string.IsNullOrWhiteSpace(icao)) break;
            var hz = _stations.FindFrequencyHz(icao!, t);
            if (hz is not null) return (TerminalName(icao, WordFor(t)), hz.Value);
        }
        return (TerminalName(icao, WordFor(types[0])), 0);
    }

    private static string WordFor(ControllerType t) => t switch
    {
        ControllerType.Ground => "Ground",
        ControllerType.Tower => "Tower",
        ControllerType.Approach => "Approach",
        ControllerType.Departure => "Departure",
        ControllerType.Clearance => "Delivery",
        ControllerType.Center => "Center",
        _ => "Control",
    };

    private double FreqFor(string? icao, params ControllerType[] types)
    {
        if (string.IsNullOrWhiteSpace(icao)) return 0;
        foreach (var t in types)
        {
            var hz = _stations.FindFrequencyHz(icao!, t);
            if (hz is not null) return hz.Value;
        }
        return 0;
    }

    private string TerminalName(string? icao, string word)
    {
        string? name = icao is not null ? FlightPlan.CleanAirportName(_stations.LookupAirportName(icao)) : null;
        return string.IsNullOrWhiteSpace(name) ? word : $"{name} {word}";
    }

    /// <summary>
    /// Nom du Centre. Le secteur en-route, quand il est installé, donne l'indicatif RÉEL
    /// (« Bordeaux Control ») : bien mieux qu'un « Center » générique, et cohérent avec la
    /// fréquence qu'on va annoncer puisqu'ils viennent de la même source.
    /// </summary>
    private string CenterName()
    {
        if (CurrentSector() is { } sector) return sector.Name;

        string cfg = _settings.Current.CenterName;
        return string.IsNullOrWhiteSpace(cfg) ? "Center" : $"{cfg} Center";
    }

    /// <summary>Secteur en-route contenant l'avion, ou null (données absentes / hors secteur).</summary>
    private EnrouteSector? CurrentSector()
    {
        if (_context is not { } c) return null;
        try { return _sectors.Find(c.Latitude, c.Longitude, c.AltitudeMslFeet); }
        catch { return null; }
    }

    // Fréquence Centre, par ordre de FIABILITÉ décroissante. On n'invente jamais : à défaut
    // on renvoie 0 et le transfert est annoncé SANS fréquence (« contact center, good day »),
    // ce qui vaut mieux qu'un silence ou qu'un chiffre faux.
    private async Task<double> CenterFrequencyHzAsync(string? icaoForRegion)
    {
        // 1. SECTEUR EN-ROUTE contenant réellement l'avion (contour + tranche d'altitude).
        //    C'est la seule source qui réponde partout : les fréquences ACC ne sont rattachées
        //    à aucun aéroport, et l'étape 2 ci-dessous ne les trouve donc qu'en Amérique du
        //    Nord, où les données les accrochent par convention aux terrains survolés.
        //    Installé à la demande (voir EnrouteSectorImporter) : absent, on enchaîne.
        if (CurrentSector() is { } sector) return sector.FrequencyHz;

        // 2. Fréquence Centre publiée LA PLUS PROCHE dans les données aéroport.
        if (_context is { } ctx)
        {
            double? near = _stations.FindNearestFrequencyHz(
                ControllerType.Center, ctx.Latitude, ctx.Longitude, CenterSearchRadiusKm);
            if (near is not null) return near.Value;
        }

        // 3. VATSIM, si le réseau est en ligne et l'option activée.
        if (_settings.Current.VatsimEnabled && !string.IsNullOrWhiteSpace(icaoForRegion))
        {
            string prefix = icaoForRegion!.Length >= 2 ? icaoForRegion[..2] : icaoForRegion!;
            double? vhz = await _vatsim.FindCenterFrequencyHzAsync(prefix);
            if (vhz is not null) return vhz.Value;
        }

        // 4. Valeur approximative, UNIQUEMENT si l'utilisateur a nommé son Centre lui-même.
        if (!string.IsNullOrWhiteSpace(_settings.Current.CenterName))
        {
            System.Diagnostics.Debug.WriteLine("[WilcoATC/Atc] fréquence Centre APPROXIMATIVE (configurée).");
            return _settings.Current.CenterFrequencyMhz * 1_000_000.0;
        }

        System.Diagnostics.Debug.WriteLine("[WilcoATC/Atc] fréquence Centre inconnue -> transfert annoncé sans fréquence.");
        return 0;
    }

    // ATC proactif : sur chaque transition de phase, l'ATC peut initier une transmission
    // (décollage sans clairance, transfert de fréquence, approche, atterrissage…).
    private void RunDirector(FlightPhase phase)
    {
        if (phase == _lastDirectorPhase) return;
        var prev = _lastDirectorPhase;
        _lastDirectorPhase = phase;

        if (phase == FlightPhase.Parked) // nouveau vol : on réarme
        {
            _takeoffCleared = false;
            _pilotAskedForArrival = false;
            _landingCleared = false;
            _emergency = false;
            _distress = false;
            _announced.Clear();
            _sequencer.Reset();
            ResetReadbackState();
        }

        if (!_settings.Current.AtcEnabled) return;

        // PREMIÈRE observation du vol (phase précédente inconnue) : on prend l'état en cours
        // comme référence SANS rien annoncer. Sinon, lancer l'app en croisière déclenchait un
        // « vous avez décollé sans autorisation » — et la lancer en finale, un « cleared to
        // land » surgi de nulle part. C'est le séquenceur qui salue, en donnant la fréquence.
        if (prev == FlightPhase.Unknown)
        {
            System.Diagnostics.Debug.WriteLine($"[WilcoATC/Atc] vol rejoint en phase {phase} -> aucune annonce d'ouverture.");
            return;
        }

        // Hors cockpit (menu principal, carte du monde) : on ne dit RIEN. Le suivi de phase
        // ci-dessus continue, mais aucune annonce n'est émise tant qu'on n'est pas aux commandes.
        if (!_inFlightSession) return;

        // En Mode Test (ou override debug « cleared »), on considère le décollage comme
        // autorisé : l'avertissement « décollé sans autorisation » n'est PAS déclenché.
        bool cleared = _takeoffCleared || _forceCleared || _settings.Current.TestMode;

        string? key = FlightDirector.OnPhaseTransition(prev, phase, cleared);
        if (key is null) return;

        // POURQUOI L'AVERTISSEMENT PART. « L'ATC dit que je n'avais pas le droit alors que
        // si » ne se tranche pas sans savoir si la clairance a RÉELLEMENT été accordée : une
        // demande de décollage mal classée — en collationnement, par exemple — obtient une
        // réponse aimable sans jamais lever le drapeau. Les deux états côte à côte le disent.
        if (!cleared && key == "takeoff_no_clearance")
            Diagnostics.FileLog.Write(
                $"[décollage] avertissement « sans autorisation » : accordé={_takeoffCleared}, " +
                $"forcé={_forceCleared}, mode test={_settings.Current.TestMode}, phase {prev} -> {phase}");

        // L'APPROCHE et l'ATTERRISSAGE ne s'offrent PAS d'eux-mêmes. Ce sont des clairances :
        // elles répondent à une demande du pilote (« en approche », « terrain en vue », une
        // demande de descente), elles ne surgissent pas parce que l'avion est descendu sous
        // un seuil. Sans demande, l'ATC se tait — et si le pilote demande plus tard, c'est la
        // voie normale requête -> réponse qui l'autorise, avec la vraie phraséologie.
        //
        // On ne marque PAS l'événement comme annoncé en sortant : une remise de gaz peut
        // repasser par l'approche, et d'ici là le pilote aura peut-être appelé.
        // DÉJÀ AUTORISÉ EN FINALE : on ne redit rien. Cette annonce-ci part au passage en
        // phase « atterrissage », laquelle ne commence qu'AU SOL, pendant le roulage — elle
        // arrivait donc systématiquement après le toucher. Elle ne subsiste que pour le pilote
        // qui n'a jamais annoncé sa finale ; pour les autres, elle ferait doublon.
        if (key == "landing" && _landingCleared) return;

        if ((key is "approach" or "landing") && !_pilotAskedForArrival)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WilcoATC/Atc] « {key} » non annoncé : le pilote n'a rien demandé.");
            return;
        }

        if (!_announced.Add(key)) return; // une seule fois par vol

        string? text = _brain.Announce(key, _flightContext.Current());
        if (!string.IsNullOrWhiteSpace(text))
        {
            SetExpectingReadback(true, text!); // l'ATC vient d'instruire -> on attend un collationnement
            _ = SpeakRawAsync(text!);
        }
    }

    private void ResetReadbackState()
    {
        lock (_stateLock) { _granted.Clear(); _expectingReadback = false; _lastAtcWords = ""; }
        ExpectingReadbackChanged?.Invoke(false);
    }

    private void OnAircraft(AircraftSnapshot a) { _aircraft = a; _flightContext.OnAircraft(a); }

    // ------------------------------------------------------------------ déclencheur auto

    private void CheckAutoContact()
    {
        if (!_settings.Current.AtcEnabled || !_settings.Current.AtcAutoContact || !_inFlightSession) return;

        var r = _radio; var c = _context;
        if (r is null || c is null) return;

        if (r.Com1ActiveHz < 1_000_000) return; // radio non initialisée

        // ANTI-REBOND : en tournant le bouton on traverse plein de fréquences ; on ne salue
        // qu'une fois la fréquence STABLE quelques secondes.
        string key = Math.Round(r.Com1ActiveHz / 1000.0).ToString();
        if (key != _pendingFreqKey)
        {
            _pendingFreqKey = key;
            _pendingSince = DateTime.UtcNow;
            return;
        }
        if ((DateTime.UtcNow - _pendingSince).TotalSeconds < FrequencySettleSeconds) return;
        if (key == _lastStationKey) return; // déjà salué sur cette fréquence

        string? previous = _lastStationName;
        var previousController = _lastStationController;
        _lastStationKey = key;

        // LE PILOTE EST PARTI SUR LA FRÉQUENCE DEMANDÉE : un passage de main en attente de
        // collationnement est SOLDÉ par ce seul fait. Sans cela, son premier appel au nouveau
        // contrôleur — « Brussels Tower, Beeline 3633, ready for departure » — reprenait assez
        // de mots de l'instruction (« Brussels », « Tower ») pour passer pour la relecture de
        // celle-ci : la demande était avalée, sans réponse.
        bool pendingHandoff;
        lock (_stateLock) pendingHandoff = _expectingReadback && _awaitedReadbackIsHandoff;
        if (pendingHandoff) SetExpectingReadback(false, consumed: true);

        // Nouveau contrôleur : on l'aborde dans la langue du PAYS, sans hériter de celle
        // qu'on parlait au précédent. C'est de nouveau le premier échange qui tranchera.
        _language.SetAirport(_stations.OperationalAirport(c.NearestAirportIcao, c.Latitude, c.Longitude));
        _language.ResetPilotLanguage();

        string station = ControllerLabel(r, c);
        _lastStationName = station;
        _lastStationController =
            _stations.ResolveStation(r.Com1ActiveHz, c.Latitude, c.Longitude)?.Controller
            ?? ControllerType.Unknown;

        // Le contrôleur peut parler DEUX langues : celle du pays, et l'anglais dès que le
        // pilote s'y met. Chacune a sa voix, donc son modèle — et charger un modèle coûte
        // une demi-seconde. On les charge MAINTENANT, pendant que personne n'attend, plutôt
        // qu'au milieu de la première réponse.
        PreloadStationVoices(station, _lastStationController);

        // Changement de fréquence : le contrôleur qu'on QUITTE dit au revoir (avec SA voix),
        // puis celui qu'on REJOINT dit bonjour (avec la sienne) -> la voix change d'une
        // fréquence à l'autre.
        _ = GreetNewStationAsync(previous, previousController, station, _lastStationController);
    }

    /// <summary>
    /// Nom du contrôleur pour la fréquence courante. Si la fréquence n'est pas dans les
    /// données OurAirports (fréquence absente du CSV, espacement 8.33…), on NE RESTE PAS
    /// muet : on se rabat sur l'aéroport le plus proche, puis sur un libellé générique.
    /// </summary>
    private string ControllerLabel(RadioSnapshot r, ContextSnapshot c)
    {
        string? resolved = _stations.Resolve(r.Com1ActiveHz, c.Latitude, c.Longitude);
        if (!string.IsNullOrWhiteSpace(resolved)) return resolved!;

        string? opIcao = _stations.OperationalAirport(c.NearestAirportIcao, c.Latitude, c.Longitude);
        string? airport = FlightPlan.CleanAirportName(
            opIcao is null ? null : _stations.LookupAirportName(opIcao));
        if (!string.IsNullOrWhiteSpace(airport)) return $"{airport} Control";

        return "Control";
    }

    /// <summary>
    /// Voix du contrôleur COURANT : dérivée de la station, donc elle CHANGE quand on
    /// change de fréquence (chaque contrôleur a sa propre voix, stable pendant le vol).
    /// </summary>
    /// <param name="controller">
    /// Type de position (Sol / Tour / Approche / Centre). Il donne son CARACTÈRE à la voix,
    /// et garantit que deux positions du même aéroport ne sonnent pas identiques.
    /// </param>
    /// <summary>
    /// Charge en tâche de fond les modèles des voix que ce contrôleur est susceptible
    /// d'employer : celle de la langue du pays, et celle de l'anglais (le pilote peut
    /// basculer à tout moment). Sans ça, le premier échange dans chaque langue paie le
    /// chargement du modèle — soit une demi-seconde de silence juste après avoir parlé.
    /// </summary>
    private void PreloadStationVoices(string station, ControllerType controller)
    {
        var languages = new HashSet<AtcLanguage> { _language.CountryLanguage(), AtcLanguage.English };

        _ = Task.Run(() =>
        {
            foreach (var lang in languages)
            {
                try { _tts.Preload(_picker.For(station, lang, controller)); }
                catch { /* au mieux : la synthèse rechargera si nécessaire */ }
            }
        });
    }

    private TtsVoice AtcVoice(string? station = null, ControllerType? controller = null)
        => _picker.For(station ?? _lastStationName ?? _flightContext.Current().StationName,
                       _brain.EffectiveLanguage,
                       controller ?? _lastStationController);

    private async Task GreetNewStationAsync(string? previousStation, ControllerType previousController,
                                            string newStation, ControllerType newController)
    {
        var ctx = _flightContext.Current();

        // L'ATIS n'est PAS un interlocuteur : c'est un enregistrement en boucle. Personne ne
        // le salue, et il ne dit bonjour à personne — la fréquence appartient à AtisDirector.
        if (previousStation is not null && previousController != ControllerType.Atis)
        {
            await SpeakRawAsync(_brain.Farewell(ctx), AtcVoice(previousStation, previousController));
            await Task.Delay(_rng.Next(500, 1200));
        }

        if (newController == ControllerType.Atis) return;

        await SpeakRawAsync(_brain.Greeting(ctx), AtcVoice(newStation, newController));
    }

    public void TriggerManualTest() => _ = SpeakAsync(AtcTrigger.ManualTest);

    // ------------------------------------------------------------------ requête pilote

    public void SetControllerOverride(ControllerType? controller) => _flightContext.SetControllerOverride(controller);

    // ------------------------------------------------------------------ overrides DEBUG (Mode Test)

    public void SetPhaseOverride(FlightPhase? phase)
    {
        // Force l'état de phase utilisé pour VALIDER les requêtes (Current().Phase). On ne
        // touche pas au suivi du director proactif (évite un ré-déclenchement parasite).
        _flightContext.SetPhaseOverride(phase);
        EmitPhaseDebug();
    }

    public void SetHasBeenAirborneOverride(bool? value)
    {
        _flightContext.SetHasBeenAirborneOverride(value);
        EmitPhaseDebug();
    }

    public void SetTakeoffClearedOverride(bool cleared)
    {
        _forceCleared = cleared;
        if (cleared) _takeoffCleared = true;
    }

    // Ré-émet l'état de phase pour rafraîchir l'UI immédiatement (utile hors simulateur).
    private void EmitPhaseDebug()
        => PhaseChanged?.Invoke(new FlightPhaseDebug(
               _flightContext.EffectivePhase, _flightContext.HasBeenAirborne, _context?.OnGround ?? true));

    public void HandlePilotText(string text) => _ = HandlePilotAsync(text);

    private async Task HandlePilotAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        PilotTranscript?.Invoke(text.Trim());

        // Le pilote parle : toute escalade en cours (relances, intercepteur) s'arrête là.
        lock (_stateLock) _lastPilotUtc = DateTime.UtcNow;
        _intercept?.Recall();

        // ET LE CONTRÔLEUR RETROUVE LA PAROLE. La communication est rétablie par le fait même
        // que le pilote transmet : c'est exactement ce que l'ATC attendait pour reprendre le
        // service. Sans cette remise à zéro, un pilote qui répond enfin resterait ignoré pour
        // le reste du vol — la sanction deviendrait définitive, ce qu'elle n'est pas.
        if (_controllerGaveUp)
        {
            _controllerGaveUp = false;
            Diagnostics.FileLog.Write("[panne radio] contact rétabli — le contrôleur reprend le service.");
        }

        // CHRONO DE BOUT EN BOUT. La latence ressentie se répartit sur quatre postes dont un
        // seul est visible depuis ici ; sans ces repères, on ne peut qu'échafauder des
        // hypothèses. Chaque réponse laisse donc une ligne dans le journal.
        var trip = System.Diagnostics.Stopwatch.StartNew();

        RecognizedIntent recognized;
        try { recognized = await _intents.RecognizeAsync(text); }
        catch { recognized = new RecognizedIntent(PilotIntent.Unknown, text, "error"); }
        long tIntent = trip.ElapsedMilliseconds;

        // État courant du sous-état collationnement (lu sous verrou).
        bool expecting; string atcWords; DateTime atcWordsAt; bool alreadyGranted;
        PilotIntent awaitedOf; bool awaitedIsHandoff;
        lock (_stateLock)
        {
            expecting = _expectingReadback;
            atcWords = _lastAtcWords;
            atcWordsAt = _lastAtcWordsUtc;
            alreadyGranted = IsClearanceRequest(recognized.Intent) && _granted.Contains(recognized.Intent);
            awaitedOf = _awaitedReadbackOf;
            awaitedIsHandoff = _awaitedReadbackIsHandoff;
        }

        string callsign = _callsigns.Speak(_aircraft?.TailNumber);

        // (6) PRIORITÉ COLLATIONNEMENT : une répétition/accusé/callsign l'emporte sur toute
        //     détection de requête -> on ne valide PAS comme une requête.
        //
        //     Le test ne dépend PLUS de « on attend un collationnement » : il suffit que l'ATC
        //     ait dit quelque chose et que le pilote le reprenne. C'est ce qui produisait les
        //     deux symptômes les plus agaçants — relire « roulez au point d'attente piste 26 »
        //     contient le mot « roulage », donc la grammaire y voyait une DEMANDE déjà
        //     accordée (« c'est déjà approuvé ») ; et relire un transfert de fréquence après
        //     l'abandon des relances ne correspondait à aucune requête connue (« say again »).
        //     Un pilote qui répète ce qu'on vient de lui dire collationne, point.
        //     BORNÉ DANS LE TEMPS. Une instruction vieille de plusieurs minutes n'appelle plus
        //     de relecture : au-delà, ce que dit le pilote est une nouvelle requête, même s'il
        //     en reprend des mots par hasard. Ce garde-fou double la remise à zéro explicite —
        //     un chemin qui oublierait de solder l'instruction ne peut plus figer l'ATC.
        bool stillRelevant = !string.IsNullOrWhiteSpace(atcWords)
                             && DateTime.UtcNow - atcWordsAt < ReadbackRelevance;

        if (stillRelevant && ReadbackDetector.IsReadback(text, callsign, atcWords))
            recognized = recognized with { Intent = PilotIntent.Readback, Source = "readback-context", Reason = "readback (context)" };

        // Le pilote vient de parler une langue identifiée : le contrôleur s'y aligne pour la
        // suite de l'échange. C'est fait AVANT d'évaluer la requête, pour que la réponse à
        // CETTE transmission soit déjà dans la bonne langue.
        if (recognized.Language is { } spoken) _language.NotePilotLanguage(spoken);

        // Le pilote engage son arrivée : à partir de là, l'ATC peut lui donner l'approche et
        // l'atterrissage. Se signaler en approche ou demander une descente suffit — c'est
        // exactement ce qui déclenche une clairance d'arrivée dans la vraie phraséologie.
        if (recognized.Intent is PilotIntent.ReportApproach or PilotIntent.ReportFinal
                              or PilotIntent.RequestAltitude)
            _pilotAskedForArrival = true;

        // AUTORISÉ À ATTERRIR : on note que c'est fait, pour que l'annonce automatique — qui
        // ne partait qu'au TOUCHER, donc bien trop tard — ne vienne pas répéter la clairance
        // une seconde fois alors que l'avion est déjà sur la piste.
        if (recognized.Intent == PilotIntent.ReportFinal) _landingCleared = true;

        // ---------------------------------------------------------------- urgence
        //
        // Une déclaration de détresse ou d'urgence bascule le contrôleur en régime prioritaire
        // et lui INTERDIT de réclamer quoi que ce soit. On coupe donc immédiatement toute
        // relance en cours : un « collationnez » qui tomberait sur un mayday serait grotesque.
        switch (recognized.Intent)
        {
            case PilotIntent.DeclareMayday:
                _emergency = true;
                _distress = true;
                _pilotAskedForArrival = true;   // il va se poser : l'arrivée est engagée d'office
                _landingCleared = true;         // la règle donne déjà l'autorisation
                SetExpectingReadback(false, consumed: true);
                _intercept?.Recall();
                Diagnostics.FileLog.Write("[urgence] DÉTRESSE déclarée — priorité, fréquence dégagée, plus de relance.");
                break;

            case PilotIntent.DeclarePanPan:
                _emergency = true;
                _pilotAskedForArrival = true;
                SetExpectingReadback(false, consumed: true);
                _intercept?.Recall();
                Diagnostics.FileLog.Write("[urgence] urgence (pan pan) déclarée — priorité, plus de relance.");
                break;

            case PilotIntent.CancelEmergency:
                _emergency = false;
                _distress = false;
                Diagnostics.FileLog.Write("[urgence] annulée — reprise du service normal.");
                break;
        }

        IntentRecognized?.Invoke(recognized);

        FlightContext ctx = _flightContext.Current();
        AtcDecision decision;

        if (recognized.Intent == PilotIntent.Readback)
        {
            // (4a) COLLATIONNEMENT INCOMPLET : le pilote a bien répondu, mais sans relire ce
            //      qui doit l'être (piste, squawk, fréquence, altitude). On redit l'instruction
            //      et on ATTEND TOUJOURS — c'est exactement le rôle du collationnement, et un
            //      « roger » ne vaut pas relecture d'une clairance.
            var required = RequiredReadback();
            var missing = ReadbackChecker.Missing(text, required);

            // TRACE DU VERDICT. Sans elle, un collationnement refusé ne dit ni ce que l'ATC
            // exigeait, ni ce qu'il a cru entendre — et un refus systématique reste
            // indiscernable d'un pilote qui s'exprime mal. Les deux chaînes côte à côte
            // suffisent à trancher.
            Diagnostics.FileLog.Write(
                $"[collationnement] exigé : [{string.Join(" ; ", required.Select(r => $"{r.Kind} {r.Digits}"))}] " +
                $"— entendu : « {text} » — manquant : " +
                (missing.Count == 0 ? "rien" : string.Join(" ; ", missing.Select(m => $"{m.Kind} {m.Digits}"))));

            if (missing.Count > 0 && _settings.Current.RequireReadback)
            {
                string callsign2 = _callsigns.Speak(ctx.Callsign);
                string instruction = LastAtcWords();

                // ON NOMME CE QUI MANQUE. Redire l'instruction entière n'apprenait rien : le
                // pilote venait de l'entendre, et rien ne lui disait lequel des éléments il
                // avait omis. « Négatif, collationnez la piste 0 9 » se corrige tout seul.
                var lang2 = _brain.EffectiveLanguage;
                string items = string.Join(", ", missing.Select(m =>
                    $"{AtcPhrases.ReadbackItemLabel(lang2, m.Kind)} {SpellDigits(m.Digits)}"));

                string correction = AtcPhrases.ReadbackMissing(lang2)
                    .Replace("{callsign}", callsign2)
                    .Replace("{items}", items);

                DecisionMade?.Invoke(new AtcDecision(
                    false, PilotIntent.Readback, correction,
                    "readback incomplete: " + string.Join(", ", missing.Select(m => $"{m.Kind} {m.Digits}")),
                    null));

                await SpeakRawAsync(correction);

                // ON REPART POUR UN TOUR — sur la MÊME instruction, donc avec la même origine.
                // Sans la repasser, un collationnement raté effaçait le fait que l'instruction
                // était une clairance de départ : le pilote qui se reprenait enfin obtenait un
                // « readback correct » sec, sans le passage au Sol qui doit le suivre.
                SetExpectingReadback(true, instruction, forIntent: awaitedOf, handoff: awaitedIsHandoff);
                return;
            }

            // (4b) Un readback n'est JAMAIS refusé pour cause de phase.
            //      L'instruction est SOLDÉE : on oublie les mots de l'ATC, sans quoi la
            //      transmission suivante serait encore prise pour un collationnement.
            SetExpectingReadback(false, consumed: true);

            // PASSAGE DE MAIN DÉJÀ DONNÉ : le contrôleur a fini avec ce vol. Le pilote relit
            // la fréquence et remercie ; on ne répond pas, on ne relance pas. C'est ainsi que
            // se termine un échange réel — et sans cela l'ATC gardait le dernier mot,
            // indéfiniment.
            if (awaitedIsHandoff)
            {
                Diagnostics.FileLog.Write("[collationnement] passage de main accusé — le contrôleur se tait.");
                DecisionMade?.Invoke(new AtcDecision(
                    true, PilotIntent.Readback, "", "handoff acknowledged (no reply)", null));
                return;
            }

            decision = _brain.Evaluate(recognized, ctx, awaitedOf); // règle READBACK -> "readback correct"

            // La réponse elle-même peut PORTER une instruction — « rappelez prêt au repoussage
            // avec le Sol sur 110.100 ». Dans ce cas on attend encore une relecture, mais sans
            // rien répondre ensuite : c'est le dernier mot de ce contrôleur.
            if (ReadbackChecker.Required(decision.ResponseText).Count > 0)
            {
                DecisionMade?.Invoke(decision);
                await SpeakRawAsync(decision.ResponseText);
                SetExpectingReadback(true, decision.ResponseText, handoff: true);
                return;
            }
        }
        else if (alreadyGranted)
        {
            // (5) Requête déjà accordée -> "déjà approuvé" au lieu d'un refus de phase.
            decision = new AtcDecision(true, recognized.Intent, _brain.AlreadyApproved(ctx), "already granted", null);
        }
        else
        {
            decision = _brain.Evaluate(recognized, ctx);

            if (decision.Approved && decision.AdvanceTo == FlightPhase.Pushback)
            {
                _flightContext.MarkPushbackGranted();
                _groundServices.RequestPushback(); // -> GSX (si activé)
            }
            if (decision.Approved && recognized.Intent == PilotIntent.ReadyForDeparture)
                _takeoffCleared = true;

            // (2) Après une clairance/approbation, on attend un collationnement. Le verrou
            //     « déjà accordé » ne vaut QUE pour les clairances sol/départ (une par vol) :
            //     un changement d'altitude peut être redemandé autant de fois qu'on veut,
            //     donc il attend un readback SANS entrer dans la liste des accordés.
            if (decision.Approved && ExpectsReadback(recognized.Intent))
            {
                if (IsClearanceRequest(recognized.Intent))
                    lock (_stateLock) _granted.Add(recognized.Intent);
                SetExpectingReadback(true, decision.ResponseText, forIntent: recognized.Intent);
            }
        }

        // Plus d'IA conversationnelle : une intention non reconnue donne « say again », tout
        // de suite. Le repli LLM interrogeait Ollama à CHAQUE transmission et attendait sa
        // réponse — plusieurs secondes — pour finir la plupart du temps par rendre le même
        // « say again ». Le déterministe répond en une milliseconde et ne dépend de rien.

        DecisionMade?.Invoke(decision);

        long tDecision = trip.ElapsedMilliseconds;
        await SpeakRawAsync(decision.ResponseText);

        // « intention » = compréhension, « décision » = cerveau ATC, « total » = jusqu'à la
        // FIN de la transmission (le son inclus). Un total très supérieur à la somme des deux
        // premiers postes signale une attente du canal, elle-même journalisée par VoiceBus.
        Diagnostics.FileLog.Write(
            $"[latence] intention {tIntent} ms · décision {tDecision - tIntent} ms · " +
            $"total {trip.ElapsedMilliseconds} ms");
    }

    private static bool IsClearanceRequest(PilotIntent i) => i is
        PilotIntent.RequestClearance or PilotIntent.RequestPushback or
        PilotIntent.RequestTaxi or PilotIntent.ReadyForDeparture;

    /// <summary>Intentions après lesquelles l'ATC attend un collationnement du pilote.</summary>
    // La finale vaut AUTORISATION D'ATTERRISSAGE : elle se collationne, piste comprise.
    private static bool ExpectsReadback(PilotIntent i)
        => IsClearanceRequest(i) || i is PilotIntent.RequestAltitude or PilotIntent.ReportFinal;

    /// <param name="consumed">
    /// Le collationnement a-t-il ÉTÉ FAIT ? C'est la distinction qui manquait, et elle est
    /// tout sauf un détail.
    ///
    /// Cesser d'attendre un collationnement recouvre deux situations opposées. Le pilote a
    /// relu correctement : l'instruction est soldée, plus rien ne s'y rapporte. Ou le pilote
    /// n'a rien dit et le contrôleur a renoncé à relancer : l'instruction, elle, tient
    /// toujours, et une relecture tardive doit encore compter.
    ///
    /// Sans cette distinction, <c>_lastAtcWords</c> survivait à un collationnement réussi. Or
    /// la reclassification ne teste que la présence de ces mots : le pilote donnant son
    /// indicatif à chaque transmission, TOUT ce qu'il disait ensuite continuait de ressembler
    /// au collationnement précédent. Demander le décollage se voyait répondre « readback
    /// correct », indéfiniment, et l'ATC devenait inutilisable après le premier échange.
    /// </param>
    /// <param name="forIntent">Demande du pilote à l'origine de l'instruction (voir <see cref="_awaitedReadbackOf"/>).</param>
    /// <param name="handoff">Passage de main : la relecture ne recevra pas de réponse (voir <see cref="_awaitedReadbackIsHandoff"/>).</param>
    private void SetExpectingReadback(bool value, string atcWords = "", bool consumed = false,
                                      PilotIntent forIntent = PilotIntent.Unknown, bool handoff = false)
    {
        lock (_stateLock)
        {
            _expectingReadback = value;
            if (value)
            {
                _lastAtcWords = atcWords;
                _lastAtcWordsUtc = DateTime.UtcNow;
                _requiredReadback = ReadbackChecker.Required(atcWords);
                _readbackNudges = 0;
                _awaitedReadbackOf = forIntent;
                _awaitedReadbackIsHandoff = handoff;
            }
            else
            {
                _requiredReadback = Array.Empty<ReadbackItem>();
                _awaitedReadbackOf = PilotIntent.Unknown;
                _awaitedReadbackIsHandoff = false;
                if (consumed) _lastAtcWords = "";
            }
        }

        ExpectingReadbackChanged?.Invoke(value);

        // PASSAGE DE MAIN : on guette encore la relecture — sans quoi le « Ground 121.875,
        // merci » du pilote passerait pour une requête nouvelle — mais on ne RELANCE pas. Un
        // contrôleur qui a rendu la main ne court pas après le pilote, et l'escalade « panne
        // radio » (trois rappels, puis la défense aérienne) n'aurait ici aucun sens.
        if (value && !handoff) StartReadbackWatch(atcWords);
        else CancelReadbackWatch();
    }

    // ------------------------------------------------------------------ collationnement exigé

    /// <summary>
    /// Silence laissé au pilote avant chaque relance, décompté à partir du POINT FINAL de la
    /// transmission du contrôleur — jamais de la décision.
    ///
    /// Vingt secondes : de quoi lire la clairance, trouver l'alternat, parler, et laisser la
    /// reconnaissance vocale rendre son texte. Dix ne suffisaient pas — une relance partait
    /// pendant que le pilote était encore en train de répondre, ce qui donnait l'impression
    /// d'être harcelé plutôt que rappelé à l'ordre.
    /// </summary>
    private const int ReadbackGraceSeconds = 20;

    /// <summary>
    /// Nombre de relances avant l'escalade. Trois appels sans réponse, c'est ce qu'on
    /// considère au sol comme une panne radio — et c'est à ce moment que la défense
    /// aérienne est prévenue.
    /// </summary>
    private const int MaxReadbackNudges = 3;

    private IReadOnlyList<ReadbackItem> _requiredReadback = Array.Empty<ReadbackItem>();

    /// <summary>
    /// Épelle des chiffres pour la synthèse vocale : « 09 » -> « 0 9 », « 118.7 » -> « 1 1 8
    /// decimal 7 ». Collés, ils se prononceraient « neuf » ou « cent dix-huit virgule sept »,
    /// alors qu'un contrôleur les détache toujours.
    /// </summary>
    private static string SpellDigits(string digits)
    {
        var parts = digits.Select(c => c == '.' ? "decimal" : c.ToString());
        return string.Join(" ", parts);
    }

    private IReadOnlyList<ReadbackItem> RequiredReadback()
    {
        lock (_stateLock) return _requiredReadback;
    }

    private string LastAtcWords()
    {
        lock (_stateLock) return _lastAtcWords;
    }

    private int _readbackNudges;
    private CancellationTokenSource? _readbackWatch;

    private void CancelReadbackWatch()
    {
        var cts = Interlocked.Exchange(ref _readbackWatch, null);
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
    }

    /// <summary>
    /// Surveille le silence du pilote. Passé le délai, le contrôleur RÉCLAME le
    /// collationnement, puis REDIT l'instruction, puis renonce — comme un vrai, qui insiste
    /// deux fois et passe à autre chose plutôt que de bloquer la fréquence.
    /// </summary>
    private void StartReadbackWatch(string instruction)
    {
        CancelReadbackWatch();
        if (!_settings.Current.RequireReadback) return;

        // JAMAIS PENDANT UNE URGENCE. Un équipage qui gère une panne moteur ne collationne
        // pas, et le relancer trois fois avant de conclure à une panne radio — puis de faire
        // décoller une patrouille — serait exactement l'inverse de ce qu'un contrôleur fait.
        if (_emergency) return;

        var cts = new CancellationTokenSource();
        _readbackWatch = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // ON ATTEND D'ABORD QUE LA TRANSMISSION COMMENCE.
                    //
                    // La surveillance est armée AVANT que l'ATC ne parle : au moment où l'on
                    // arrive ici, la synthèse n'a pas encore démarré, donc le canal n'est PAS
                    // occupé. Sans cette première attente, le test ci-dessous passait
                    // immédiatement et le compte à rebours s'écoulait PENDANT la clairance —
                    // six à huit secondes — si bien que le « collationnez » tombait deux
                    // secondes après le point final. Le pilote n'avait pas le temps
                    // d'appuyer sur son alternat, et se faisait harceler.
                    //
                    // Borné : si la transmission n'arrive jamais (supprimée, canal pris), on
                    // ne reste pas bloqué à l'attendre indéfiniment.
                    var armed = DateTime.UtcNow;
                    while (!_voice.IsBusy && !cts.IsCancellationRequested
                           && DateTime.UtcNow - armed < TimeSpan.FromSeconds(5))
                        await Task.Delay(100, cts.Token).ConfigureAwait(false);

                    // Puis qu'elle se TERMINE : le silence de grâce part du point final.
                    while (_voice.IsBusy && !cts.IsCancellationRequested)
                        await Task.Delay(200, cts.Token).ConfigureAwait(false);

                    await Task.Delay(TimeSpan.FromSeconds(ReadbackGraceSeconds), cts.Token)
                              .ConfigureAwait(false);

                    bool stillWaiting;
                    int nudges;
                    lock (_stateLock)
                    {
                        stillWaiting = _expectingReadback;
                        nudges = ++_readbackNudges;
                    }

                    // La fréquence a changé, le pilote a répondu, ou le vol est terminé.
                    if (!stillWaiting || !_inFlightSession || cts.IsCancellationRequested) return;

                    var ctx = _flightContext.Current();
                    var lang = _brain.EffectiveLanguage;
                    string callsign = _callsigns.Speak(ctx.Callsign);

                    // Au-delà des relances : PANNE RADIO PRÉSUMÉE. Le contrôleur prévient la
                    // défense aérienne, et un intercepteur se manifeste sur la fréquence.
                    if (nudges > MaxReadbackNudges)
                    {
                        SetExpectingReadback(false);   // on cesse de réclamer, quoi qu'il arrive
                        if (_settings.Current.ReadbackRadioFailureCall)
                            await EscalateNoRadioAsync(callsign, lang).ConfigureAwait(false);
                        return;
                    }

                    // 1re relance : on réclame. 2e : on redit tout, car un pilote muet n'a
                    // généralement pas compris l'instruction. 3e : on insiste, sèchement.
                    string text = nudges switch
                    {
                        1 => AtcPhrases.ReadbackRequest(lang).Replace("{callsign}", callsign),
                        2 => AtcPhrases.SayAgain(lang)
                                       .Replace("{callsign}", callsign)
                                       .Replace("{instruction}", StripCallsign(instruction, callsign)),
                        _ => AtcPhrases.ReadbackLastCall(lang).Replace("{callsign}", callsign),
                    };

                    await SpeakRawAsync(text).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* collationnement reçu : rien à faire */ }
            catch (Exception ex) { Diagnostics.FileLog.Exception("relance de collationnement", ex);
            }
        }, cts.Token);
    }

    /// <summary>
    /// PANNE RADIO PRÉSUMÉE : trois appels sans réponse. Le contrôleur en tire la conclusion,
    /// demande le squawk 7600 et cesse de réclamer — il continue simplement d'écouter.
    /// </summary>
    /// <summary>Voix de l'intercepteur : identité dédiée, donc stable d'un vol à l'autre.</summary>
    private TtsVoice MilitaryVoice(AtcLanguage lang)
        => _picker.For("military interceptor", lang, ControllerType.Center);

    private async Task EscalateNoRadioAsync(string callsign, AtcLanguage lang)
    {
        try
        {
            DateTime startedAt = DateTime.UtcNow;

            await SpeakRawAsync(AtcPhrases.NoRadioAlert(lang).Replace("{callsign}", callsign))
                .ConfigureAwait(false);

            // À PARTIR D'ICI, LE CONTRÔLEUR NE PARLE PLUS. C'était sa dernière phrase : il a
            // constaté la perte de communication et passé la main. Toute clairance ultérieure
            // de sa part est supprimée dans SpeakRawAsync, jusqu'à ce que le pilote transmette.
            _controllerGaveUp = true;
            Diagnostics.FileLog.Write("[panne radio] le contrôleur se retire — seule l'armée émet désormais.");

            // Le chasseur arrive AVANT de parler : on l'entend une fois qu'il est là.
            _intercept?.Launch(_context);
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (PilotSpokeSince(startedAt)) return;

            // RIEN DANS LE CIEL, RIEN À LA RADIO. Sans appareil réellement créé (option
            // coupée, titre inconnu, simulateur absent), on se tait : annoncer « vous êtes
            // intercepté » à un pilote qui ne voit personne à côté de lui casse l'immersion
            // bien plus sûrement qu'un silence. L'annonce de panne radio, elle, se suffit.
            if (_intercept is not { HasAircraft: true })
            {
                Diagnostics.FileLog.Write(
                    "[intercepteur] aucun appareil créé -> transmission militaire tue.");
                return;
            }

            await SpeakRawAsync(AtcPhrases.MilitaryIntercept(lang).Replace("{callsign}", callsign),
                                MilitaryVoice(lang), fromMilitary: true).ConfigureAwait(false);

            // L'INTERCEPTION PROGRESSE au lieu de se répéter. On constate l'absence de
            // réaction, on ordonne le transpondeur, on annonce l'escorte, puis le déroutement :
            // c'est une conversation qui avance, et non une phrase rejouée en boucle — laquelle
            // trahissait la machine dès la deuxième fois.
            foreach (string line in AtcPhrases.MilitaryFollowUp(lang))
            {
                await Task.Delay(TimeSpan.FromSeconds(MilitaryRepeatSeconds)).ConfigureAwait(false);

                if (PilotSpokeSince(startedAt)) return;
                if (_intercept is not { HasAircraft: true }) return;   // il est reparti

                await SpeakRawAsync(line.Replace("{callsign}", callsign),
                                    MilitaryVoice(lang), fromMilitary: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Diagnostics.FileLog.Exception("annonce de panne radio", ex); }
    }

    /// <summary>
    /// Silence entre deux transmissions de l'intercepteur. La longueur de l'échange n'est plus
    /// un nombre de répétitions mais celle de la séquence elle-même (AtcPhrases.MilitaryFollowUp).
    /// </summary>
    private const int MilitaryRepeatSeconds = 25;

    /// <summary>Le pilote a-t-il transmis depuis cet instant ? Toute parole arrête l'escalade.</summary>
    private bool PilotSpokeSince(DateTime utc)
    {
        lock (_stateLock) return _lastPilotUtc > utc;
    }

    /// <summary>
    /// Retire l'indicatif en tête d'instruction avant de la répéter : « Air France 1462, je
    /// répète : Air France 1462, piste 26… » sonnerait comme un bégaiement.
    /// </summary>
    private static string StripCallsign(string instruction, string callsign)
    {
        string trimmed = instruction.TrimStart();
        if (trimmed.StartsWith(callsign, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[callsign.Length..].TrimStart(' ', ',');
        return trimmed;
    }

    // Émet un texte déjà rédigé (réponse du brain) sans passer par le générateur.
    // On MET EN FILE (au lieu de laisser tomber) : réponses pilote et transferts sont trop
    // importants pour être perdus si l'ATC est déjà en train de parler. Garde-fou : si le
    // canal reste bloqué > 30 s, on abandonne cette transmission plutôt que de figer.
    /// <summary>
    /// Le contrôleur a-t-il renoncé ? Vrai à partir de sa dernière transmission, faux dès que
    /// le pilote reprend la parole. Voir <see cref="EscalateNoRadioAsync"/>.
    /// </summary>
    private volatile bool _controllerGaveUp;

    private bool _noVoiceWarned;

    /// <summary>
    /// Signale UNE FOIS l'absence de voix. Répété à chaque transmission supprimée, le message
    /// noierait le journal — alors que le problème, lui, ne change pas tant que l'utilisateur
    /// n'a rien téléchargé.
    /// </summary>
    private void WarnNoVoiceOnce()
    {
        if (_noVoiceWarned) return;
        _noVoiceWarned = true;

        Diagnostics.FileLog.Write("[voix] ATC désactivé : aucune voix installée.");
        TransmissionText?.Invoke(Loc.T("S.Status.NoVoiceDetail"));
    }

    /// <summary>
    /// Parole du contrôleur. <paramref name="fromMilitary"/> distingue l'intercepteur, qui est
    /// le SEUL à pouvoir encore émettre une fois le contrôleur retiré.
    /// </summary>
    private async Task SpeakRawAsync(string text, TtsVoice? voice = null, bool fromMilitary = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // PAS DE VOIX INSTALLÉE, PAS D'ATC. Laisser passer reviendrait à faire parler la
        // synthèse de Windows — dans la langue du système — à la place du contrôleur, sans
        // que rien ne le signale. Le pilote croirait entendre le logiciel.
        if (!_tts.IsReady)
        {
            SetStatus(Loc.T("S.Status.NoVoice"));
            WarnNoVoiceOnce();
            return;
        }

        // LE CONTRÔLEUR S'EST TU, ET IL RESTE TU. Après avoir constaté la perte de
        // communication et fait décoller une patrouille, il ne redonne pas de clairances : ce
        // serait se contredire à voix haute. Toute transmission de sa part est donc supprimée
        // jusqu'à ce que le pilote se manifeste — c'est ce silence qui donne son poids à
        // l'interception, et l'armée, elle, garde la parole.
        if (_controllerGaveUp && !fromMilitary)
        {
            Diagnostics.FileLog.Write($"[panne radio] transmission ATC supprimée : {text}");
            return;
        }
        try
        {
            SetStatus(Loc.T("S.Status.Transmitting"));

            // SYNTHÈSE ET DÉLAI EN PARALLÈLE. Ils s'additionnaient : on attendait le « temps
            // de réflexion » du contrôleur, PUIS on fabriquait l'audio. La réponse coûtait
            // donc délai + synthèse, alors que le pilote n'entend qu'une seule attente.
            // Lancés ensemble, le coût tombe au PLUS LONG des deux — la synthèse se fait
            // pendant la pause, qui n'est plus perdue.
            var chosen = voice ?? AtcVoice();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var synthesis = _tts.SynthesizeAsync(text, chosen);

            int delayMs = Math.Clamp(_settings.Current.AtcResponseDelayMs, 0, 3000);
            var pause = Task.Delay(delayMs + _rng.Next(0, 150)); // gigue : deux réponses ne tombent pas au même rythme

            TtsAudio audio = await synthesis;
            sw.Stop();
            if (sw.ElapsedMilliseconds > 900)
                Diagnostics.FileLog.Write($"[voix] synthèse lente : {sw.ElapsedMilliseconds} ms " +
                                          $"(voix « {chosen.Name} », {text.Length} caractères)");

            await pause;                  // déjà écoulée si la synthèse a été plus longue
            TransmissionText?.Invoke(text);
            // Canal voix partagé (ATC / copilote / ambiance) : on met en file, garde-fou 30 s.
            await _voice.SpeakAsync(audio, _settings.Current.ToRadioProfile(), TimeSpan.FromSeconds(30));
            SetStatus(Loc.T("S.Status.Ready"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("S.Status.AudioError"));
            System.Diagnostics.Debug.WriteLine("[WilcoATC/Atc] " + ex);
        }
    }

    // ------------------------------------------------------------------ émission

    private async Task SpeakAsync(AtcTrigger trigger)
    {
        // L'auto respecte l'interrupteur ATC ; le test manuel marche toujours.
        if (trigger == AtcTrigger.InitialContact && !_settings.Current.AtcEnabled) return;

        if (!_tts.IsReady) { SetStatus(Loc.T("S.Status.NoVoice")); WarnNoVoiceOnce(); return; }

        // Ce chemin-ci NE PASSE PAS par SpeakRawAsync : il synthétise et émet directement.
        // Il lui faut donc sa propre garde, sans quoi le contrôleur qui vient d'annoncer qu'il
        // renonce reprendrait contact tout seul à la fréquence suivante. Le test manuel, lui,
        // reste toujours possible : c'est l'utilisateur qui appuie.
        if (trigger == AtcTrigger.InitialContact && _controllerGaveUp)
        {
            Diagnostics.FileLog.Write("[panne radio] contact initial supprimé : le contrôleur s'est retiré.");
            return;
        }

        if (_voice.IsBusy) return; // déjà en train de parler -> contact auto/test non critique
        try
        {
            SetStatus(Loc.T("S.Status.Transmitting"));
            var flight = BuildSnapshot();

            // Délai réaliste avant de « répondre ».
            int delay = trigger == AtcTrigger.ManualTest ? _rng.Next(150, 500) : _rng.Next(1500, 4000);
            await Task.Delay(delay);

            string text = await _generator.GenerateAsync(flight, trigger);
            if (string.IsNullOrWhiteSpace(text)) { SetStatus(Loc.T("S.Status.Ready")); return; }

            TransmissionText?.Invoke(text);

            TtsAudio audio = await _tts.SynthesizeAsync(text, AtcVoice());
            // Non critique : on abandonne si le canal s'est occupé entre-temps.
            await _voice.SpeakAsync(audio, _settings.Current.ToRadioProfile(), TimeSpan.Zero);

            SetStatus(Loc.T("S.Status.Ready"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("S.Status.AudioError"));
            System.Diagnostics.Debug.WriteLine("[WilcoATC/Atc] " + ex);
        }
    }

    private FlightSnapshot BuildSnapshot()
    {
        var r = _radio; var c = _context; var a = _aircraft;

        // Indicatif parlé : plan de vol (SimBrief) en priorité, sinon immat phonétique.
        string callsign = _callsigns.Speak(a?.TailNumber);

        double hz = r?.Com1ActiveHz ?? 0;
        string? station = (r is not null && c is not null)
            ? _stations.Resolve(hz, c.Latitude, c.Longitude)
            : null;

        var rules = _flightContext.RulesDecision;

        return new FlightSnapshot(
            Callsign: callsign,
            AircraftTitle: a?.Title ?? "",
            OnGround: c?.OnGround ?? true,
            AltitudeMslFeet: c?.AltitudeMslFeet ?? 0,
            AltitudeAglFeet: c?.AltitudeAglFeet ?? 0,
            HeadingTrueDeg: c?.HeadingTrueDeg ?? 0,
            IasKnots: c?.IasKnots ?? 0,
            GroundSpeedKnots: c?.GroundSpeedKnots ?? 0,
            Com1ActiveMhz: FrequencyFormatter.FormatMHz(hz),
            Com1ActiveHz: hz,
            Station: station,
            NearestAirportIcao: c is null ? null : _stations.OperationalAirport(c.NearestAirportIcao, c.Latitude, c.Longitude),
            Latitude: c?.Latitude ?? 0,
            Longitude: c?.Longitude ?? 0,
            Rules: rules.Rules,
            Class: rules.Class);
    }

    private void SetStatus(string s) => StatusChanged?.Invoke(s);

    public void Dispose()
    {
        _sim.RadioSnapshotReceived -= OnRadio;
        _sim.ContextReceived -= OnContext;
        _sim.AircraftReceived -= OnAircraft;
        _sim.StateChanged -= OnState;
        _voice.Stop();
    }
}
