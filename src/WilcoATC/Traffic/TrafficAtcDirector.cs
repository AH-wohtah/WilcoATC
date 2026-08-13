using System.Diagnostics;
using WilcoATC.Atc;
using WilcoATC.Atc.Context;
using WilcoATC.Audio;
using WilcoATC.Common;
using WilcoATC.Diagnostics;
using WilcoATC.Formatting;
using WilcoATC.Settings;
using WilcoATC.Sim;
using WilcoATC.Stations;

namespace WilcoATC.Traffic;

/// <summary>
/// Un échange radio complet. <paramref name="PilotFirst"/> est l'appel initial de l'équipage,
/// quand il y en a un : une arrivée se signale AVANT que le contrôleur ne l'autorise, et
/// entendre la clairance sans la demande qui l'a provoquée sonne faux.
/// </summary>
public sealed record TrafficExchange(string Callsign, string Controller, string Readback,
                                     string? PilotFirst = null);

/// <summary>
/// DONNE UNE VOIX AU TRAFIC QUI EXISTE DÉJÀ.
///
/// L'idée tient en une phrase : le simulateur et ses extensions produisent déjà un trafic
/// abondant et crédible — cent quatorze appareils relevés à Roissy, avec de vraies compagnies
/// et de vraies livrées — mais il vole en silence. Plutôt que d'en fabriquer, on REGARDE
/// celui-là et on en PARLE.
///
/// Ce que cela change par rapport au bavardage d'ambiance : les indicatifs, les pistes et les
/// instants sont VRAIS. Quand la tour autorise « Air France 1234 » à atterrir en 27 droite,
/// l'appareil est réellement en courte finale sur cette piste, et le joueur le voit se poser
/// pendant qu'il l'entend. C'est la différence entre une bande-son et un monde.
///
/// Rien n'est écrit dans le simulateur. Ce directeur est en LECTURE SEULE — ce qui écarte du
/// même coup le déplacement saccadé des appareils pilotés de l'extérieur, défaut connu et sans
/// remède propre de l'approche inverse.
/// </summary>
public sealed class TrafficAtcDirector : IDisposable
{
    /// <summary>
    /// Rythme des relevés. Trois secondes, et non cinq : une course au décollage traverse la
    /// plage de vitesses où on la reconnaît en quelques secondes à peine, et un relevé trop
    /// espacé la manquait purement et simplement — l'appareil décollait en silence.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Rayon des relevés (mètres). 60 milles couvre largement les finales tout en gardant
    /// deux messages par appareil et par relevé à un volume raisonnable.
    /// </summary>
    private const uint RadiusMeters = 111_000;

    /// <summary>
    /// Distance à laquelle la tour délivre l'autorisation d'atterrissage. En pratique elle
    /// tombe entre 8 et 4 milles ; on vise le milieu de cette fenêtre.
    /// </summary>
    private const double LandingClearanceNm = 6.5;

    /// <summary>
    /// Silence minimal entre deux transmissions. Douze secondes laissent respirer la
    /// fréquence tout en la peuplant : à vingt, une tour comme Roissy paraissait déserte.
    /// </summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(12);

    private readonly ISimConnectService _sim;
    private readonly TrafficPicture _picture;
    private readonly RunwayRepository _runways;
    private readonly FlightContextProvider _flight;
    private readonly ITtsEngine _tts;
    private readonly VoiceBus _voice;
    private readonly VoicePicker _picker;
    private readonly SettingsService _settings;
    private readonly System.Threading.Timer _timer;

    /// <summary>
    /// Événements déjà émis : (appareil, type) -> quand. Sans cette mémoire, un appareil en
    /// finale se ferait autoriser à l'atterrissage à chaque relevé.
    /// </summary>
    private readonly Dictionary<(uint ObjectId, string Kind), DateTime> _fired = new();

    /// <summary>
    /// Délai après lequel un même événement peut se reproduire pour un même appareil. Assez
    /// long pour qu'on ne radote pas, assez court pour qu'un appareil qui refait un tour de
    /// piste — ou qui revient en fin de session — soit à nouveau pris en compte.
    /// </summary>
    private static readonly TimeSpan Rearm = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Priorité des transmissions de REMPLISSAGE — celles qui meublent sans rien décider.
    /// Elles perdent systématiquement face à une clairance, et sont en plus rationnées : elles
    /// sont disponibles pour tout appareil en approche, en permanence, ce qui les rendait
    /// omniprésentes. Une autorisation d'atterrissage, elle, n'existe qu'à un instant donné.
    /// </summary>
    private const int FillerPriority = 9;

