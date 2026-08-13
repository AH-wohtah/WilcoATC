using System.Text;
using WilcoATC.Atc.Context;

namespace WilcoATC.Immersion;

/// <summary>Une prise de parole : qui parle (identité = voix), et quoi.</summary>
public sealed record ChatterTurn(string Speaker, bool IsController, string Text);

/// <summary>Un échange complet : l'un appelle, l'autre répond.</summary>
public sealed record ChatterExchange(string Callsign, IReadOnlyList<ChatterTurn> Turns);

/// <summary>
/// Type de fréquence écoutée : détermine CE QUE les autres équipages peuvent demander.
/// On n'entend pas une demande de repoussage sur la fréquence de départ.
/// </summary>
public enum ChatterScope
{
    Ground,     // sol / prévol : repoussage, roulage, poste
    Tower,      // tour : alignement, décollage, atterrissage, dégagement
    Departure,  // départ : montée initiale, identification radar
    Approach,   // approche : descente, guidage, ILS
    Center,     // en-route : niveaux, directs, transferts
}

/// <summary>
/// Trafic radio AMBIANT sous forme de VRAIS ÉCHANGES : un équipage appelle et le contrôle
/// répond (ou l'inverse), chacun avec sa propre voix. Purement cosmétique — aucun trafic
/// n'est créé dans le simulateur, ce sont juste des voix.
///
/// ANGLAIS UNIQUEMENT (standard international de l'aviation).
/// Logique PURE (horloge + tirage) donc testable.
/// </summary>
public sealed class ChatterDirector
{
    // Part de trafic « long-courrier étranger de passage » (une KLM/Emirates hors de sa région).
    // Le reste = compagnies de la RÉGION du terrain le plus proche. Voir AirlineRegistry.
    private const double ForeignLongHaulChance = 0.15;

    private static readonly string[] Waypoints =
    {
        "BOGNA", "SOSAL", "LUKIP", "ROTOS", "DEVOL", "ATSIX", "KOBBI", "MOPAR", "NEBRO", "TELNO",
    };

    /// <summary>
    /// Types d'appareils légers, cités dans l'indicatif comme le veut l'usage en aviation
    /// générale (« Cessna Golf Bravo Kilo Lima »).
    /// </summary>
    private static readonly string[] LightTypes =
    {
        "Cessna", "Piper", "Robin", "Cirrus", "Diamond", "Katana", "Bonanza", "Warrior",
    };

    /// <summary>Points de report VFR : des lieux-dits, pas des points de navigation IFR.</summary>
    private static readonly string[] VfrReportingPoints =
    {
        "the lake", "the motorway junction", "the power station", "the railway bridge",
        "the golf course", "the water tower", "the quarry", "November", "Sierra", "Whiskey",
    };

    private static readonly char[] RegLetters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

    private readonly Random _rng;
    private double _sinceLast;
    private double _nextGap = -1;

    public ChatterDirector(Random? rng = null) => _rng = rng ?? new Random();

    public void Reset() { _sinceLast = 0; _nextGap = -1; }

    /// <summary>
    /// Avance l'horloge ; renvoie un échange quand le délai est écoulé, sinon null.
    /// <paramref name="scope"/> restreint les demandes au type de fréquence écoutée.
    /// </summary>
    public ChatterExchange? Update(double dtSeconds, int minGap, int maxGap,
                                   string? station, ChatterScope scope,
                                   FlightRules rules = FlightRules.Ifr,
                                   AirlineRegion region = AirlineRegion.Unknown)
    {
        if (_nextGap < 0) _nextGap = NextGap(minGap, maxGap);
        _sinceLast += dtSeconds;
        if (_sinceLast < _nextGap) return null;

        _sinceLast = 0;
        _nextGap = NextGap(minGap, maxGap);
        return Exchange(station, scope, rules, region);
    }

    private double NextGap(int minGap, int maxGap)
    {
        int lo = Math.Max(5, minGap);
        int hi = Math.Max(lo + 1, maxGap);
        return _rng.Next(lo, hi);
    }

