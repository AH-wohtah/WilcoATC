namespace WilcoATC.Atc.Localization;

/// <summary>
/// Langue de contrôle d'un terrain, déduite de son <b>indicatif d'emplacement OACI</b>.
///
/// POURQUOI LE PRÉFIXE OACI ET PAS LE PAYS DU CSV : les indicatifs OACI sont géographiques
/// par construction (LF = France, ED = Allemagne, SK = Colombie…). Le préfixe est donc
/// disponible partout où l'on connaît le terrain — y compris quand les données OurAirports
/// ne sont pas chargées, ou quand la fréquence vient du simulateur et non du CSV. Aucune
/// donnée supplémentaire à charger, aucun plombage à faire traverser au résolveur.
///
/// COUVERTURE MONDIALE : la table ne liste QUE les pays dont la langue de contrôle est une
/// de celles que l'application sait parler. Tout le reste — et c'est la majorité du globe —
/// retombe sur l'anglais, ce qui est aussi le standard OACI. Ajouter une langue, c'est
/// ajouter ses préfixes ici et ses phrases dans <see cref="AtcPhrases"/>.
///
/// AMBIGUÏTÉS ASSUMÉES : plusieurs pays sont plurilingues. On retient la langue réellement
/// employée à la radio pour le trafic intérieur — Suisse (LS) en allemand, Belgique (EB) en
/// français, Canada (C) en anglais malgré le Québec. Un pilote qui préfère autre chose garde
/// la main : les réglages permettent d'imposer l'anglais partout, ou une langue fixe.
/// </summary>
public static class AtcCountryLanguages
{
    // Préfixes de 2 lettres. Ordre sans importance : la recherche essaie 2 lettres puis 1.
    private static readonly Dictionary<string, AtcLanguage> ByPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------------------------------------------------------------- français
        ["LF"] = AtcLanguage.French,   // France métropolitaine + outre-mer (LFV, LFO…)
        ["LN"] = AtcLanguage.French,   // Monaco
        ["EL"] = AtcLanguage.French,   // Luxembourg
        ["EB"] = AtcLanguage.French,   // Belgique
        ["TF"] = AtcLanguage.French,   // Antilles françaises (Guadeloupe, Martinique…)
        ["SO"] = AtcLanguage.French,   // Guyane française
        ["MT"] = AtcLanguage.French,   // Haïti
        ["NW"] = AtcLanguage.French,   // Nouvelle-Calédonie
        ["NT"] = AtcLanguage.French,   // Polynésie française
        ["FM"] = AtcLanguage.French,   // Madagascar, Comores, Mayotte, La Réunion
        // Afrique francophone
        ["DA"] = AtcLanguage.French,   // Algérie
        ["DB"] = AtcLanguage.French,   // Bénin
        ["DF"] = AtcLanguage.French,   // Burkina Faso
        ["DI"] = AtcLanguage.French,   // Côte d'Ivoire
        ["DR"] = AtcLanguage.French,   // Niger
        ["DT"] = AtcLanguage.French,   // Tunisie
        ["DX"] = AtcLanguage.French,   // Togo
        ["FC"] = AtcLanguage.French,   // Congo
        ["FE"] = AtcLanguage.French,   // République centrafricaine
        ["FK"] = AtcLanguage.French,   // Cameroun
        ["FO"] = AtcLanguage.French,   // Gabon
        ["FT"] = AtcLanguage.French,   // Tchad
        ["FZ"] = AtcLanguage.French,   // République démocratique du Congo
        ["GA"] = AtcLanguage.French,   // Mali
        ["GF"] = AtcLanguage.English,  // Sierra Leone (anglophone au milieu de la zone)
        ["GM"] = AtcLanguage.French,   // Maroc
        ["GO"] = AtcLanguage.French,   // Sénégal
        ["GQ"] = AtcLanguage.French,   // Mauritanie
        ["GU"] = AtcLanguage.French,   // Guinée
        ["HB"] = AtcLanguage.French,   // Burundi
        ["HR"] = AtcLanguage.French,   // Rwanda
        ["HD"] = AtcLanguage.French,   // Djibouti

        // ---------------------------------------------------------------- allemand
        ["ED"] = AtcLanguage.German,   // Allemagne (civil)
        ["ET"] = AtcLanguage.German,   // Allemagne (militaire)
        ["LO"] = AtcLanguage.German,   // Autriche
        ["LS"] = AtcLanguage.German,   // Suisse

        // ---------------------------------------------------------------- espagnol
        ["LE"] = AtcLanguage.Spanish,  // Espagne
        ["GC"] = AtcLanguage.Spanish,  // Canaries
        ["GE"] = AtcLanguage.Spanish,  // Ceuta / Melilla
        ["FG"] = AtcLanguage.Spanish,  // Guinée équatoriale
        ["MM"] = AtcLanguage.Spanish,  // Mexique
        ["MD"] = AtcLanguage.Spanish,  // République dominicaine
        ["MG"] = AtcLanguage.Spanish,  // Guatemala
        ["MH"] = AtcLanguage.Spanish,  // Honduras
        ["MN"] = AtcLanguage.Spanish,  // Nicaragua
        ["MP"] = AtcLanguage.Spanish,  // Panama
        ["MR"] = AtcLanguage.Spanish,  // Costa Rica
        ["MS"] = AtcLanguage.Spanish,  // Salvador
        ["MU"] = AtcLanguage.Spanish,  // Cuba
        ["SA"] = AtcLanguage.Spanish,  // Argentine
        ["SC"] = AtcLanguage.Spanish,  // Chili
        ["SE"] = AtcLanguage.Spanish,  // Équateur
        ["SG"] = AtcLanguage.Spanish,  // Paraguay
        ["SK"] = AtcLanguage.Spanish,  // Colombie
        ["SL"] = AtcLanguage.Spanish,  // Bolivie
        ["SP"] = AtcLanguage.Spanish,  // Pérou
        ["SU"] = AtcLanguage.Spanish,  // Uruguay
        ["SV"] = AtcLanguage.Spanish,  // Venezuela

        // ---------------------------------------------------------------- italien
        ["LI"] = AtcLanguage.Italian,  // Italie
    };

    /// <summary>
    /// Langue de contrôle du terrain, anglais par défaut. Accepte un ICAO complet
    /// (« LFPG ») comme un simple préfixe (« LF »).
    /// </summary>
    public static AtcLanguage ForIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return AtcLanguage.English;

        string s = icao!.Trim();
        if (s.Length < 2) return AtcLanguage.English;

        return ByPrefix.TryGetValue(s[..2], out var lang) ? lang : AtcLanguage.English;
    }

    /// <summary>Vrai si le terrain est contrôlé dans une autre langue que l'anglais.</summary>
    public static bool HasLocalLanguage(string? icao) => ForIcao(icao) != AtcLanguage.English;
}
