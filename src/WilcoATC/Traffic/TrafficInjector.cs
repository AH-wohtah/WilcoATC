using System.Diagnostics;
using System.IO;
using WilcoATC.Common;
using WilcoATC.Diagnostics;
using WilcoATC.Settings;
using WilcoATC.Sim;

namespace WilcoATC.Traffic;

/// <summary>
/// FAIT NAÎTRE DU TRAFIC là où il n'y en a pas assez.
///
/// LE PRINCIPE, ET CE QUI LE DISTINGUE DE L'INTERCEPTEUR : les appareils créés ici reçoivent
/// un PLAN DE VOL, et c'est le simulateur qui les pilote — roulage, décollage, montée,
/// approche, atterrissage. Nous n'écrivons jamais leur position. C'est la seule façon
/// d'obtenir un vol fluide : un objet poussé de l'extérieur image par image ne peut pas
/// l'être, c'est un défaut documenté de l'API, et la parade historique (le mode slew) ne
/// fonctionne plus dans Microsoft Flight Simulator 2024.
///
/// TROIS SOURCES DE VIE, dans l'ordre où on les remarque depuis le cockpit :
///   • des ARRIVÉES, qui apparaissent en approche et viennent se poser sous vos yeux ;
///   • des DÉPARTS, qui partent du terrain et s'en vont ;
///   • des APPAREILS GARÉS, qui remplissent les postes de stationnement.
///
/// TOUT EST RETIRÉ à l'arrêt : on ne laisse jamais d'appareil orphelin dans la session.
/// </summary>
public sealed class TrafficInjector : IDisposable
{
    /// <summary>
    /// Cadence des créations. Huit secondes : de quoi peupler un terrain en une minute ou
    /// deux, sans faire naître vingt appareils d'un coup — ce que le simulateur digère mal.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Distance à laquelle naissent les arrivées. Assez loin pour que l'approche soit
    /// crédible, assez près pour que l'atterrissage arrive avant qu'on ait changé de terrain.
    /// </summary>
    private const double ArrivalSpawnNm = 18;

    /// <summary>Altitude d'apparition d'une arrivée aux instruments, au-dessus du terrain.</summary>
    private const int ArrivalAltitudeFeet = 5000;

    /// <summary>
    /// Part de vols À VUE parmi les vols injectés, en pourcentage.
    ///
    /// Sans ce mélange, tout le trafic est un avion de ligne aux instruments : c'était le cas,
    /// et un aérodrome n'a alors plus rien d'un aérodrome. Les vols à vue apportent ce que
    /// l'IFR ne donne jamais — des appareils légers, bas, lents, des tours de piste. La part
    /// n'a de sens que si le catalogue contient des appareils légers ; sinon elle est ignorée.
    /// </summary>
    private const int VfrPercent = 45;

    /// <summary>Un vol à vue tourne bas : le circuit se vole entre mille et deux mille pieds.</summary>
    private const int VfrCircuitAltitudeFeet = 1500;

    /// <summary>Étape maximale d'un vol à vue : on ne traverse pas un océan en VFR.</summary>
    private const double MaxVfrLegNm = 120;

    /// <summary>
    /// Étape minimale d'un vol injecté. En deçà, l'appareil ferait demi-tour avant d'avoir
    /// rentré le train.
    /// </summary>
    private const double MinLegNm = 25;

    /// <summary>
    /// Étape MAXIMALE. Sans ce plafond, le cache d'aéroports du simulateur — qui ne se limite
    /// pas au voisinage immédiat — envoyait les départs du Cap-Vert vers les îles Salomon.
    /// </summary>
    private const double MaxLegNm = 400;

    /// <summary>Étape minimale d'une ARRIVÉE : il faut de la place pour apparaître en approche.</summary>
    private const double MinArrivalLegNm = 60;

    private readonly ISimConnectService _sim;
    private readonly SimTitleCatalog _catalog;
    private readonly SettingsService _settings;
    private readonly System.Threading.Timer _timer;
    private readonly Random _rng = new();

    /// <summary>Répertoire des plans générés. Nettoyé au démarrage : ils ne survivent pas à la session.</summary>
    private static readonly string PlansDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WilcoATC", "traffic-plans");

    private readonly List<uint> _created = new();
    private readonly object _gate = new();

    private ContextSnapshot? _context;
    private bool _connected;
    private int _serial;