    /// <summary>
    /// Compose un échange pilote ↔ contrôle, COHÉRENT avec la fréquence écoutée ET avec les
    /// règles de vol. Sur la fréquence d'un aéroclub on n'entend pas Lufthansa établi ILS :
    /// en VFR les autres appareils sont eux aussi des avions légers, avec des indicatifs
    /// épelés, des altitudes en pieds et des positions dans le circuit.
    /// </summary>
    /// <summary>
    /// Indicatif du JOUEUR, à ne jamais attribuer à un équipage d'ambiance. Fourni par
    /// l'appelant, qui seul le connaît ; null tant qu'il n'est pas câblé, auquel cas la
    /// garde est simplement inopérante.
    /// </summary>
    public Func<string?>? PlayerCallsign { get; set; }

    private bool SameAsMine(string callsign)
    {
        string mine = Simplify(PlayerCallsign?.Invoke());
        return mine.Length > 0 && Simplify(callsign) == mine;
    }

    /// <summary>Forme comparable : minuscules, sans espaces ni ponctuation.</summary>
    private static string Simplify(string? s)
        => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public ChatterExchange Exchange(string? station, ChatterScope scope,
                                    FlightRules rules = FlightRules.Ifr,
                                    AirlineRegion region = AirlineRegion.Unknown)
    {
        bool vfr = rules == FlightRules.Vfr;

        // INDICATIF DIFFÉRENT DE CELUI DU JOUEUR. Les indicatifs d'ambiance sont tirés au
        // hasard dans un espace restreint : la collision avec celui du pilote finit par
        // arriver, et elle est ravageuse — un équipage de synthèse répond alors à sa place,
        // le contrôleur tient le collationnement pour fait, et le pilote perd la main sans
        // comprendre. On retire donc, quitte à insister un peu.
        string cs = vfr ? LightCallsign() : Callsign(region);
        for (int i = 0; i < 8 && SameAsMine(cs); i++)
            cs = vfr ? LightCallsign() : Callsign(region);
        string st = string.IsNullOrWhiteSpace(station) ? "Control" : station!;

        string rwy = Runway();
        string fl1 = Digits(_rng.Next(8, 39) * 10);
        string fl2 = Digits(_rng.Next(8, 39) * 10);
        string freq = Freq();
        string sq = Digits(_rng.Next(1000, 7000));
        string stand = $"{(char)('A' + _rng.Next(0, 6))}{_rng.Next(1, 40)}";
        string wpt = Waypoints[_rng.Next(Waypoints.Length)];
        string hdg = Digits(_rng.Next(1, 36) * 10);
        string alt = Digits(_rng.Next(3, 9) * 1000);

        var table = vfr
            ? VfrTemplates(scope, st, cs, rwy, freq,
                           Digits(_rng.Next(15, 45) * 100),                 // altitude VFR : 1500 à 4500 ft
                           VfrReportingPoints[_rng.Next(VfrReportingPoints.Length)],
                           Circuit())
            : Templates(scope, st, cs, rwy, fl1, fl2, freq, sq, stand, wpt, hdg, alt);
        var t = table[_rng.Next(table.Length)];

        var pilot = new ChatterTurn(cs, IsController: false, t.Pilot);
        var atc = new ChatterTurn(st, IsController: true, t.Atc);

        var turns = t.PilotFirst
            ? new List<ChatterTurn> { pilot, atc }
            : new List<ChatterTurn> { atc, pilot };

        return new ChatterExchange(cs, turns);
    }

