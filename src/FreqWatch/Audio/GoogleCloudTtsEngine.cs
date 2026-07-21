using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreqWatch.Audio;

/// <summary>
/// TTS via Google Cloud Text-to-Speech (voix WaveNet / Neural2 / Studio).
///
/// « BYOK » : la clé API est lue dans une VARIABLE D'ENVIRONNEMENT (jamais stockée
/// dans l'app). Optionnel : sans clé (ou en cas d'erreur), renvoie
/// <see cref="TtsAudio.Empty"/> et le sélecteur retombe sur la voix Windows.
///
/// On demande l'audio en LINEAR16 : Google renvoie un WAV (base64) qu'on décode en
/// mono float via <see cref="WavUtil"/>. Le code de langue est déduit du nom de la
/// voix (ex. « en-GB-Neural2-B » -> « en-GB »).
/// </summary>
public sealed class GoogleCloudTtsEngine : ITtsEngine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Endpoint = "https://texttospeech.googleapis.com/v1/text:synthesize";

    private readonly Func<(string KeyEnvVar, string Voice)> _config;

    public GoogleCloudTtsEngine(Func<(string, string)> config) => _config = config;

    // Quelques voix recommandées (liste statique -> pas d'appel réseau pour peupler les réglages).
    private static readonly string[] Recommended =
    {
        "en-US-Neural2-D", "en-US-Neural2-C", "en-US-Studio-O", "en-US-Wavenet-D",
        "en-GB-Neural2-B", "en-GB-Neural2-A", "en-GB-Wavenet-D",
        "en-AU-Neural2-B", "en-IN-Neural2-B",
    };

    public IReadOnlyList<string> GetVoices() => Recommended;

    public async Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        var (keyEnvVar, voice) = _config();
        string envVar = string.IsNullOrWhiteSpace(keyEnvVar) ? "FREQWATCH_GOOGLE_KEY" : keyEnvVar;
        string? key = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(voice))
            return TtsAudio.Empty; // pas de clé/voix -> repli Windows

        var body = new
        {
            input = new { text },
            voice = new { languageCode = LanguageOf(voice), name = voice },
            audioConfig = new { audioEncoding = "LINEAR16", sampleRateHertz = 22050 },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("X-Goog-Api-Key", key); // clé en en-tête, pas dans l'URL

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("audioContent", out var ac)) return TtsAudio.Empty;
        string? b64 = ac.GetString();
        if (string.IsNullOrEmpty(b64)) return TtsAudio.Empty;

        byte[] wav = Convert.FromBase64String(b64);
        using var ms = new MemoryStream(wav);
        return WavUtil.ReadMono(ms);
    }

    // "en-GB-Neural2-B" -> "en-GB"
    private static string LanguageOf(string voice)
    {
        var parts = voice.Split('-');
        return parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "en-US";
    }
}