    /// <summary>
    /// Intervalle minimal entre deux phrases de remplissage. Quarante-cinq secondes : assez
    /// pour ne plus tourner en boucle, assez peu pour qu'une tour dont tout le trafic est en
    /// approche — le cas le plus courant avec l'injection — ne paraisse pas éteinte.
    /// </summary>
    private static readonly TimeSpan FillerGap = TimeSpan.FromSeconds(45);

    private DateTime _lastFiller = DateTime.MinValue;

    private DateTime _lastCensus = DateTime.MinValue;
    private static readonly TimeSpan CensusInterval = TimeSpan.FromSeconds(30);

    private DateTime _lastSpoke = DateTime.MinValue;

    /// <summary>Nom de la station courante, pour étiqueter le contrôleur dans le journal.</summary>
    private string _station = "";

    /// <summary>Piste en service et position du terrain, réévaluées à chaque relevé.</summary>
    private string? _activeRunway;
    private double _apLat, _apLon;
    private bool _connected;
    private ContextSnapshot? _context;

    /// <summary>
    /// Une ligne RÉELLEMENT prononcée : l'interlocuteur, puis son texte. Levé au moment de la
    /// prise de parole, et non à la décision — un collationnement abandonné parce que le canal
    /// s'est libéré entre-temps ne doit pas apparaître dans le journal comme s'il avait été
    /// entendu.
    /// </summary>
    public event Action<string, string>? Said;

    /// <summary>
    /// La radio porte-t-elle en ce moment du trafic RÉEL ?
    ///
    /// Sert à faire taire le trafic d'ambiance, qui invente ses échanges. Les deux fonctions
    /// sont inconciliables : entendre « Vueling 8620, request pushback » alors qu'aucun
    /// Vueling n'est garé nulle part détruit exactement l'illusion que le trafic réel
    /// construit. Quand il y a de vrais appareils à faire parler, ils ont la fréquence pour
    /// eux ; quand il n'y en a aucun — terrain désert, trafic coupé — l'ambiance reprend son
    /// rôle, qui est de meubler un silence, pas de contredire ce qu'on voit.
    /// </summary>
    public bool IsVoicingRealTraffic => _settings.Current.TrafficAtcEnabled && _picture.Count > 0;

    /// <summary>
    /// SILENCE RADIO : un appareil est en détresse, la fréquence lui appartient.
    ///
    /// Sur une vraie fréquence, le contrôleur impose le silence à tout le monde le temps de
    /// traiter un mayday. Continuer à faire rouler et atterrir le trafic pendant ce temps
    /// n'est pas seulement irréaliste : ça noie les transmissions qui comptent, au moment
    /// précis où le pilote a le plus besoin d'entendre.
    /// </summary>
    public Func<bool>? DistressInProgress { get; set; }

    public TrafficAtcDirector(ISimConnectService sim, TrafficPicture picture,
                              RunwayRepository runways, FlightContextProvider flight,
                              ITtsEngine tts, VoiceBus voice, VoicePicker picker,
                              SettingsService settings)
    {
        _sim = sim;
        _picture = picture;
        _runways = runways;
        _flight = flight;
        _tts = tts;
        _voice = voice;
        _picker = picker;
        _settings = settings;

        _sim.NearbyAircraftSeen += _picture.Observe;
        _sim.NearbyAircraftStateSeen += _picture.Observe;
        _sim.ContextReceived += OnContext;
        _sim.WeatherReceived += OnWeather;
        _sim.StateChanged += OnState;

        _timer = new System.Threading.Timer(_ => SafeTick(), null, Tick, Tick);
    }

    private void OnContext(ContextSnapshot c) => _context = c;

    /// <summary>
    /// Météo : elle sert à DEUX choses, la piste en service et le calage altimétrique.
    ///
    /// C'est ce qui séparait le plus mes transmissions de celles du simulateur. Là où il
    /// annonce « QNH 1014, approche autorisée ILS piste 12 », je disais « continue inbound » —
    /// non par impossibilité, mais parce que je ne consultais pas une météo que j'avais déjà.
    /// </summary>
    private void OnWeather(WeatherSnapshot w) => _weather = w;

    private WeatherSnapshot? _weather;