    private static (bool PilotFirst, string Pilot, string Atc)[] Templates(
        ChatterScope scope, string st, string cs, string rwy, string fl1, string fl2,
        string freq, string sq, string stand, string wpt, string hdg, string alt) => scope switch
    {
        ChatterScope.Ground => new[]
        {
            (true,  $"{st}, {cs}, request pushback, stand {stand}.",            $"{cs}, pushback approved, expect runway {rwy}."),
            (true,  $"{st}, {cs}, request taxi.",                                $"{cs}, taxi to holding point runway {rwy} via alpha."),
            (true,  $"{st}, {cs}, runway vacated, request taxi to stand.",       $"{cs}, taxi to stand {stand} via bravo."),
            (true,  $"{st}, {cs}, request start up, stand {stand}.",             $"{cs}, start up approved, squawk {sq}."),
        },
        ChatterScope.Tower => new[]
        {
            (true,  $"{st}, {cs}, holding short runway {rwy}.",                  $"{cs}, line up and wait runway {rwy}."),
            (true,  $"{st}, {cs}, ready for departure runway {rwy}.",            $"{cs}, runway {rwy}, cleared for takeoff, wind calm."),
            (true,  $"{st}, {cs}, final runway {rwy}.",                          $"{cs}, runway {rwy}, cleared to land."),
            (false, $"Vacating right, {cs}, good day.",                          $"{cs}, vacate next right, contact ground on {freq}."),
        },
        ChatterScope.Departure => new[]
        {
            (true,  $"{st}, {cs}, airborne runway {rwy}, climbing {fl1}.",       $"{cs}, radar contact, climb flight level {fl2}."),
            (true,  $"{st}, {cs}, passing {fl1}, climbing {fl2}.",               $"{cs}, identified, continue climb flight level {fl2}."),
            (false, $"Direct {wpt}, {cs}.",                                      $"{cs}, cleared direct {wpt}, climb flight level {fl2}."),
            (false, $"Center on {freq}, {cs}, good day.",                        $"{cs}, contact center on {freq}, good day."),
        },
        ChatterScope.Approach => new[]
        {
            (true,  $"{st}, {cs}, descending flight level {fl1}, information alpha.", $"{cs}, descend altitude {alt} feet, QNH 1013."),
            (true,  $"{st}, {cs}, established ILS runway {rwy}.",                $"{cs}, contact tower on {freq}, good day."),
            (false, $"Left heading {hdg}, descend {alt} feet, {cs}.",            $"{cs}, turn left heading {hdg}, descend {alt} feet."),
            (true,  $"{st}, {cs}, request vectors ILS runway {rwy}.",            $"{cs}, turn right heading {hdg}, cleared ILS runway {rwy}."),
        },
        _ => new[] // Center / en-route
        {
            (true,  $"{st}, {cs}, passing flight level {fl1} for flight level {fl2}.", $"{cs}, roger, maintain flight level {fl2}."),
            (true,  $"{st}, {cs}, request direct {wpt}.",                        $"{cs}, cleared direct {wpt}."),
            (true,  $"{st}, {cs}, request descent.",                             $"{cs}, descend flight level {fl2}, report leaving."),
            (false, $"{freq}, {cs}, good day.",                                  $"{cs}, contact next center on {freq}, good day."),
            (true,  $"{st}, {cs}, with you, level {fl1}.",                        $"{cs}, identified, maintain flight level {fl1}."),
        },
    };

