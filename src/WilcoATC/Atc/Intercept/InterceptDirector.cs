using WilcoATC.Diagnostics;
using WilcoATC.Settings;
using WilcoATC.Sim;

namespace WilcoATC.Atc.Intercept;

/// <summary>
/// Fait apparaître un CHASSEUR qui vient escorter l'avion du joueur, le tient en formation,
/// puis le retire. Déclenché par la panne radio présumée (trois appels sans réponse).
///
/// POURQUOI ON PILOTE PLUTÔT QUE DE LAISSER VOLER : un objet IA du simulateur suit un plan
/// de vol, il ne sait pas coller à un avion qui manœuvre. On recalcule donc sa place à côté
/// du joueur et on l'y pose — mais À CHAQUE IMAGE, et en recopiant son assiette. C'est la
/// cadence qui fait tout : à 1 Hz on voit des bonds de cent trente mètres, au rythme de
/// l'image on voit un avion qui vole en formation.
///
/// SÛRETÉ : l'appareil du joueur n'est JAMAIS touché — on n'écrit que sur l'objet créé. Et
/// il est retiré dans tous les cas de sortie : fin de l'escorte, reprise du contact radio,
/// déconnexion du simulateur, fermeture de l'application.
/// </summary>
public sealed class InterceptDirector : IDisposable
{
    /// <summary>
    /// Titre du chasseur de Microsoft Flight Simulator 2024, RELEVÉ dans le simulateur.
    ///
    /// Il vaut d'être regardé, parce qu'il résume tout le problème : les quatre orthographes
    /// que le bon sens suggérait — « Boeing FA-18E Super Hornet », « FA-18E Super Hornet
    /// Asobo » et leurs variantes — ont toutes été refusées. Le vrai titre n'a ni tiret, ni
    /// espace dans « SuperHornet », ni nom de constructeur. Aucune supposition ne pouvait
    /// tomber juste, et une création ratée ne dit pas pourquoi elle a échoué.
    ///
    /// D'où la règle du projet : on ne devine JAMAIS un titre de conteneur. Celui-ci vient du
    /// simulateur, comme tous ceux du <see cref="SimTitleCatalog"/>, qui reste la source à
    /// consulter en premier — cette liste n'est qu'un dernier recours si le catalogue et le
    /// fichier livré ont tous deux échoué.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTitles = new[]
    {
        "FA18E SuperHornet",
    };

    /// <summary>Décalage latéral de l'escorte (mètres). Un chasseur se met à VOTRE gauche.</summary>
    private const double OffsetLeftMeters = 70;

    /// <summary>Il se tient légèrement plus bas : c'est ce qui rend la formation lisible.</summary>
    private const double OffsetBelowFeet = 40;

    /// <summary>Léger retrait, pour ne pas apparaître pile dans l'axe du cockpit.</summary>
    private const double OffsetBehindMeters = 25;

    private readonly ISimConnectService _sim;
    private readonly SettingsService _settings;
    private readonly SimTitleCatalog? _catalog;

    private uint? _objectId;
    private int _titleIndex;
    private DateTime _spawnedAt;
    private bool _active;

    public InterceptDirector(ISimConnectService sim, SettingsService settings,
                             SimTitleCatalog? catalog = null)
    {
        _sim = sim;
        _settings = settings;
        _catalog = catalog;
        _sim.InterceptorCreated += OnCreated;
        _sim.ContextReceived += OnContext;
        _sim.StateChanged += OnState;
        _sim.AircraftReceived += OnAircraft;
        _sim.FormationTick += OnFormationTick;
    }

    /// <summary>
    /// Mots qui trahissent un appareil de combat dans un titre de conteneur. Volontairement
    /// larges : ils ne servent qu'à repérer un candidat, jamais à en fabriquer un.
    /// </summary>
    private static readonly string[] FighterHints =
    {
        "hornet", "fa-18", "fa18", "f/a-18", "f-16", "f-15", "f-22", "f-35",
        "rafale", "eurofighter", "typhoon", "gripen", "mirage", "viper", "tomcat",
    };

