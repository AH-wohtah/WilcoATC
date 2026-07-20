using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreqWatch.Atc.Llm;

/// <summary>LLM local via Ollama (gratuit). POST {url}/api/chat, stream désactivé.</summary>
public sealed class OllamaLlmClient : ILlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _url;
    private readonly string _model;

    public OllamaLlmClient(string url, string model)
    {
        _url = url.TrimEnd('/');
        _model = model;
    }

    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var body = new
        {
            model = _model,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        using var resp = await Http.PostAsJsonAsync($"{_url}/api/chat", body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("message", out var m) &&
               m.TryGetProperty("content", out var c)
            ? c.GetString()
            : null;
    }
}
