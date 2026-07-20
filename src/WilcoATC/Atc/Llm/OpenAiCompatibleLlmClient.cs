using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace FreqWatch.Atc.Llm;

/// <summary>
/// LLM cloud « BYOK » (clé fournie par l'utilisateur via variable d'environnement),
/// via une API OpenAI-compatible (/chat/completions). Aucune clé n'est jamais codée en dur.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiCompatibleLlmClient(string baseUrl, string model, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
    }

    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var body = new
        {
            model = _model,
            temperature = 0.7,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return null;
        return choices[0].GetProperty("message").GetProperty("content").GetString();
    }
}