    public TrafficInjector(ISimConnectService sim, SimTitleCatalog catalog, SettingsService settings)
    {
        _sim = sim;
        _catalog = catalog;
        _settings = settings;

        _sim.ContextReceived += OnContext;
        _sim.StateChanged += OnState;
        _sim.AircraftReceived += OnPlayerAircraft;
        _sim.TrafficAircraftCreated += OnCreated;

        _timer = new System.Threading.Timer(_ => SafeTick(), null, TimeSpan.FromSeconds(25), Tick);
    }

    /// <summary>Nombre d'appareils actuellement injectés par nous.</summary>
    public int Count { get { lock (_gate) return _created.Count; } }

    private void OnContext(ContextSnapshot c) => _context = c;

    /// <summary>
    /// TITRE DE L'APPAREIL DU JOUEUR — retenu pour ne JAMAIS le faire naître.
    ///
    /// Le catalogue de titres se remplit d'abord avec l'avion piloté : c'est la première chose
    /// que le simulateur nous montre, et longtemps la seule. Un utilisateur qui active
    /// l'injection à son premier vol voit donc apparaître des copies de SON PROPRE APPAREIL —
    /// même modèle, même livrée, et avec les gros modules (Fenix, PMDG) la même immatriculation,
    /// celle-ci venant de la livrée et non du numéro qu'on demande. Le résultat est un sosie
    /// qui roule et vole à côté de vous : c'est le « ghost » que les utilisateurs décrivent.
    /// </summary>
    private void OnPlayerAircraft(AircraftSnapshot a)
    {
        string title = a.Title?.Trim() ?? "";
        if (title.Length > 0) _playerTitle = title;
    }

    private volatile string _playerTitle = "";

    /// <summary>Ce titre est-il celui de l'appareil du joueur ?</summary>
    private bool IsPlayerTitle(string title)
        => _playerTitle.Length > 0 && string.Equals(title, _playerTitle, StringComparison.OrdinalIgnoreCase);

    private void OnCreated(uint objectId)
    {
        lock (_gate) _created.Add(objectId);
    }

    private void OnState(ConnectionState state, string? _)
    {
        _connected = state == ConnectionState.Connected;

        // À la déconnexion, les identifiants ne veulent plus rien dire : on oublie sans
        // chercher à retirer quoi que ce soit, le monde ayant disparu avec la session.
        if (!_connected) { lock (_gate) _created.Clear(); }
    }

    private void SafeTick()
    {
        try { OnTick(); }
        catch (Exception ex) { Debug.WriteLine("[WilcoATC/Injection] " + ex); }
    }

    private void OnTick()
    {
        if (!_connected) return;

        var s = _settings.Current;
        if (!s.TrafficInjectionEnabled) return;
        if (_context is not { InFlightSession: true } ctx) return;

        int target = Math.Clamp(s.TrafficInjectionCount, 0, 40);
        lock (_gate) if (_created.Count >= target) return;

        string? icao = ctx.NearestAirportIcao;
        if (string.IsNullOrWhiteSpace(icao)) { Log("aucun terrain proche : rien à injecter"); return; }
        if (!_sim.TryGetAirportPosition(icao, out double apLat, out double apLon)) return;

        // Sans titre valide, on ne crée rien. Un titre inventé échoue en silence — c'est la
        // leçon la plus coûteuse de ce projet.
        // OBSERVÉ D'ABORD. L'injecteur ne tente qu'un seul titre par appareil : s'il échoue,
        // rien ne naît et le tour est perdu. Il lui faut donc du certain — ce que le simulateur
        // a montré ici. Le catalogue livré ne sert que si l'utilisateur n'a encore rien vu,
        // typiquement à son tout premier vol.
        var airliners = Flyable(_catalog.Observed(SimAircraftKind.Airliner));
        var light = Flyable(_catalog.Observed(SimAircraftKind.GeneralAviation));

        if (airliners.Count == 0 && light.Count == 0)
        {
            airliners = Flyable(_catalog.Of(SimAircraftKind.Airliner));
            light = Flyable(_catalog.Of(SimAircraftKind.GeneralAviation));
        }
        if (airliners.Count == 0 && light.Count == 0)
        { Log("aucun titre volant au catalogue : rien à injecter"); return; }

        // VOL À VUE OU AUX INSTRUMENTS. Le tirage n'a lieu que si les deux familles existent :
        // sur une installation sans appareil léger, tout reste aux instruments plutôt que de
        // faire voler un A320 en circuit d'aérodrome.
        bool vfr = light.Count > 0 && (airliners.Count == 0 || _rng.Next(100) < VfrPercent);

        var pool = vfr ? light : airliners;
        var flight = new FlightProfile(
            Title: pool[_rng.Next(pool.Count)],
            Vfr: vfr,
            CruiseAltFeet: vfr ? _rng.Next(2, 6) * 1000 : _rng.Next(9, 20) * 1000,
            MaxLegNm: vfr ? MaxVfrLegNm : MaxLegNm);

        // On alterne arrivée / départ / parking : c'est le mélange qui fait un terrain vivant,
        // pas le nombre. Une file d'arrivées sans personne au sol sonne aussi faux qu'un
        // parking plein où rien ne décolle.
        //
        // ET ON REPLIE si le type tiré n'est pas possible. Sur un terrain isolé — le Cap-Vert,
        // Madère — il n'y a parfois aucun aéroport à portée pour bâtir une arrivée. Sans repli,
        // un tour sur trois ne produisait rien et le terrain se peuplait trois fois moins vite.
        int start = _serial++ % 3;
        for (int i = 0; i < 3; i++)
        {
            bool done = ((start + i) % 3) switch
            {
                0 => InjectArrival(flight, icao!, apLat, apLon),
                1 => InjectDeparture(flight, icao!, apLat, apLon),
                // Le stationnement accepte TOUS les titres, maquettes comprises : c'est
                // exactement ce pour quoi elles existent.
                _ => InjectParked(ParkableTitle(), icao!),
            };
            if (done) return;
        }
    }

