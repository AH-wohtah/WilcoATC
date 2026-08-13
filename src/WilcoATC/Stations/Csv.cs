using System.Globalization;
using System.IO;

namespace WilcoATC.Stations;

/// <summary>
/// Mini-lecteur CSV suffisant pour les fichiers OurAirports (en-tête + champs
/// éventuellement entre guillemets doubles). Volontairement minimal et isolé :
/// aucune dépendance externe.
/// </summary>
internal static class Csv
{
    /// <summary>Une ligne CSV indexée par nom de colonne (d'après l'en-tête).</summary>
    internal sealed class Row
    {
        private readonly Dictionary<string, int> _index;
        private readonly string[] _fields;

        public Row(Dictionary<string, int> index, string[] fields)
        {
            _index = index;
            _fields = fields;
        }

        public bool TryGet(string column, out string value)
        {
            value = "";
            if (!_index.TryGetValue(column, out int i) || i >= _fields.Length) return false;
            value = _fields[i];
            return true;
        }

        public bool TryGetDouble(string column, out double value)
        {
            value = 0;
            return TryGet(column, out var s)
                   && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    public static IEnumerable<Row> Read(string path)
    {
        using var reader = new StreamReader(path);

        string? header = reader.ReadLine();
        if (header is null) yield break;

        var cols = ParseLine(header);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cols.Length; i++) index[cols[i]] = i;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            yield return new Row(index, ParseLine(line));
        }
    }

    // Analyse une ligne CSV en gérant les guillemets doubles et les "" échappés.
    private static string[] ParseLine(string line)
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