    /// <summary>
    /// APPREND le titre du chasseur en observant l'appareil du JOUEUR.
    ///
    /// Le titre de conteneur exact ne peut pas être deviné : dans Microsoft Flight Simulator
    /// 2024, les aircraft.cfg sont empaquetés dans des archives compressées, et un titre
    /// inexact fait échouer la création (CREATE_OBJECT_FAILED) sans autre indice. En revanche
    /// le simulateur nous DONNE le titre de l'avion piloté. Il suffit donc de charger le
    /// chasseur une fois pour que l'application le retienne — et l'interception fonctionne
    /// ensuite avec n'importe quel appareil.
    ///
    /// On n'écrase jamais un titre saisi à la main.
    /// </summary>
    private void OnAircraft(AircraftSnapshot a)
    {
        string title = a.Title?.Trim() ?? "";
        if (title.Length == 0) return;

        // Le titre courant est journalisé à chaque changement d'appareil : c'est la seule
        // façon, pour l'utilisateur comme pour le support, de connaître la chaîne attendue.
        if (!string.Equals(title, _lastSeenTitle, StringComparison.OrdinalIgnoreCase))
        {
            _lastSeenTitle = title;
            FileLog.Write($"[intercepteur] appareil piloté : « {title} »");
        }

        if (_settings.Current.InterceptorTitle is { Length: > 0 }) return;

        string lower = title.ToLowerInvariant();
        if (!FighterHints.Any(h => lower.Contains(h))) return;

        _settings.Current.InterceptorTitle = title;
        _settings.Save();
        FileLog.Write($"[intercepteur] titre retenu pour l'escorte : « {title} »");
    }

    private string? _lastSeenTitle;

    /// <summary>Une escorte est-elle en cours (demandée, pas forcément arrivée) ?</summary>
    public bool IsActive => _active;

    /// <summary>
    /// Un appareil existe-t-il RÉELLEMENT dans le simulateur ?
    ///
    /// C'est la seule chose qui autorise l'ATC à dire « vous êtes intercepté ». Demander la
    /// création ne suffit pas : l'option peut être coupée, le titre inconnu, le simulateur
    /// absent — et annoncer un intercepteur que le pilote ne voit pas dans son ciel est pire
    /// que de ne rien annoncer du tout.
    /// </summary>
    public bool HasAircraft => _active && _objectId is not null;

    /// <summary>
    /// Déclenche l'interception. Sans connexion, sans position, ou si l'option est coupée,
    /// il ne se passe rien — silencieusement.
    /// </summary>
    public void Launch(ContextSnapshot? context)
    {
        if (!_settings.Current.InterceptorEnabled || _active) return;
        if (context is not { } c || c.OnGround || !c.InFlightSession) return;

        _candidates = BuildCandidates();
        if (_candidates.Count == 0)
        {
            // Le simulateur ne nous a encore montré aucun appareil : on ne tente rien plutôt
            // que d'inventer un titre. L'ATC, qui vérifie HasAircraft, n'annoncera rien.
            FileLog.Write("[intercepteur] aucun titre connu : escorte abandonnée");
            return;
        }

        FileLog.Write($"[intercepteur] {_candidates.Count} titre(s) à essayer, " +
                      $"à commencer par « {_candidates[0]} »");

        _active = true;
        _titleIndex = 0;
        _spawnedAt = DateTime.UtcNow;

        // Flux par image : c'est lui qui fait voler l'escorte au lieu de la faire sauter.
        _sim.StartFormationUpdates();
        Spawn(c);
    }

    /// <summary>Fin de l'escorte : l'appareil disparaît. Appelable à tout moment.</summary>
    public void Recall()
    {
        if (_active) _sim.StopFormationUpdates();   // on ne laisse pas tourner un flux par image
        _active = false;
        if (_objectId is { } id)
        {
            _sim.RemoveInterceptor(id);
            FileLog.Write("intercepteur retiré");
        }
        _objectId = null;
    }