    /// <summary>
    /// Ce qui caractérise un vol injecté : l'appareil, ses règles de vol, son niveau et la
    /// distance qu'il peut couvrir. Regroupé parce que ces quatre valeurs doivent rester
    /// cohérentes — un Cessna aux instruments à trente mille pieds sur mille milles n'existe pas.
    /// </summary>
    private sealed record FlightProfile(string Title, bool Vfr, int CruiseAltFeet, double MaxLegNm)
    {
        public string Rules => Vfr ? "VFR" : "IFR";
    }

    /// <summary>
    /// Une ARRIVÉE : un vol venu d'un autre terrain, placé D'EMBLÉE presque au bout de son
    /// plan, donc en approche. Le simulateur l'amène jusqu'au toucher : c'est la plus visible
    /// des trois sources de vie — on la voit se poser.
    ///
    /// LE PLAN PART D'UN VRAI AÉROPORT, et c'est tout le correctif. La première version
    /// commençait sur un point libre placé à dix-huit milles : le simulateur a refusé chaque
    /// création, sans message, là où les départs — qui partent d'un aéroport — passaient. Un
    /// appareil « ATC en route » a besoin d'un terrain de départ pour entrer dans le système.
    /// On le fait donc partir pour de bon d'ailleurs, et on le pose près de l'arrivée en
    /// jouant sur sa POSITION dans le plan.
    /// </summary>
    private bool InjectArrival(FlightProfile flight, string icao, double apLat, double apLon)
    {
        var origin = PickAirport(icao, apLat, apLon, MinArrivalLegNm, flight.MaxLegNm);
        if (origin is null)
        {
            Log($"aucun terrain à moins de {flight.MaxLegNm:F0} nm de {icao} " +
                $"({flight.Rules}) : arrivée impossible ici");
            return false;
        }

        var (originIcao, originLat, originLon) = origin.Value;
        double legNm = Geo.DistanceMeters(originLat, originLon, apLat, apLon) / 1852.0;

        var plan = new List<PlanWaypoint>
        {
            new(originIcao, originLat, originLon, 0, originIcao),
            new(icao, apLat, apLon, 0, icao),
        };

        string path = FlightPlanWriter.Save(PlansDir, $"arr{_serial}", plan,
                                            flight.CruiseAltFeet, flight.Rules);
        string tail = NextTail();

        // Position dans le plan : 0 = au départ, 1 = à l'arrivée. On vise le point situé à
        // ArrivalSpawnNm du terrain, ce qui fait naître l'appareil en approche plutôt que de
        // le faire voler toute l'étape avant d'être visible. Un vol à vue se présente de plus
        // près : il vole moins vite, et l'attendre à dix-huit milles le rendrait invisible
        // pendant de longues minutes.
        double spawnNm = flight.Vfr ? 8 : ArrivalSpawnNm;
        double position = Math.Clamp(1.0 - (spawnNm / legNm), 0.05, 0.97);

        _sim.CreateEnrouteAircraft(flight.Title, tail, _rng.Next(100, 9999), path, position);
        Log($"arrivée {flight.Rules} « {flight.Title} » {tail} de {originIcao} vers {icao} " +
            $"({legNm:F0} nm, lâché à {position:F2} du plan)");
        return true;
    }

