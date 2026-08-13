using System.Globalization;
using WilcoATC.Common;

namespace WilcoATC.Stations;

/// <summary>
/// Classification des fréquences ATC — logique PARTAGÉE entre la résolution OurAirports
/// (texte libre du CSV) et la résolution live SimConnect (type numérique MSFS + nom). Source
/// unique de vérité pour : type de contrôleur, libellé court, ordre d'affichage, bande VHF.
/// </summary>
internal static class ControllerTaxonomy
{
    /// <summary>
    /// Bande VHF aviation civile : 118,000 à 136,990 MHz. C'est exactement ce qu'une radio
    /// COM sait afficher, espacement 25 kHz comme 8,33 kHz compris.
    /// </summary>
    public static bool IsAviationComBand(double mhz) => mhz >= 118.0 && mhz < 137.0;

    // Texte "type" OurAirports (ou nom MSFS) -> ControllerType.
    //
    // Le jeu de données est BEAUCOUP moins normalisé qu'il n'y paraît : à côté des codes
    // courts on trouve du texte libre (« Melbourne Centre », « Area Control », « APP/DEP »,
    // « CLNC DEL »…). Surtout, le centre en-route s'y écrit « CNTR » (1211 entrées) et
    // « ACC » (157) — et non « CTR », qui ne compte que 23 entrées. Ne reconnaître que
    // « CTR » revenait à ne JAMAIS trouver de fréquence Centre.
    //
    // D'où deux étages : les codes exacts, puis une reconnaissance par sous-chaîne pour le
    // texte libre. L'ordre du second étage compte (« Ground Control » ne doit pas devenir
    // un centre).
    public static ControllerType FromText(string? type)
    {
        string t = (type ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0) return ControllerType.Unknown;

        switch (t)
        {
            case "CLD" or "CLR" or "DEL" or "CLRN" or "CLRD" or "DELIVERY" or "CLEARANCE"
                 or "CLNC DEL" or "CLR DLVR":
                return ControllerType.Clearance;
            case "GND" or "GROUND":
                return ControllerType.Ground;
            case "TWR" or "TOWER":
                return ControllerType.Tower;
            case "APP" or "APPROACH" or "ARR" or "DIR" or "RDR" or "RADAR":
                return ControllerType.Approach;
            case "DEP" or "DEPARTURE" or "DEPARTURES" or "DEPT":
                return ControllerType.Departure;
            case "CNTR" or "CTR" or "CTL" or "CTRL" or "ACC" or "ARTC"
                 or "CENTER" or "CENTRE" or "CONTROL":
                return ControllerType.Center;
            case "ATIS" or "AWOS" or "ASOS":
                return ControllerType.Atis;
        }

        if (t.Contains("CENTER") || t.Contains("CENTRE") || t.Contains("CTR")) return ControllerType.Center;
        if (t.Contains("DEP")) return ControllerType.Departure;      // « APP/DEP » -> l'un ou l'autre convient
        if (t.Contains("APP")) return ControllerType.Approach;
        if (t.Contains("DEL") || t.Contains("CLNC") || t.Contains("CLR")) return ControllerType.Clearance;
        if (t.Contains("GROUND")) return ControllerType.Ground;
        if (t.Contains("TWR") || t.Contains("TOWER")) return ControllerType.Tower;
        if (t.Contains("ATIS")) return ControllerType.Atis;

        return ControllerType.Unknown;
    }

    /// <summary>
    /// Type de fréquence numérique MSFS (facility data « TYPE ») -> ControllerType. Filet de
    /// secours quand le NOM ne suffit pas à classer (« RAMP 1 », nom propre au terrain…).
    /// Valeurs SDK : 0 NONE, 1 ATIS, 2 MULTICOM, 3 UNICOM, 4 CTAF, 5 GROUND, 6 TOWER,
    /// 7 CLEARANCE, 8 APPROACH, 9 DEPARTURE, 10 CENTER, 11 FSS, 12 AWOS, 13 ASOS,
    /// 14 CPT (clearance pre-taxi), 15 GCO (remote clearance delivery).
    /// </summary>
    public static ControllerType FromSimComType(int simType) => simType switch
    {
        1 => ControllerType.Atis,
        5 => ControllerType.Ground,
        6 => ControllerType.Tower,
        7 => ControllerType.Clearance,
        8 => ControllerType.Approach,
        9 => ControllerType.Departure,
        10 => ControllerType.Center,
        12 or 13 => ControllerType.Atis,       // météo automatique -> traité comme ATIS
        14 or 15 => ControllerType.Clearance,  // pré-taxi / clearance distante
        _ => ControllerType.Unknown,           // NONE / MULTICOM / UNICOM / CTAF / FSS -> texte libre
    };

