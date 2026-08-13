namespace WilcoATC.Immersion;

/// <summary>Grande région du monde d'où une compagnie est « locale » (pour le trafic ambiant).</summary>
public enum AirlineRegion
{
    NorthAmerica,
    LatinAmerica,
    Europe,
    Africa,
    MiddleEast,
    SouthAsia,
    EastAsia,
    SoutheastAsia,
    Oceania,
    Unknown,
}

/// <summary>Compagnie : indicatif RADIO (télophonie) + région d'attache + long-courrier ?</summary>
public sealed record Airline(string Telephony, AirlineRegion Region, bool LongHaul);

/// <summary>
/// Répertoire de compagnies TAGUÉES par région, pour un trafic ambiant réaliste : au-dessus de
/// Manille on entend des transporteurs asiatiques, pas « Lufthansa 123 ». Les compagnies
/// ÉTRANGÈRES n'apparaissent qu'en LONG-COURRIER (une KLM à Tokyo est crédible ; une KLM en
/// domestique indien ne l'est pas). La région se déduit du préfixe OACI du terrain le plus proche.
/// </summary>
public static class AirlineRegistry
{
    /// <summary>Compagnie « locale » d'après le préfixe OACI de l'aéroport (RP = Philippines…).</summary>
    public static AirlineRegion FromIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao) || icao!.Length < 2) return AirlineRegion.Unknown;
        string p2 = icao.Substring(0, 2).ToUpperInvariant();

        // Exceptions à DEUX lettres (elles priment sur la lettre de région).
        switch (p2)
        {
            case "RP": return AirlineRegion.SoutheastAsia;                 // Philippines
            case "VH" or "VM": return AirlineRegion.EastAsia;             // Hong Kong, Macao
            case "VT" or "VD" or "VL" or "VV" or "VY" or "VP":            // Thaï, Cambodge, Laos, Vietnam, Myanmar
                return AirlineRegion.SoutheastAsia;
            case "OP": return AirlineRegion.SouthAsia;                     // Pakistan
            case "LL" or "LC": return AirlineRegion.MiddleEast;          // Israël, Chypre
            case "GC" or "GE": return AirlineRegion.Europe;              // Canaries, Melilla (Espagne)
            case "BG": return AirlineRegion.NorthAmerica;                // Groenland
            case "PA" or "PH" or "PB" or "PF" or "PO" or "PP":           // Alaska / Hawaii (USA)
                return AirlineRegion.NorthAmerica;
        }

        return p2[0] switch
        {
            'K' or 'C' => AirlineRegion.NorthAmerica,
            'M' or 'S' or 'T' => AirlineRegion.LatinAmerica,
            'E' or 'L' or 'B' or 'U' => AirlineRegion.Europe,             // U = Russie/CIS -> pool Europe
            'O' => AirlineRegion.MiddleEast,
            'V' => AirlineRegion.SouthAsia,                               // Inde et voisins (SE-Asie en exception)
            'W' => AirlineRegion.SoutheastAsia,
            'R' => AirlineRegion.EastAsia,                                // Japon/Corée/Taïwan (RP traité plus haut)
            'Z' => AirlineRegion.EastAsia,                                // Chine, Mongolie
            'F' or 'H' or 'D' or 'G' => AirlineRegion.Africa,
            'Y' or 'N' or 'A' or 'P' => AirlineRegion.Oceania,
            _ => AirlineRegion.Unknown,
        };
    }

    /// <summary>
    /// Choisit un indicatif de compagnie pour un vol ambiant dans <paramref name="region"/>.
    /// Avec la probabilité <paramref name="foreignLongHaulChance"/>, c'est un LONG-COURRIER
    /// étranger de passage ; sinon une compagnie de la région. Région inconnue -> tirage global.
    /// </summary>
    public static string Pick(AirlineRegion region, Random rng, double foreignLongHaulChance)
    {
        if (region != AirlineRegion.Unknown && rng.NextDouble() < foreignLongHaulChance)
        {
            var foreign = All.Where(a => a.LongHaul && a.Region != region).ToArray();
            if (foreign.Length > 0) return foreign[rng.Next(foreign.Length)].Telephony;
        }

        var local = region == AirlineRegion.Unknown
            ? (IReadOnlyList<Airline>)All
            : All.Where(a => a.Region == region).ToArray();
        if (local.Count == 0) local = All;
        return local[rng.Next(local.Count)].Telephony;
    }

    // Indicatifs RADIO réels (télophonie OACI), pas les noms commerciaux : « Airfrans » = Air
    // France, « Springbok » = South African, « Dynasty » = China Airlines, « Red Cap » = AirAsia.
    public static readonly IReadOnlyList<Airline> All = new[]
    {
        // ---- Amérique du Nord ----
        new Airline("American",   AirlineRegion.NorthAmerica, true),
        new Airline("United",     AirlineRegion.NorthAmerica, true),
        new Airline("Delta",      AirlineRegion.NorthAmerica, true),
        new Airline("Air Canada", AirlineRegion.NorthAmerica, true),
        new Airline("Southwest",  AirlineRegion.NorthAmerica, false),
        new Airline("JetBlue",    AirlineRegion.NorthAmerica, false),
        new Airline("Alaska",     AirlineRegion.NorthAmerica, false),
        new Airline("Spirit Wings", AirlineRegion.NorthAmerica, false),
        new Airline("Frontier Flight", AirlineRegion.NorthAmerica, false),
        new Airline("WestJet",    AirlineRegion.NorthAmerica, false),

        // ---- Europe (+ Russie/CIS) ----
        new Airline("Speedbird",     AirlineRegion.Europe, true),
        new Airline("Lufthansa",     AirlineRegion.Europe, true),
        new Airline("Airfrans",      AirlineRegion.Europe, true),
        new Airline("KLM",           AirlineRegion.Europe, true),
        new Airline("Iberia",        AirlineRegion.Europe, true),
        new Airline("Swiss",         AirlineRegion.Europe, true),
        new Airline("Scandinavian",  AirlineRegion.Europe, true),
        new Airline("Air Portugal",  AirlineRegion.Europe, true),
        new Airline("Turkish",       AirlineRegion.Europe, true),
        new Airline("Aeroflot",      AirlineRegion.Europe, true),
        new Airline("Ryanair",       AirlineRegion.Europe, false),
        new Airline("Easy",          AirlineRegion.Europe, false),
        new Airline("Wizz Air",      AirlineRegion.Europe, false),
        new Airline("Vueling",       AirlineRegion.Europe, false),
        new Airline("Eurowings",     AirlineRegion.Europe, false),
        new Airline("Austrian",      AirlineRegion.Europe, false),

        // ---- Moyen-Orient ----
        new Airline("Emirates", AirlineRegion.MiddleEast, true),
        new Airline("Qatari",   AirlineRegion.MiddleEast, true),
        new Airline("Etihad",   AirlineRegion.MiddleEast, true),
        new Airline("Saudia",   AirlineRegion.MiddleEast, true),
        new Airline("Gulf Air", AirlineRegion.MiddleEast, true),
        new Airline("Oman Air", AirlineRegion.MiddleEast, false),
        new Airline("Kuwaiti",  AirlineRegion.MiddleEast, false),
        new Airline("Jordanian",AirlineRegion.MiddleEast, false),
        new Airline("Skydubai", AirlineRegion.MiddleEast, false),
        new Airline("Arabia",   AirlineRegion.MiddleEast, false),

        // ---- Afrique ----
        new Airline("Ethiopian",      AirlineRegion.Africa, true),
        new Airline("Kenya",          AirlineRegion.Africa, true),
        new Airline("Springbok",      AirlineRegion.Africa, true),
        new Airline("Egyptair",       AirlineRegion.Africa, true),
        new Airline("Royalair Maroc", AirlineRegion.Africa, true),
        new Airline("Air Algerie",    AirlineRegion.Africa, false),
        new Airline("Tunair",         AirlineRegion.Africa, false),
        new Airline("Rwandair",       AirlineRegion.Africa, false),
        new Airline("Airmauritius",   AirlineRegion.Africa, false),
        new Airline("Arik Air",       AirlineRegion.Africa, false),

        // ---- Asie du Sud ----
        new Airline("Airindia",  AirlineRegion.SouthAsia, true),
        new Airline("Vistara",   AirlineRegion.SouthAsia, true),
        new Airline("Srilankan", AirlineRegion.SouthAsia, true),
        new Airline("Pakistan",  AirlineRegion.SouthAsia, true),
        new Airline("IndiGo",    AirlineRegion.SouthAsia, false),
        new Airline("Spicejet",  AirlineRegion.SouthAsia, false),
        new Airline("Bangladesh",AirlineRegion.SouthAsia, false),

        // ---- Asie de l'Est ----
        new Airline("Japan Air",      AirlineRegion.EastAsia, true),
        new Airline("All Nippon",     AirlineRegion.EastAsia, true),
        new Airline("Koreanair",      AirlineRegion.EastAsia, true),
        new Airline("Asiana",         AirlineRegion.EastAsia, true),
        new Airline("Air China",      AirlineRegion.EastAsia, true),
        new Airline("China Eastern",  AirlineRegion.EastAsia, true),
        new Airline("China Southern", AirlineRegion.EastAsia, true),
        new Airline("Cathay",         AirlineRegion.EastAsia, true),
        new Airline("Dynasty",        AirlineRegion.EastAsia, true),
        new Airline("Eva",            AirlineRegion.EastAsia, true),
        new Airline("Hainan",         AirlineRegion.EastAsia, false),
        new Airline("Xiamen Air",     AirlineRegion.EastAsia, false),

        // ---- Asie du Sud-Est ----
        new Airline("Singapore",        AirlineRegion.SoutheastAsia, true),
        new Airline("Malaysian",        AirlineRegion.SoutheastAsia, true),
        new Airline("Indonesia",        AirlineRegion.SoutheastAsia, true),   // Garuda
        new Airline("Thai",             AirlineRegion.SoutheastAsia, true),
        new Airline("Vietnam Airlines", AirlineRegion.SoutheastAsia, true),
        new Airline("Philippine",       AirlineRegion.SoutheastAsia, true),
        new Airline("Cebu Air",         AirlineRegion.SoutheastAsia, false),
        new Airline("Red Cap",          AirlineRegion.SoutheastAsia, false),  // AirAsia
        new Airline("Lion Inter",       AirlineRegion.SoutheastAsia, false),
        new Airline("Scooter",          AirlineRegion.SoutheastAsia, false),  // Scoot
        new Airline("Bangkok Air",      AirlineRegion.SoutheastAsia, false),

        // ---- Océanie ----
        new Airline("Qantas",      AirlineRegion.Oceania, true),
        new Airline("New Zealand", AirlineRegion.Oceania, true),
        new Airline("Velocity",    AirlineRegion.Oceania, true),   // Virgin Australia
        new Airline("Jetstar",     AirlineRegion.Oceania, false),
        new Airline("Rex",         AirlineRegion.Oceania, false),
        new Airline("Fiji",        AirlineRegion.Oceania, true),

        // ---- Amérique latine ----
        new Airline("LATAM",      AirlineRegion.LatinAmerica, true),
        new Airline("Aeromexico", AirlineRegion.LatinAmerica, true),
        new Airline("Avianca",    AirlineRegion.LatinAmerica, true),
        new Airline("Copa",       AirlineRegion.LatinAmerica, true),
        new Airline("Volaris",    AirlineRegion.LatinAmerica, false),
        new Airline("Gol Transporte", AirlineRegion.LatinAmerica, false),
        new Airline("Azul",       AirlineRegion.LatinAmerica, false),
        new Airline("Argentina",  AirlineRegion.LatinAmerica, false),
    };
}