    /// <summary>
    /// Un DÉPART : il naît au terrain et s'en va vers un autre. Le simulateur le fait rouler
    /// puis décoller — il faut donc un vrai terrain de destination, pas un point en l'air.
    /// </summary>
    private bool InjectDeparture(FlightProfile flight, string icao, double apLat, double apLon)
    {
        // TOUR DE PISTE : un vol à vue sur deux ne part nulle part, il tourne. C'est l'image
        // même d'un aérodrome vivant, et le simulateur sait le faire seul — le paramètre
        // « touch and go » de l'API existe précisément pour ça. Un aller simple vers un terrain
        // lointain ne donne, lui, qu'un décollage et plus rien.
        bool circuit = flight.Vfr && _rng.Next(2) == 0;

        var destination = PickAirport(icao, apLat, apLon, MinLegNm, flight.MaxLegNm);
        if (destination is null && !circuit)
        {
            Log($"aucun terrain de destination à portée ({flight.Rules}) : départ abandonné");
            return false;
        }

        var plan = new List<PlanWaypoint> { new(icao, apLat, apLon, 0, icao) };

        if (circuit)
        {
            // Un point à l'écart puis retour au terrain : le simulateur en fait un circuit.
            var (wpLat, wpLon) = Project(apLat, apLon, 5, _rng.Next(360));
            plan.Add(new($"CIRC{_serial}", wpLat, wpLon, VfrCircuitAltitudeFeet));
            plan.Add(new(icao, apLat, apLon, 0, icao));
        }
        else
        {
            var (destIcao, destLat, destLon) = destination!.Value;
            plan.Add(new(destIcao, destLat, destLon, 0, destIcao));
        }

        string path = FlightPlanWriter.Save(PlansDir, $"dep{_serial}", plan,
                                            circuit ? VfrCircuitAltitudeFeet : flight.CruiseAltFeet,
                                            flight.Rules);
        string tail = NextTail();

        _sim.CreateEnrouteAircraft(flight.Title, tail, _rng.Next(100, 9999), path,
                                   planPosition: 0, touchAndGo: circuit);

        Log(circuit
            ? $"tour de piste VFR « {flight.Title} » {tail} à {icao}"
            : $"départ {flight.Rules} « {flight.Title} » {tail} de {icao} vers {plan[^1].Id}");
        return true;
    }

    private bool InjectParked(string title, string icao)
    {
        if (title.Length == 0) return false;

        string tail = NextTail();
        _sim.CreateParkedAircraft(title, tail, icao);
        Log($"appareil garé « {title} » {tail} à {icao}");
        return true;
    }

    /// <summary>
    /// Choisit un terrain AU HASARD dans une tranche de distance donnée.
    ///
    /// Deux raisons de ne pas prendre le plus éloigné, comme le faisait la première version :
    /// le cache d'aéroports du simulateur ne se limite pas au voisinage, ce qui envoyait des
    /// départs du Cap-Vert vers les îles Salomon ; et prendre toujours le même terrain donne
    /// vingt appareils qui partent tous au même endroit. Le tirage au sort dans une tranche
    /// raisonnable règle les deux.
    /// </summary>
    private (string Icao, double Lat, double Lon)? PickAirport(string exclude, double lat, double lon,
                                                               double minNm, double maxNm)
    {
        var candidates = new List<(string Icao, double Lat, double Lon)>();

        foreach (var (icao, aLat, aLon) in _sim.NearbyAirports())
        {
            if (string.Equals(icao, exclude, StringComparison.OrdinalIgnoreCase)) continue;

            double nm = Geo.DistanceMeters(lat, lon, aLat, aLon) / 1852.0;
            if (nm >= minNm && nm <= maxNm) candidates.Add((icao, aLat, aLon));
        }

        return candidates.Count == 0 ? null : candidates[_rng.Next(candidates.Count)];
    }

    /// <summary>Point situé à une distance et un relèvement donnés (formule du grand cercle).</summary>
    private static (double Lat, double Lon) Project(double lat, double lon, double nm, double bearingDeg)
    {
        double d = nm * 1852.0 / 6_371_000.0;
        double b = bearingDeg * Math.PI / 180.0;
        double φ1 = lat * Math.PI / 180.0, λ1 = lon * Math.PI / 180.0;

        double φ2 = Math.Asin(Math.Sin(φ1) * Math.Cos(d) + Math.Cos(φ1) * Math.Sin(d) * Math.Cos(b));
        double λ2 = λ1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(φ1),
                                    Math.Cos(d) - Math.Sin(φ1) * Math.Sin(φ2));

