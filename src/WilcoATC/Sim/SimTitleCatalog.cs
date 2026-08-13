using System.IO;
using System.Text.Json;
using WilcoATC.Diagnostics;

namespace WilcoATC.Sim;

/// <summary>
/// Catégorie d'appareil, déduite du titre. Grossière à dessein : elle ne sert qu'à choisir
/// un appareil plausible pour un rôle donné (une escorte militaire, un vol de ligne), pas à
/// modéliser une flotte.
/// </summary>
public enum SimAircraftKind
{
    Unknown,
    Fighter,     // chasseur — l'escorte d'interception
    Airliner,    // avion de ligne — le trafic commercial
    GeneralAviation,
}

/// <summary>
/// CATALOGUE DES TITRES DE CONTENEUR RÉELLEMENT DISPONIBLES sur cette installation.
///
/// LE PROBLÈME QU'IL RÉSOUT. Faire naître un appareil dans le simulateur exige son titre de
/// conteneur au caractère près. Un titre inexact ne produit ni erreur explicite ni appareil :
/// la création échoue, et rien dans le ciel ne le signale. Or ces titres sont introuvables
/// sur le disque — Microsoft Flight Simulator 2024 empaquette les aircraft.cfg dans des
/// archives compressées — et les deviner ne fonctionne pas : les quatre orthographes
/// plausibles du F/A-18 ont toutes été refusées.
///
/// LA SOURCE FIABLE. Le simulateur connaît le titre de chaque appareil qu'il a lui-même fait
/// naître, et il accepte de le dire. Deux robinets alimentent donc ce catalogue :
///   • l'appareil PILOTÉ par le joueur, connu dès la connexion ;
///   • les appareils du TRAFIC autour de lui, relevés par <see cref="ISimConnectService.RequestNearbyAircraft"/>.
/// Un titre qui vient de là est valide par construction : il est en vol au moment où on le lit.
///
/// PERSISTANT. Le catalogue est écrit sur disque, donc ce qui a été vu une fois reste
/// utilisable ensuite — y compris quand le joueur a changé d'avion et que la source a disparu.
/// </summary>
public sealed class SimTitleCatalog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WilcoATC", "sim-titles.json");

    /// <summary>Titre -> catégorie. Comparaison insensible à la casse : le simulateur n'est pas constant.</summary>
    private readonly Dictionary<string, SimAircraftKind> _titles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CATALOGUE LIVRÉ AVEC L'APPLICATION (data/sim-titles-seed.csv).
    ///
    /// Sans lui, chaque utilisateur repart de zéro : tant que son simulateur ne lui a montré
    /// aucun appareil, rien ne peut naître — et pour l'escorte, il devait charger un chasseur
    /// lui-même avant que l'interception fonctionne une seule fois. Un titre relevé sur UNE
    /// installation est valide sur toutes celles qui possèdent le même appareil ; le partager
    /// évite à chacun de refaire la découverte.
    ///
    /// Il reste SÉPARÉ des titres observés, et non fondu dedans : un titre livré n'est qu'un
    /// PARI — l'utilisateur peut très bien ne pas posséder cet appareil. Ce qu'il a vu de ses
    /// yeux, lui, est certain. La distinction permet de préférer le certain au probable, et
    /// elle interdit qu'un pari se retrouve écrit dans son fichier comme s'il avait été observé.
    /// </summary>
    private readonly Dictionary<string, SimAircraftKind> _seed = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    public SimTitleCatalog()
    {
        LoadSeed();
        Load();
    }

    /// <summary>Nombre de titres connus, toutes catégories confondues.</summary>
    public int Count { get { lock (_gate) return _titles.Count; } }

    /// <summary>
    /// Enregistre un titre observé. Renvoie vrai s'il était inconnu — ce qui permet à
    /// l'appelant de ne journaliser que les nouveautés plutôt que chaque relevé.
    /// </summary>
    public bool Observe(string? title)
    {
        string t = title?.Trim() ?? "";
        if (t.Length == 0) return false;

        lock (_gate)
        {
            if (_titles.ContainsKey(t)) return false;
            _titles[t] = Classify(t);
        }

        FileLog.Write($"[titres] nouvel appareil connu : « {t} » ({Classify(t)})");
        Save();
        return true;
    }

    /// <summary>
    /// Titres RÉELLEMENT VUS dans ce simulateur, pour cette catégorie. Ce sont les seuls dont
    /// la création est garantie : l'appareil existait au moment où on a lu son titre.
    /// </summary>
    public IReadOnlyList<string> Observed(SimAircraftKind kind)
    {
        lock (_gate)
            return _titles.Where(kv => kv.Value == kind).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// Tous les titres d'une catégorie : ceux OBSERVÉS d'abord, puis ceux LIVRÉS avec
    /// l'application. L'ordre est la garantie : on tente le certain avant le probable, et un
    /// titre livré absent de cette installation échoue simplement, laissant la place au suivant.
    ///
    /// Destiné à ce qui essaie plusieurs candidats — l'escorte. Ce qui n'en tente qu'un seul
    /// doit préférer <see cref="Observed"/>.
    /// </summary>
    public IReadOnlyList<string> Of(SimAircraftKind kind)
    {
        var observed = Observed(kind);
        var seen = new HashSet<string>(observed, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
            return observed
                .Concat(_seed.Where(kv => kv.Value == kind && !seen.Contains(kv.Key)).Select(kv => kv.Key))
                .ToList();
    }

    /// <summary>Premier titre connu d'une catégorie, ou null.</summary>
    public string? FirstOf(SimAircraftKind kind) => Of(kind).FirstOrDefault();

    // ------------------------------------------------------------------ classement

    private static readonly string[] FighterHints =
    {
        "hornet", "fa-18", "fa18", "fa 18", "f/a-18", "f-16", "f-15", "f-22", "f-35",
        "rafale", "eurofighter", "typhoon", "gripen", "mirage", "viper", "tomcat",
        "harrier", "a-10", "mig-", "su-27", "su-35", "pilatus pc-21",
    };

    /// <summary>
    /// Indices d'avion de ligne. Deux conventions coexistent, et il faut les deux :
    ///
    ///   • le NOM COMMERCIAL — « Airbus A320neo Asobo » — celui des appareils livrés avec le
    ///     simulateur, donc celui que porte l'avion du joueur ;
    ///   • le CODE OACI — « FSLTL_FAIB_B738_TVF », « FSLTL_B77W_KAC » — celui qu'emploient les
    ///     paquets de trafic, où l'appareil n'est jamais désigné par son nom commercial.
    ///
    /// N'avoir que le premier fait classer un 777-300ER en aviation générale : c'est ce qui
    /// s'est produit sur les 53 appareils relevés à Roissy, et c'est la convention du trafic
    /// — donc de tout ce qui nous intéresse ici — qui manquait.
    /// </summary>
    private static readonly string[] AirlinerHints =
    {
        // Noms commerciaux
        "airbus", "boeing", "embraer", "atr ", "atr-", "dash 8", "q400",
        "a300", "a310", "a318", "a319", "a320", "a321", "a330", "a340", "a350", "a380",
        "737", "747", "757", "767", "777", "787", "md-11", "md-80",

        // Codes OACI — Airbus (a19n/a20n/a21n = famille neo, bcs1/bcs3 = A220)
        "a19n", "a20n", "a21n", "a306", "a30b", "a332", "a333", "a338", "a339",
        "a342", "a343", "a345", "a346", "a359", "a35k", "a388", "bcs1", "bcs3",

        // Codes OACI — Boeing (b77w = 777-300ER, b77l = 777F, b78x = 787-10)
        "b712", "b731", "b732", "b733", "b734", "b735", "b736", "b737", "b738", "b739",
        "b38m", "b39m", "b3xm", "b744", "b748", "b752", "b753", "b762", "b763", "b764",
        "b772", "b773", "b77w", "b77l", "b788", "b789", "b78x",

        // Codes OACI — régionaux et turbopropulseurs de ligne
        "e170", "e75l", "e75s", "e190", "e195", "e290", "e295", "e135", "e145",
        "crj2", "crj7", "crj9", "crjx", "dh8a", "dh8b", "dh8c", "dh8d",
        "at43", "at45", "at72", "at75", "at76", "sb20", "f100", "rj85", "rj1h",
        "md11", "md82", "md83", "md88", "md90",
    };

    /// <summary>
    /// Range un titre dans une catégorie d'après les mots qu'il contient. Volontairement
    /// simple : un mauvais classement fait choisir un appareil peu adapté, jamais échouer
    /// une création — le titre, lui, reste exact.
    /// </summary>
    public static SimAircraftKind Classify(string title)
    {
        string t = title.ToLowerInvariant();
        if (FighterHints.Any(t.Contains)) return SimAircraftKind.Fighter;
        if (AirlinerHints.Any(t.Contains)) return SimAircraftKind.Airliner;
        return SimAircraftKind.GeneralAviation;
    }

    // ------------------------------------------------------------------ persistance

    /// <summary>
    /// Charge le catalogue livré. Format volontairement trivial — <c>titre</c> par ligne,
    /// la catégorie étant recalculée : c'est un fichier destiné à être régénéré et complété
    /// à la main, pas une base de données.
    /// </summary>
    private void LoadSeed()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "data", "sim-titles-seed.csv");
            if (!File.Exists(path)) return;

            foreach (string raw in File.ReadAllLines(path))
            {
                string title = raw.Trim();
                if (title.Length == 0 || title.StartsWith('#')) continue;
                _seed[title] = Classify(title);
            }

            FileLog.Write($"[titres] catalogue livré : {_seed.Count} titre(s) de départ");
        }
        catch (Exception ex) { FileLog.Write($"[titres] catalogue livré illisible : {ex.Message}"); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, SimAircraftKind>>(File.ReadAllText(FilePath));
            if (saved is null) return;

            // La catégorie enregistrée est DÉLIBÉRÉMENT ignorée et recalculée. Ce qui compte
            // et ne périme jamais, c'est le titre — le classement, lui, n'est qu'une lecture
            // que l'on sait imparfaite. Sans ce recalcul, un catalogue rempli par une version
            // antérieure garderait ses erreurs à vie : c'est ce qui est arrivé quand les codes
            // OACI du trafic (B77W, A20N, BCS3…) rangeaient des avions de ligne en aviation
            // générale. Un classement amélioré répare ainsi les catalogues existants.
            lock (_gate)
                foreach (var title in saved.Keys) _titles[title] = Classify(title);

            FileLog.Write($"[titres] catalogue chargé : {_titles.Count} appareil(s) connu(s)");
            Save();   // on réécrit avec le classement à jour, pour que le fichier reste lisible
        }
        // Un catalogue illisible n'est pas une raison d'empêcher l'application de démarrer :
        // il se reconstruira tout seul au prochain vol.
        catch (Exception ex) { FileLog.Write($"[titres] lecture impossible : {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            Dictionary<string, SimAircraftKind> copy;
            lock (_gate) copy = new(_titles);

            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { FileLog.Write($"[titres] écriture impossible : {ex.Message}"); }
    }
}