    /// <summary>Libellé court affiché (« TWR », « GND »…), vide pour du texte libre.</summary>
    public static string ShortLabel(ControllerType t) => t switch
    {
        ControllerType.Clearance => "CLR",
        ControllerType.Ground => "GND",
        ControllerType.Tower => "TWR",
        ControllerType.Approach => "APP",
        ControllerType.Departure => "DEP",
        ControllerType.Center => "CTR",
        ControllerType.Atis => "ATIS",
        _ => "",
    };

    /// <summary>Ordre d'utilisation le long d'un vol — pas l'ordre alphabétique.</summary>
    public static int SortRank(ControllerType t) => t switch
    {
        ControllerType.Atis => 0,
        ControllerType.Clearance => 1,
        ControllerType.Ground => 2,
        ControllerType.Tower => 3,
        ControllerType.Departure => 4,
        ControllerType.Approach => 5,
        ControllerType.Center => 6,
        _ => 7,   // texte libre en fin de liste
    };

    /// <summary>
    /// Construit la liste d'affichage des fréquences d'un terrain à partir d'entrées brutes
    /// (fréquence MHz + nom/type texte + type numérique MSFS optionnel). Filtre le hors-bande,
    /// classe, déduplique les doublons libellé+fréquence, trie dans l'ordre d'un vol. Utilisée
    /// AUSSI BIEN pour le CSV OurAirports que pour les fréquences live du simulateur.
    /// </summary>
    public static List<AirportFrequency> BuildList(IEnumerable<(double Mhz, string? Name, int? SimType)> src)
    {
        var result = new List<AirportFrequency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (mhz, name, simType) in src)
        {
            // HORS BANDE AVIATION -> on n'affiche pas (entrées militaires UHF, ATIS sur VOR…).
            if (!IsAviationComBand(mhz)) continue;

            var type = FromText(name);
            if (type == ControllerType.Unknown && simType is int st) type = FromSimComType(st);

            // Libellé court quand la position est reconnue ; sinon le texte brut, qui reste
            // informatif (« UNICOM », « FIRE », « OPS »…).
            string label = ShortLabel(type);
            if (string.IsNullOrEmpty(label)) label = (name ?? "").Trim().ToUpperInvariant();
            if (label.Length == 0) label = "—";

            if (!seen.Add(label + "|" + mhz.ToString("F3", CultureInfo.InvariantCulture))) continue;

            result.Add(new AirportFrequency(label, type, mhz));
        }

        // Même fréquence avec une entrée de type RECONNU (TWR/APP…) ET une entrée « texte libre »
        // (Unknown, ex. « A/D ») : la reconnue prime — on n'affiche pas « 119.700 APP » ET
        // « 119.700 A/D ». Deux types RECONNUS sur une même fréquence (APP + DEP d'un poste
        // combiné) restent, eux, tous les deux.
        var recognized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in result)
            if (f.Type != ControllerType.Unknown)
                recognized.Add(f.Mhz.ToString("F3", CultureInfo.InvariantCulture));
        if (recognized.Count > 0)
            result = result.Where(f => f.Type != ControllerType.Unknown
                                       || !recognized.Contains(f.Mhz.ToString("F3", CultureInfo.InvariantCulture)))
                           .ToList();

        result.Sort((a, b) =>
        {
            int byType = SortRank(a.Type).CompareTo(SortRank(b.Type));
            return byType != 0 ? byType : a.Mhz.CompareTo(b.Mhz);
        });
        return result;
    }
}
