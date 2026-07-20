using System.Globalization;
using System.IO;
using System.Reflection;

namespace FreqWatch.Formatting;

/// <summary>
/// Télophonie compagnie (ICAO -> nom radio, ex. « UAE » -> « Emirates ») à partir du
/// dataset OpenFlights <c>airlines.dat</c> embarqué. Colonnes : ID, Name, Alias, IATA,
/// <b>ICAO</b> (index 4), <b>Callsign</b> (index 5), Country, Active.
/// </summary>
public sealed class AirlineTelephony
{
    private readonly Dictionary<string, string> _byIcao = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _loaded;

    public string? Lookup(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return null;
        EnsureLoaded();
        return _byIcao.TryGetValue(icao.Trim(), out var v) ? v : null;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("airlines.dat", StringComparison.OrdinalIgnoreCase));
                if (name is null) return;

                using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var f = ParseCsv(line);
                    if (f.Length < 6) continue;

                    string icao = f[4].Trim();
                    string callsign = f[5].Trim();
                    if (icao.Length != 3 || !IsLetters(icao)) continue;
                    if (string.IsNullOrEmpty(callsign) || callsign == "\\N") continue;

                    if (!_byIcao.ContainsKey(icao)) // on garde la 1re occurrence
                        _byIcao[icao] = TitleCase(callsign);
                }
            }
            catch { /* dataset absent/corrompu -> lookups renverront null */ }
        }
    }

    private static bool IsLetters(string s)
    {
        foreach (char c in s) if (!char.IsLetter(c)) return false;
        return true;
    }

    private static string TitleCase(string s)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    // Petit parseur CSV : champs éventuellement entre guillemets, virgules internes gérées.
    private static string[] ParseCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