        return (φ2 * 180.0 / Math.PI, λ2 * 180.0 / Math.PI);
    }

    /// <summary>
    /// Ne garde que les appareils CAPABLES DE VOLER.
    ///
    /// Le simulateur livre des conteneurs « PassiveAircraft » : des maquettes destinées à
    /// garnir les parkings, sans mécanique du vol. Elles remplissent parfaitement un poste de
    /// stationnement — c'est leur raison d'être — mais leur confier un plan de vol ne peut
    /// rien donner de bon. La distinction ne se lit que dans le titre, faute de mieux.
    /// </summary>
    /// <remarks>
    /// L'APPAREIL DU JOUEUR EST ÉCARTÉ ICI, et c'est tout aussi important : voir
    /// <see cref="OnPlayerAircraft"/>. Un sosie de son propre avion ne peuple pas un terrain,
    /// il inquiète.
    /// </remarks>
    private List<string> Flyable(IReadOnlyList<string> titles) =>
        titles.Where(t => !t.Contains("passive", StringComparison.OrdinalIgnoreCase)
                       && !IsPlayerTitle(t)).ToList();

    /// <summary>
    /// N'importe quel titre pour garnir un parking — maquettes comprises, c'est leur emploi.
    /// Comme ailleurs, l'observé passe avant le livré : une seule tentative par appareil.
    /// </summary>
    private string ParkableTitle()
    {
        var observed = _catalog.Observed(SimAircraftKind.Airliner)
            .Concat(_catalog.Observed(SimAircraftKind.GeneralAviation))
            .Concat(_catalog.Observed(SimAircraftKind.Fighter))
            .ToList();

        var all = observed.Count > 0
            ? observed
            : _catalog.Of(SimAircraftKind.Airliner)
                .Concat(_catalog.Of(SimAircraftKind.GeneralAviation))
                .Concat(_catalog.Of(SimAircraftKind.Fighter))
                .ToList();

        // Même règle qu'en vol : on ne gare pas un sosie de l'appareil du joueur à côté de lui.
        all = all.Where(t => !IsPlayerTitle(t)).ToList();

        return all.Count == 0 ? "" : all[_rng.Next(all.Count)];
    }

    /// <summary>
    /// Immatriculation fictive, DIFFÉRENTE de celle du joueur.
    ///
    /// Le préfixe « WT » n'appartient à aucune nomenclature réelle, ce qui rend déjà la
    /// collision improbable — mais pas impossible si le pilote s'est attribué la même. Un
    /// homonyme sur la fréquence est le pire cas : l'équipage de synthèse répond aux appels
    /// destinés au joueur, et le contrôleur tient l'échange pour clos.
    ///
    /// On préfère prévenir ici plutôt que rattraper au moment de parler : un appareil déjà
    /// né avec le mauvais nom restera muet toute sa vie, ce qui se remarque.
    /// </summary>
    private string NextTail()
    {
        string mine = new string((_playerCallsign?.Invoke() ?? "")
            .Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        for (int i = 0; i < 8; i++)
        {
            string tail = $"WT{_rng.Next(100, 999)}";
            if (mine.Length == 0 || tail.ToLowerInvariant() != mine) return tail;
        }
        return $"WT{_rng.Next(100, 999)}";
    }

    /// <summary>Indicatif du joueur, pour ne jamais le réutiliser. Null = garde inopérante.</summary>
    public Func<string?>? PlayerCallsign { get => _playerCallsign; set => _playerCallsign = value; }

    private Func<string?>? _playerCallsign;

    private static void Log(string message) => FileLog.Write($"[injection] {message}");

    /// <summary>Retire tout ce qu'on a créé. Appelé à la fermeture.</summary>
    public void RemoveAll()
    {
        List<uint> copy;
        lock (_gate) { copy = new(_created); _created.Clear(); }

        foreach (uint id in copy) _sim.RemoveAircraft(id);
        if (copy.Count > 0) Log($"{copy.Count} appareil(s) retiré(s)");
    }

    public void Dispose()
    {
        _timer.Dispose();
        RemoveAll();
        _sim.ContextReceived -= OnContext;
        _sim.StateChanged -= OnState;
        _sim.AircraftReceived -= OnPlayerAircraft;
        _sim.TrafficAircraftCreated -= OnCreated;
    }
}