    /// <summary>
    /// Titres à essayer, du plus souhaitable au dernier recours :
    ///   1. celui saisi dans les réglages, s'il y en a un — l'utilisateur a le dernier mot ;
    ///   2. les CHASSEURS vus dans le simulateur : valides à coup sûr, et adaptés au rôle ;
    ///   3. les titres devinés, qui n'ont jamais fonctionné mais ne coûtent rien à tenter ;
    ///   4. n'importe quel appareil connu — l'appareil du joueur, au minimum.
    ///
    /// Le point 4 mérite qu'on s'y arrête : escorté par un avion de ligne, c'est étrange.
    /// Mais aucun chasseur n'est installé partout, et un appareil visible dans le ciel vaut
    /// mieux qu'une interception dont on parle sans que rien n'apparaisse — c'était
    /// précisément le défaut à corriger. Le choix est journalisé pour rester explicable.
    /// </summary>
    private List<string> BuildCandidates()
    {
        var list = new List<string>();

        if (_settings.Current.InterceptorTitle is { Length: > 0 } custom) list.Add(custom);
        if (_catalog is not null) list.AddRange(_catalog.Of(SimAircraftKind.Fighter));
        list.AddRange(DefaultTitles);
        if (_catalog is not null) list.AddRange(_catalog.Of(SimAircraftKind.GeneralAviation));
        if (_catalog is not null) list.AddRange(_catalog.Of(SimAircraftKind.Airliner));

        // JAMAIS L'APPAREIL DU JOUEUR. Le repli du point 4 accepte n'importe quel modèle connu,
        // et le premier titre que le simulateur nous montre est justement celui qu'on pilote :
        // l'escorte prenait donc volontiers la forme d'une COPIE de votre propre avion, tenue à
        // soixante-dix mètres et recopiant votre assiette image par image. Escorté par un avion
        // de ligne, c'est étrange ; escorté par soi-même, c'est un fantôme — et c'est ainsi que
        // les utilisateurs le décrivent. L'utilisateur garde le dernier mot : un titre saisi à
        // la main dans les réglages n'est pas filtré.
        list.RemoveAll(t => _lastSeenTitle is { Length: > 0 } mine
                            && !string.Equals(t, _settings.Current.InterceptorTitle, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t, mine, StringComparison.OrdinalIgnoreCase));

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> _candidates = new();

    private void Spawn(ContextSnapshot c)
    {
        if (_titleIndex >= _candidates.Count) { _active = false; return; }
        string title = _candidates[_titleIndex];

        var (lat, lon) = Offset(c.Latitude, c.Longitude, c.HeadingTrueDeg);

        _sim.CreateInterceptor(
            title, "AF01",
            lat, lon,
            Math.Max(500, c.AltitudeMslFeet - OffsetBelowFeet),
            c.HeadingTrueDeg,
            Math.Max(120, c.GroundSpeedKnots));
    }

    private void OnCreated(uint objectId)
    {
        _objectId = objectId;
        FileLog.Write($"intercepteur en place (objet {objectId})");
    }

    private void OnContext(ContextSnapshot c)
    {
        if (!_active) return;

        // L'escorte ne dure pas éternellement : au-delà, le chasseur s'en va.
        if ((DateTime.UtcNow - _spawnedAt).TotalSeconds > Math.Max(10, _settings.Current.InterceptorSeconds))
        {
            Recall();
            return;
        }

        // Le simulateur n'a pas créé l'appareil (titre refusé) : on essaie le suivant, une
        // seule fois chacun, puis on renonce en le disant dans le journal — c'est le seul
        // endroit où l'on peut constater qu'aucun titre de cette installation ne convient.
        if (_objectId is null)
        {
            if (++_titleIndex >= _candidates.Count)
            {
                FileLog.Write($"[intercepteur] les {_candidates.Count} titre(s) essayé(s) ont " +
                              "tous été refusés par le simulateur : aucune escorte");
                _active = false;
                return;
            }
            FileLog.Write($"[intercepteur] titre refusé, essai suivant : « {_candidates[_titleIndex]} »");
            Spawn(c);
            return;
        }

        if (c.OnGround) Recall();   // le joueur s'est posé : l'escorte n'a plus lieu d'être

        // Le PLACEMENT ne se fait plus ici : ce flux est à 1 Hz, ce qui produirait un saut de
        // plus de cent mètres à chaque mise à jour. Il vit dans OnFormationTick, alimenté à
        // chaque image du simulateur.
    }

    /// <summary>
    /// Place l'escorte, une fois par image. C'est LA différence entre un avion qui vole à
    /// côté de vous et un avion qui se téléporte : à 250 nœuds, une seconde d'écart
    /// représente cent trente mètres, et l'œil ne voit alors que des bonds.
    ///
    /// L'assiette est RECOPIÉE du joueur : en virage, l'escorte s'incline avec vous. Sans
    /// cela elle reste désespérément à plat et trahit l'objet piloté de l'extérieur.
    /// </summary>
    private void OnFormationTick(FormationSnapshot f)
    {
        if (!_active || _objectId is not { } id) return;

        var (lat, lon) = Offset(f.Latitude, f.Longitude, f.HeadingTrueDeg);

        _sim.MoveInterceptor(
            id, lat, lon,
            Math.Max(500, f.AltitudeFeet - OffsetBelowFeet),
            pitchDeg: f.PitchDeg,
            bankDeg: f.BankDeg,
            headingTrueDeg: f.HeadingTrueDeg,
            airspeedKnots: Math.Max(120, f.AirspeedTrueKnots),
            // MÊME VECTEUR VITESSE que le joueur : l'escorte vole en parallèle. C'est lui qui
            // la fait AVANCER entre deux corrections, avec ses animations et son inertie —
            // sans vitesse, le simulateur n'affiche qu'un modèle déplacé de force.
            velocityEastFps: f.VelocityEastFps,
            velocityUpFps: f.VelocityUpFps,
            velocityNorthFps: f.VelocityNorthFps);
    }

    private void OnState(ConnectionState state, string? _)
    {
        if (state != ConnectionState.Connected) { _active = false; _objectId = null; }
    }

    /// <summary>
    /// Place l'escorte à gauche et légèrement en arrière du joueur. Le décalage est calculé
    /// dans le repère de l'AVION (donc il tourne avec lui), puis converti en degrés — un
    /// degré de longitude rétrécit avec le cosinus de la latitude, sans quoi la formation
    /// dériverait de plus en plus en s'éloignant de l'équateur.
    /// </summary>
    private static (double Lat, double Lon) Offset(double lat, double lon, double headingDeg)
    {
        double hdg = headingDeg * Math.PI / 180.0;

        // Axe avion : x vers l'avant, y vers la droite. On veut à gauche et en arrière.
        double forward = -OffsetBehindMeters;
        double right = -OffsetLeftMeters;

        double north = forward * Math.Cos(hdg) - right * Math.Sin(hdg);
        double east = forward * Math.Sin(hdg) + right * Math.Cos(hdg);

        const double MetersPerDegreeLat = 111_320.0;
        double metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(lat * Math.PI / 180.0);
        if (Math.Abs(metersPerDegreeLon) < 1) metersPerDegreeLon = 1; // près des pôles

        return (lat + north / MetersPerDegreeLat, lon + east / metersPerDegreeLon);
    }

    public void Dispose()
    {
        // Ne jamais laisser un appareil derrière soi : il resterait dans la session du joueur
        // jusqu'au rechargement du vol.
        Recall();
        _sim.InterceptorCreated -= OnCreated;
        _sim.ContextReceived -= OnContext;
        _sim.StateChanged -= OnState;
        _sim.AircraftReceived -= OnAircraft;
        _sim.FormationTick -= OnFormationTick;
    }
}