    /// <summary>
    /// Piste EN SERVICE du terrain, déduite du vent. Elle sert de repli quand on ne sait pas
    /// sur quelle piste va un appareil : au roulage, personne n'est aligné sur quoi que ce
    /// soit, et « point d'arrêt piste 12 » vaut infiniment mieux que « suivez le balisage ».
    /// </summary>
    private string? ActiveRunway(string icao) =>
        _weather is { } w
            ? _runways.Active(icao, w.WindDirectionTrueDeg, w.WindSpeedKnots, 0)?.Ident
            : null;

    /// <summary>
    /// Calage altimétrique parlé, « QNH 1014 ». Vide si la météo n'est pas encore arrivée :
    /// mieux vaut une clairance sans QNH qu'un QNH inventé.
    /// </summary>
    private string Qnh() =>
        _weather is { SeaLevelPressureHpa: > 800 and < 1100 } w
            ? $"QNH {Math.Round(w.SeaLevelPressureHpa):F0}, "
            : "";

    /// <summary>
    /// Direction d'où vient un appareil, vue du terrain : « 18 miles south ». C'est ce qui
    /// rend un compte rendu de position crédible — un contrôleur situe toujours son trafic.
    /// </summary>
    /// <summary>
    /// Forme comparable d'un indicatif : minuscules, sans espaces ni ponctuation.
    ///
    /// « AFR1234 », « Air France 1234 » et « airfrance-1234 » désignent le même avion, et la
    /// comparaison doit le voir — sans quoi la garde ne servirait à rien dès que les deux
    /// sources n'écrivent pas l'indicatif de la même façon, ce qui est le cas courant.
    /// </summary>
    private static string Simplify(string? callsign)
        => new string((callsign ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Bearing(double airportLat, double airportLon, NearbyAircraftState s)
    {
        double b = Geo.BearingDeg(airportLat, airportLon, s.Latitude, s.Longitude);
        string[] points = { "north", "north-east", "east", "south-east",
                            "south", "south-west", "west", "north-west" };
        return points[(int)Math.Round(b / 45.0) % 8];
    }

    private void OnState(ConnectionState state, string? _)
    {
        _connected = state == ConnectionState.Connected;
        if (!_connected) _fired.Clear();   // nouvelle session : les identifiants d'objet sont réattribués
    }

    private void SafeTick()
    {
        // Un timer qui laisse échapper une exception emporte le processus : ce directeur est
        // un agrément, il ne doit jamais faire tomber l'application.
        try { OnTick(); }
        catch (Exception ex)
        {
            // AU FICHIER, pas seulement dans le débogueur. Une exception avalée dans
            // Debug.WriteLine est invisible chez l'utilisateur : le directeur se tait, le
            // recensement continue de s'écrire, et rien ne dit que le tour a échoué.
            Debug.WriteLine("[WilcoATC/Traffic] " + ex);
            FileLog.Write($"[trafic] ERREUR dans le relevé : {ex.GetType().Name} — {ex.Message}");
        }
    }

    private void OnTick()
    {
        if (!_connected || !_settings.Current.TrafficAtcEnabled) return;

        // Détresse en cours : la fréquence est au pilote, on ne dit plus rien. Les relevés,
        // eux, continuent — ils ne font aucun bruit et alimentent le catalogue de titres.
        if (DistressInProgress?.Invoke() == true)
        {
            _sim.RequestNearbyAircraft(RadiusMeters);
            _sim.RequestNearbyAircraftState(RadiusMeters);
            _picture.Prune();
            Census("silence radio : détresse en cours");
            return;
        }

        // Les relevés tournent en permanence dès que la fonction est active : ils alimentent
        // aussi le catalogue de titres, et c'est de la lecture pure.
        _sim.RequestNearbyAircraft(RadiusMeters);
        _sim.RequestNearbyAircraftState(RadiusMeters);
        _picture.Prune();

        // Au menu ou sur la carte du monde, le joueur n'est aux commandes de rien : la radio
        // ne doit pas se mettre à vivre en arrière-plan.
        if (_context is not { InFlightSession: true }) { Census("hors session"); return; }

        var flight = _flight.Current();
        _station = SpokenStation(flight.StationName, flight.Controller);
        string? icao = flight.AirportIcao;
        if (string.IsNullOrWhiteSpace(icao)) { Census("aucun terrain de référence"); return; }

        var runways = _runways.For(icao);
        if (runways.Count == 0) { Census($"aucune piste connue pour {icao}"); return; }

        // Position du terrain, telle que le simulateur la publie. On ne la devine pas : sans
        // elle, aucune distance en finale n'a de sens.
        if (!_sim.TryGetAirportPosition(icao, out double apLat, out double apLon))
        { Census($"position de {icao} inconnue du simulateur"); return; }

        (_apLat, _apLon) = (apLat, apLon);
        _activeRunway = ActiveRunway(icao);

        // Analyse d'abord, décision ensuite : le recensement doit refléter TOUT ce qui bouge,
        // y compris quand on choisit de ne rien dire. C'est ce qui rend un silence explicable.
        var seen = new List<(TrafficAircraft Aircraft, TrafficSituation Situation)>();
        foreach (var t in _picture.Current())
        {
            var s = t.State;
            seen.Add((t, ApproachDetector.Analyse(
                s.Latitude, s.Longitude, s.AltitudeAglFeet, s.HeadingTrueDeg,
                s.GroundSpeedKnots, s.VerticalSpeedFpm, s.OnGround,
                apLat, apLon, runways)));
        }
        Census(null, icao, flight.Controller, seen);

        if (DateTime.UtcNow - _lastSpoke < MinGap) return;
        if (_voice.IsBusy) return;                       // jamais par-dessus une vraie clairance

        // Les appareils les plus PROCHES du terrain d'abord : ce sont eux dont le sort se
        // joue maintenant. Sans ce tri, un appareil à vingt milles pourrait accaparer la
        // fréquence pendant qu'un autre se pose sans un mot.
        // ON CHOISIT LA TRANSMISSION LA PLUS IMPORTANTE, pas la plus proche.
        //
        // Trier par distance donnait le pire résultat possible : les appareils qui viennent
        // d'apparaître en approche accaparaient la fréquence avec une phrase d'attente, servie
        // en boucle à chaque nouvel arrivant, pendant que les vraies clairances passaient après.
        // Une autorisation d'atterrissage prime sur un « rappelez en finale », toujours.
        // INDICATIF DU JOUEUR : personne d'autre ne doit le porter.
        string mine = Simplify(flight.Callsign);

        var candidates = new List<Candidate>();
        foreach (var (aircraft, situation) in seen)
        {
            if (aircraft.Callsign.Length == 0) continue;  // sans indicatif, on ne parle à personne

            // UN APPAREIL NE PEUT PAS S'APPELER COMME VOUS. Le simulateur fait naître ses
            // avions sans savoir quel indicatif vous portez : la collision arrive. Et quand
            // elle arrive, elle est ravageuse — la tour appelle « Air France 1234 », un
            // équipage de synthèse répond à votre place, et le contrôleur considère que vous
            // avez collationné. Vous perdez la main sans comprendre pourquoi.
            //
            // On préfère le silence : cet appareil-là ne sera jamais adressé.
            if (mine.Length > 0 && Simplify(aircraft.Callsign) == mine)
            {
                FileLog.Write($"[trafic] « {aircraft.Callsign} » ignoré : même indicatif que le joueur");
                continue;
            }

            var candidate = Decide(aircraft, situation, flight.Controller, _station);
            if (candidate is not null) candidates.Add(candidate);
        }

        var best = candidates.OrderBy(c => c.Priority).ThenBy(c => c.DistanceNm).FirstOrDefault();
        if (best is null) return;

        // Le remplissage n'a droit à la parole que de loin en loin. C'est lui qui tournait en
        // boucle : il est disponible en permanence, alors qu'une clairance ne l'est qu'à
        // l'instant précis où elle a lieu.
        if (best.Priority >= FillerPriority)
        {
            var since = DateTime.UtcNow - _lastFiller;
            if (since < FillerGap)
            {
                // Tracé une fois par recensement, pas à chaque tour : sinon cette ligne
                // noierait le journal toutes les trois secondes.
                if (DateTime.UtcNow - _lastCensus < TimeSpan.FromSeconds(3))
                    FileLog.Write($"[trafic] {candidates.Count} candidat(s), tous de remplissage — " +
                                  $"prochain dans {(FillerGap - since).TotalSeconds:F0} s");
                return;
            }
            _lastFiller = DateTime.UtcNow;
        }

        Fire(best.ObjectId, best.Kind);        // on ne consomme l'événement qu'en le prononçant
        _lastSpoke = DateTime.UtcNow;
        _ = SpeakAsync(best.Exchange);
    }

    /// <summary>Une transmission possible, avec ce qui permet de la départager.</summary>
    private sealed record Candidate(TrafficExchange Exchange, int Priority, string Kind,
                                    uint ObjectId, double DistanceNm);

    /// <summary>
    /// Choisit quoi dire, ou rien.
    ///
    /// LA FRÉQUENCE COMMANDE. On n'entend que ce qui se dit sur celle où l'on est calé : le
    /// sol donne des instructions de roulage, la tour autorise pistes et atterrissages,
    /// l'approche descend et aligne les arrivées. Diffuser tout sur tout serait plus bavard,
    /// et faux — c'est précisément ce qui trahissait le trafic d'ambiance.
    ///
    /// Chaque appareil n'est traité qu'une fois par type d'événement, avec réarmement au bout
    /// d'un moment : sans quoi un appareil au roulage se ferait guider toutes les trois
    /// secondes, et un appareil qui revient plus tard dans la session resterait muet.
    /// </summary>
    private Candidate? Decide(TrafficAircraft t, TrafficSituation situation,
                              ControllerType controller, string station)
    {
        string cs = t.Callsign;

        // PISTE EN SERVICE EN REPLI. Un appareil au roulage n'est aligné sur rien, et un
        // appareil encore loin non plus : sans repli, ils recevaient tous la version vague de
        // l'instruction. Or le terrain a bien une piste en service, déduite du vent — c'est
        // celle que le simulateur nomme, lui, dans ses propres clairances.
        string? runwayId = situation.Runway ?? _activeRunway;
        string rwy = RunwayFormatter.Speak(runwayId);
        bool namedRunway = runwayId is { Length: > 0 };
        string who = station.Length > 0 ? $", {station}" : "";

        // Fabrique un candidat. RIEN N'EST CONSOMMÉ ICI : l'événement n'est marqué comme émis
        // qu'une fois la transmission choisie, sinon un appareil écarté au profit d'un autre
        // perdrait sa clairance sans l'avoir entendue.
        Candidate? Offer(string kind, int priority, string controllerLine, string readback,
                         string? pilotFirst = null) =>
            Ready(t.ObjectId, kind)
                ? new Candidate(new TrafficExchange(cs, controllerLine, readback, pilotFirst),
                                priority, kind, t.ObjectId, situation.DistanceNm)
                : null;

        // D'où vient l'appareil, vu du terrain — « 18 miles south ». Calculé ici parce qu'il
        // dépend de l'appareil, pas du tour de relevé.
        string fromDirection = Bearing(_apLat, _apLon, t.State);

        // Variante stable par appareil : « Air France 1234 » aura toujours la même tournure,
        // mais deux appareils différents n'auront pas la même. C'est ce qui manquait le plus —
        // la même phrase servie à la chaîne s'entend comme un disque rayé.
        int v = (int)(t.ObjectId % 3);

        switch (controller, situation.Phase)
        {
            // ---------------------------------------------------------------- TOUR
            case (ControllerType.Tower, TrafficPhase.Final) when situation.DistanceNm <= LandingClearanceNm:
                return Offer("land", 0,
                    v switch
                    {
                        0 => $"{cs}{who}, {rwy}, cleared to land.",
                        1 => $"{cs}, wind calm, {rwy}, cleared to land.",
                        _ => $"{cs}, {rwy}, cleared to land, wind light and variable.",
                    },
                    $"Cleared to land {rwy}, {cs}.");

            case (ControllerType.Tower, TrafficPhase.DepartureRoll):
                return Offer("takeoff", 0,
                    v switch
                    {
                        0 => $"{cs}, {rwy}, cleared for takeoff.",
                        1 => $"{cs}, wind calm, {rwy}, cleared for takeoff.",
                        _ => $"{cs}, {rwy}, cleared for takeoff, no delay.",
                    },
                    $"Cleared for takeoff {rwy}, {cs}.");

            case (ControllerType.Tower, TrafficPhase.Departing):
                return Offer("handoff-dep", 1,
                    v == 0 ? $"{cs}, contact departure, good day."
                           : $"{cs}, radar service terminated, contact departure.",
                    $"Contact departure, {cs}, good day.");

            // Il s'est posé et il roule : la tour le renvoie au sol.
            case (ControllerType.Tower, TrafficPhase.Taxiing) when Fired(t.ObjectId, "land"):
                return Offer("vacate", 1,
                    v == 0 ? $"{cs}, vacate when able, contact ground."
                           : $"{cs}, vacate next taxiway, monitor ground.",
                    $"Vacating, ground, {cs}.");

            // LA TOUR ASSURE AUSSI LE SOL sur la plupart des terrains — une seule fréquence y
            // traite roulage et piste. Réserver le roulage à une fréquence « Ground » qui
            // n'existe pas rendait le directeur muet devant des appareils qu'il voyait bouger.
            case (ControllerType.Tower or ControllerType.Ground, TrafficPhase.Taxiing):
                return namedRunway
                    ? Offer("taxi", 2,
                        v == 0 ? $"{cs}, taxi to holding point {rwy}, hold short."
                               : $"{cs}, continue taxi, hold short {rwy}.",
                        $"Holding short {rwy}, {cs}.")
                    : Offer("taxi", 2,
                        v switch
                        {
                            0 => $"{cs}, continue taxi, follow the greens.",
                            1 => $"{cs}, taxi via the outer, give way to traffic on your right.",
                            _ => $"{cs}, continue taxi to the apron.",
                        },
                        $"Continue taxi, {cs}.");

            case (ControllerType.Ground, TrafficPhase.DepartureRoll):
                return Offer("gnd-twr", 1,
                    $"{cs}, contact tower, good day.",
                    $"Contact tower, {cs}.");

            // L'ARRIVÉE SE PRÉSENTE, et c'est l'ÉQUIPAGE qui parle en premier : il annonce sa
            // position, le contrôleur répond par une clairance d'approche avec le calage.
            // C'est la forme réelle de l'échange, et celle que le simulateur emploie dans ses
            // propres messages — « 18 nautiques sud, en trajectoire d'approche ILS piste 12 »,
            // puis « QNH 1014, approche autorisée ILS piste 12 ».
            case (ControllerType.Tower, TrafficPhase.Inbound) when namedRunway:
                return Offer("sequence", FillerPriority,
                    $"{cs}{who}, {Qnh()}cleared ILS approach {rwy}, report established.",
                    $"Cleared ILS approach {rwy}, {cs}.",
                    pilotFirst: $"{station}, {cs}, {situation.DistanceNm:F0} miles {fromDirection}, " +
                                $"inbound ILS {rwy}.");

            case (ControllerType.Tower, TrafficPhase.Inbound):
                return Offer("sequence", FillerPriority,
                    v == 0 ? $"{cs}{who}, {Qnh()}report field in sight."
                           : $"{cs}, no reported traffic, continue inbound.",
                    $"Wilco, {cs}.",
                    pilotFirst: $"{station}, {cs}, {situation.DistanceNm:F0} miles {fromDirection}, inbound.");

            // ---------------------------------------------------------------- APPROCHE
            case (ControllerType.Approach, TrafficPhase.Inbound):
                return namedRunway
                    ? Offer("approach", 1,
                        v == 0 ? $"{cs}{who}, descend three thousand, cleared ILS approach {rwy}."
                               : $"{cs}, turn left heading two seven zero, cleared ILS {rwy}.",
                        $"Cleared ILS approach {rwy}, {cs}.")
                    : Offer("approach", FillerPriority,
                        $"{cs}{who}, radar contact, expect vectors for the approach.",
                        $"Expecting vectors, {cs}.");

            case (ControllerType.Approach, TrafficPhase.Final):
                return Offer("app-twr", 1,
                    $"{cs}, contact tower, good day.",
                    $"Contact tower, {cs}.");

            // ---------------------------------------------------------------- DÉPART
            case (ControllerType.Departure, TrafficPhase.Departing):
                return Offer("climb", 1,
                    v == 0 ? $"{cs}{who}, radar contact, climb flight level one hundred."
                           : $"{cs}, radar contact, resume own navigation, climb flight level one hundred.",
                    $"Climb flight level one hundred, {cs}.");
        }

        return null;
    }

    /// <summary>
    /// Nom PRONONÇABLE de la station : « Amílcar Cabral Tower », pas « Amílcar Cabral
    /// International Airport ».
    ///
    /// La base de stations donne le nom administratif du terrain, qui n'a jamais été un
    /// indicatif radio. Aucune tour ne s'annonce comme un aéroport, et lu par la synthèse
    /// vocale au milieu d'une clairance, c'est le détail qui trahit immédiatement la machine.
    /// On retire donc les mots d'aéroport et on ajoute le rôle réellement tenu.
    /// </summary>
    private static string SpokenStation(string name, ControllerType controller)
    {
        string s = (name ?? "").Trim();
        if (s.Length == 0) return "";

        foreach (var noise in new[] { "International Airport", "Regional Airport", "Airport",
                                      "Aerodrome", "Airfield", "Intl" })
            s = s.Replace(noise, "", StringComparison.OrdinalIgnoreCase);

        s = s.Trim(' ', '-', ',');
        if (s.Length == 0) return "";

        string role = controller switch
        {
            ControllerType.Tower => "Tower",
            ControllerType.Ground => "Ground",
            ControllerType.Approach => "Approach",
            ControllerType.Departure => "Departure",
            ControllerType.Center => "Center",
            _ => "",
        };

        // Le rôle n'est ajouté que s'il n'y est pas déjà : certaines stations le portent
        // dans leur nom, et « Sal Tower Tower » serait pire que le nom d'origine.
        if (role.Length == 0 || s.Contains(role, StringComparison.OrdinalIgnoreCase)) return s;
        return $"{s} {role}";
    }

    /// <summary>
    /// Cet événement a-t-il DÉJÀ eu lieu pour cet appareil (sans le consommer) ?
    /// </summary>
    private bool Fired(uint objectId, string kind) => _fired.ContainsKey((objectId, kind));

    /// <summary>
    /// Cet événement est-il DISPONIBLE pour cet appareil ? Ne consomme rien — c'est ce qui
    /// permet d'examiner tous les candidats avant d'en retenir un seul.
    /// </summary>
    private bool Ready(uint objectId, string kind) =>
        !_fired.TryGetValue((objectId, kind), out var when) || DateTime.UtcNow - when >= Rearm;

    /// <summary>
    /// Marque un événement comme émis. Appelé UNIQUEMENT sur la transmission retenue : marquer
    /// tous les candidats examinés ferait taire des appareils qui n'ont jamais été entendus.
    /// </summary>
    private bool Fire(uint objectId, string kind)
    {
        var key = (objectId, kind);
        var now = DateTime.UtcNow;

        if (_fired.TryGetValue(key, out var when) && now - when < Rearm) return false;

        _fired[key] = now;
        return true;
    }

    /// <summary>
    /// Écrit dans le journal ce que le directeur VOIT et pourquoi il se tait. Sans cela, un
    /// silence est indiscernable d'une panne — et c'est exactement ce qui s'est produit :
    /// soixante-six appareils autour, aucune transmission, aucun moyen de savoir lequel des
    /// dix filtres avait mordu.
    /// </summary>
    private void Census(string? blocker, string? icao = null,
                        ControllerType controller = ControllerType.Unknown,
                        List<(TrafficAircraft Aircraft, TrafficSituation Situation)>? seen = null)
    {
        if (DateTime.UtcNow - _lastCensus < CensusInterval) return;
        _lastCensus = DateTime.UtcNow;

        if (blocker is not null)
        {
            FileLog.Write($"[trafic] muet : {blocker} ({_picture.Count} appareil(s) suivis)");
            return;
        }

        var counts = (seen ?? new()).GroupBy(x => x.Situation.Phase)
                                    .ToDictionary(g => g.Key, g => g.Count());
        string detail = string.Join(", ",
            counts.Where(kv => kv.Key != TrafficPhase.None).Select(kv => $"{kv.Value} {kv.Key}"));
        int withCallsign = (seen ?? new()).Count(x => x.Aircraft.Callsign.Length > 0);

        FileLog.Write($"[trafic] {icao} sur {controller} — {seen?.Count ?? 0} appareil(s), " +
                      $"{withCallsign} avec indicatif" +
                      (detail.Length > 0 ? $" — {detail}" : " — aucun en mouvement utile"));

        // LE DÉTAIL DES PLUS PROCHES. « Aucun en mouvement utile » ne dit pas s'ils sont
        // garés ou si la géométrie se trompe — et ce sont deux problèmes opposés : dans un
        // cas il n'y a rien à dire, dans l'autre on rate ce qu'il y avait à dire. Les chiffres
        // bruts tranchent en une ligne, là où quatre itérations à l'aveugle n'y suffisaient pas.
        // CE QUI BOUGE D'ABORD. Trier par distance faisait remonter les cinq appareils garés
        // sur le tablier voisin, à deux cents mètres et zéro nœud — les seuls dont on n'a rien
        // à dire. Les appareils en mouvement passent devant, quitte à être plus loin.
        var interesting = (seen ?? new())
            .OrderBy(x => x.Situation.Phase == TrafficPhase.None ? 1 : 0)
            .ThenBy(x => x.Situation.DistanceNm);

        foreach (var (a, sit) in interesting.Take(5))
        {
            var st = a.State;
            FileLog.Write($"[trafic]   {a.Callsign,-18} {sit.DistanceNm,6:F1}nm " +
                          $"{st.AltitudeAglFeet,7:F0}ft/sol {st.GroundSpeedKnots,5:F0}kt " +
                          $"{st.VerticalSpeedFpm,+7:F0}fpm {(st.OnGround ? "au sol" : "en vol")} " +
                          $"cap {st.HeadingTrueDeg,3:F0} -> {sit.Phase}");
        }
    }

    /// <summary>
    /// Joue l'échange : le contrôleur, puis le collationnement de l'équipage — chacun avec sa
    /// propre voix. C'est l'aller-retour qui fait vivant ; une instruction sans réponse sonne
    /// comme une annonce de gare.
    /// </summary>
    private async Task SpeakAsync(TrafficExchange exchange)
    {
        var profile = _settings.Current.ToRadioProfile();
        profile.Volume = Math.Clamp(profile.Volume * 0.75, 0, 1);

        const AtcLanguage lang = AtcLanguage.English;
        string controllerName = _station.Length > 0 ? _station : "TOWER";

        // L'INTENTION est tracée avant la tentative. Sans cela, un échec de synthèse ou un
        // canal occupé ne laissent aucune trace : le journal reste vide, exactement comme si
        // le directeur n'avait rien trouvé à dire — deux causes opposées, un même silence.
        FileLog.Write($"[trafic] transmission : {exchange.Controller}");

        // L'APPEL DE L'ÉQUIPAGE D'ABORD, quand il y en a un. Une clairance d'approche qui
        // tombe sans que personne ne l'ait demandée s'entend comme une annonce ; précédée du
        // compte rendu de position, elle devient une conversation.
        if (exchange.PilotFirst is { Length: > 0 } opening)
        {
            if (!await SayAsync(opening, _picker.For(exchange.Callsign, lang), profile, TimeSpan.Zero))
            {
                FileLog.Write("[trafic] non prononcée (canal occupé ou synthèse en échec)");
                return;
            }
            Announce(exchange.Callsign, opening);
            await Task.Delay(600).ConfigureAwait(false);
        }

        // Le contrôleur abandonne si le canal est pris ; le collationnement, lui, attend un
        // peu — couper un échange en deux serait pire que de ne rien dire.
        var controllerWait = exchange.PilotFirst is { Length: > 0 }
            ? TimeSpan.FromSeconds(6)   // l'équipage vient de parler : on ne coupe pas l'échange
            : TimeSpan.Zero;

        if (!await SayAsync(exchange.Controller, _picker.For("TOWER", lang), profile, controllerWait))
        {
            FileLog.Write("[trafic] non prononcée (canal occupé ou synthèse en échec)");
            return;
        }

        Announce(controllerName, exchange.Controller);

        await Task.Delay(700).ConfigureAwait(false);

        if (await SayAsync(exchange.Readback, _picker.For(exchange.Callsign, lang), profile,
                           TimeSpan.FromSeconds(6)).ConfigureAwait(false))
            Announce(exchange.Callsign, exchange.Readback);
    }

    /// <summary>Publie une ligne prononcée : journal de l'application ET fichier journal.</summary>
    private void Announce(string speaker, string text)
    {
        Said?.Invoke(speaker, text);
        FileLog.Write($"[trafic] {speaker}: {text}");
    }

    private async Task<bool> SayAsync(string text, TtsVoice voice, RadioProfile profile, TimeSpan wait)
    {
        try
        {
            TtsAudio audio = await _tts.SynthesizeAsync(text, voice).ConfigureAwait(false);
            return await _voice.SpeakAsync(audio, profile, wait, priority: VoicePriority.Ambient)
                               .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WilcoATC/Traffic] " + ex);
            FileLog.Write($"[trafic] synthèse impossible : {ex.GetType().Name} — {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _sim.NearbyAircraftSeen -= _picture.Observe;
        _sim.NearbyAircraftStateSeen -= _picture.Observe;
        _sim.ContextReceived -= OnContext;
        _sim.StateChanged -= OnState;
    }
}
