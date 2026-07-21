using System.Text;

namespace FreqWatch.Immersion;

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
    private static readonly string[] Airlines =
    {
        "Speedbird", "Lufthansa", "Easy", "Ryanair", "Swiss", "KLM", "Delta",
        "Iberia", "Wizz Air", "Vueling", "Transavia", "Brussels", "Austrian", "Eurowings",
    };

    private static readonly string[] Waypoints =
    {
        "BOGNA", "SOSAL", "LUKIP", "ROTOS", "DEVOL", "ATSIX", "KOBBI", "MOPAR", "NEBRO", "TELNO",
    };

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
                                   string? station, ChatterScope scope)
    {
        if (_nextGap < 0) _nextGap = NextGap(minGap, maxGap);
        _sinceLast += dtSeconds;
        if (_sinceLast < _nextGap) return null;

        _sinceLast = 0;
        _nextGap = NextGap(minGap, maxGap);
        return Exchange(station, scope);
    }

    private double NextGap(int minGap, int maxGap)
    {
        int lo = Math.Max(5, minGap);
        int hi = Math.Max(lo + 1, maxGap);
        return _rng.Next(lo, hi);
    }

    /// <summary>Compose un échange pilote ↔ contrôle, COHÉRENT avec la fréquence écoutée.</summary>
    public ChatterExchange Exchange(string? station, ChatterScope scope)
    {
        string cs = Callsign();
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

        var table = Templates(scope, st, cs, rwy, fl1, fl2, freq, sq, stand, wpt, hdg, alt);
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

    private string Callsign()
        => $"{Airlines[_rng.Next(Airlines.Length)]} {Digits(_rng.Next(100, 9999))}";

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