    /// <summary>
    /// Répertoire VFR. Trois différences de fond avec l'IFR, pas seulement de vocabulaire :
    /// les altitudes sont en PIEDS (jamais de niveau de vol), les positions sont dans le
    /// CIRCUIT ou sur des points de report au sol (jamais des points de navigation), et le
    /// contrôle informe du trafic au lieu de guider au radar.
    /// </summary>
    private static (bool PilotFirst, string Pilot, string Atc)[] VfrTemplates(
        ChatterScope scope, string st, string cs, string rwy, string freq,
        string alt, string point, string circuit) => scope switch
    {
        ChatterScope.Ground => new[]
        {
            (true,  $"{st}, {cs}, at the club house, request taxi for a local flight.",  $"{cs}, taxi to holding point runway {rwy}, QNH one zero one three."),
            (true,  $"{st}, {cs}, request taxi, information alpha.",                     $"{cs}, taxi holding point runway {rwy}, report ready."),
            (true,  $"{st}, {cs}, runway vacated.",                                      $"{cs}, taxi to parking, good day."),
            (true,  $"{st}, {cs}, request start up for circuits.",                       $"{cs}, start up approved, {circuit} circuit in use runway {rwy}."),
        },
        ChatterScope.Tower => new[]
        {
            (true,  $"{st}, {cs}, holding short runway {rwy}, ready for departure.",     $"{cs}, runway {rwy}, cleared for takeoff, {circuit} circuit approved."),
            (true,  $"{st}, {cs}, downwind runway {rwy}, touch and go.",                 $"{cs}, number two, follow the traffic on base, cleared touch and go."),
            (true,  $"{st}, {cs}, final runway {rwy}.",                                  $"{cs}, runway {rwy}, cleared to land, wind calm."),
            (true,  $"{st}, {cs}, overhead {point}, {alt} feet, request join.",          $"{cs}, join {circuit} downwind runway {rwy}, report downwind."),
            (false, $"Leaving the zone to the north, {cs}, good day.",                   $"{cs}, frequency change approved, squawk VFR, good day."),
        },
        ChatterScope.Departure or ChatterScope.Approach => new[]
        {
            (true,  $"{st}, {cs}, {alt} feet, request zone transit.",                    $"{cs}, cleared to transit the zone, {alt} feet, report {point}."),
            (true,  $"{st}, {cs}, overhead {point}, {alt} feet, VFR.",                   $"{cs}, roger, maintain VFR, report field in sight."),
            (true,  $"{st}, {cs}, request traffic information.",                         $"{cs}, one light aircraft in your twelve o'clock, two miles, opposite direction."),
            (true,  $"{st}, {cs}, field in sight.",                                      $"{cs}, contact tower on {freq}, good day."),
        },
        _ => new[] // en route à vue : personne ne guide, on informe
        {
            (true,  $"{st}, {cs}, {alt} feet, VFR to the coast.",                        $"{cs}, roger, no reported traffic, report {point}."),
            (true,  $"{st}, {cs}, passing {point}, {alt} feet.",                         $"{cs}, roger, maintain VFR, QNH one zero one three."),
            (true,  $"{st}, {cs}, request descent to {alt} feet.",                       $"{cs}, descend at your discretion, maintain VFR."),
            (false, $"{cs}, going en route frequency, good day.",                        $"{cs}, frequency change approved, squawk VFR."),
        },
    };

    // Indicatif de ligne RÉGIONAL : une compagnie de la région du terrain (ou, ~15% du temps,
    // un long-courrier étranger de passage). Voir AirlineRegistry.
    private string Callsign(AirlineRegion region)
        => $"{AirlineRegistry.Pick(region, _rng, ForeignLongHaulChance)} {Digits(_rng.Next(100, 9999))}";

    /// <summary>
    /// Indicatif d'aviation générale : type + immatriculation épelée en alphabet OACI.
    /// C'est ce qu'on entend réellement sur une fréquence de terrain — pas « Speedbird 42 ».
    /// </summary>
    private string LightCallsign()
    {
        string type = LightTypes[_rng.Next(LightTypes.Length)];
        var sb = new StringBuilder(type);
        for (int i = 0; i < 4; i++)
        {
            sb.Append(' ');
            sb.Append(Phonetic(RegLetters[_rng.Next(RegLetters.Length)]));
        }
        return sb.ToString();
    }

    private string Circuit() => _rng.Next(3) == 0 ? "right hand" : "left hand";

    private static string Phonetic(char c) => c switch
    {
        'A' => "Alpha", 'B' => "Bravo", 'C' => "Charlie", 'D' => "Delta", 'E' => "Echo",
        'F' => "Foxtrot", 'G' => "Golf", 'H' => "Hotel", 'I' => "India", 'J' => "Juliet",
        'K' => "Kilo", 'L' => "Lima", 'M' => "Mike", 'N' => "November", 'O' => "Oscar",
        'P' => "Papa", 'Q' => "Quebec", 'R' => "Romeo", 'S' => "Sierra", 'T' => "Tango",
        'U' => "Uniform", 'V' => "Victor", 'W' => "Whiskey", 'X' => "X-ray", 'Y' => "Yankee",
        _ => "Zulu",
    };

    private string Runway()
    {
        string n = Digits(_rng.Next(1, 37));
        return _rng.Next(3) switch { 0 => n + " left", 1 => n + " right", _ => n };
    }

    private string Freq()
        => $"{_rng.Next(118, 137)}.{_rng.Next(0, 20) * 5:000}".TrimEnd('0').TrimEnd('.');

    /// <summary>Épelle un nombre chiffre par chiffre (phraséologie : « 320 » -> « 3 2 0 »).</summary>
    private static string Digits(int value)
    {
        var sb = new StringBuilder();
        foreach (char c in value.ToString())
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
