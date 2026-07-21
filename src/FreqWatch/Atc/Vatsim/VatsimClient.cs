using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace FreqWatch.Atc.Vatsim;

/// <summary>
/// (Optionnel) Interroge le flux de données public VATSIM pour trouver la VRAIE fréquence
/// d'un Centre (contrôleur « *_CTR ») couvrant la région de l'avion. Heuristique honnête :
/// on matche par préfixe ICAO (ex. « LS » pour la Suisse). Hors-ligne / non trouvé -> null,
/// et l'appelant retombe sur une fréquence approximative clairement marquée.
/// </summary>
public sealed class VatsimClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string Url = "https://data.vatsim.net/v3/vatsim-data.json";

    private string? _cachedBody;
    private DateTime _cachedAtUtc;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(120);

    /// <summary>Fréquence (Hz) du Centre dont le callsign commence par <paramref name="icaoPrefix"/>, ou null.</summary>
    public async Task<double?> FindCenterFrequencyHzAsync(string? icaoPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(icaoPrefix)) return null;

        string? body = await GetBodyAsync(ct);
        if (body is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("controllers", out var controllers)
                || controllers.ValueKind != JsonValueKind.Array) return null;

            string prefix = icaoPrefix.Trim().ToUpperInvariant();
            foreach (var c in controllers.EnumerateArray())
            {
                string callsign = Get(c, "callsign");
                if (!callsign.EndsWith("_CTR", StringComparison.OrdinalIgnoreCase)) continue;
                if (!callsign.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                if (double.TryParse(Get(c, "frequency"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
                    && mhz > 100)
                    return mhz * 1_000_000.0;
            }
        }
        catch { /* feed illisible -> null */ }
        return null;
    }

    private async Task<string?> GetBodyAsync(CancellationToken ct)
    {
        if (_cachedBody is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cachedBody;
        try
        {
            using var resp = await Http.GetAsync(Url, ct);
            resp.EnsureSuccessStatusCode();
            _cachedBody = await resp.Content.ReadAsStringAsync(ct);
            _cachedAtUtc = DateTime.UtcNow;
            return _cachedBody;
        }
        catch { return null; }
    }

    private static string Get(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
