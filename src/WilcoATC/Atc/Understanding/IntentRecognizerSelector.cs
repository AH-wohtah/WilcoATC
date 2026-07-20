using FreqWatch.Atc.Llm;
using FreqWatch.Settings;

namespace FreqWatch.Atc.Understanding;

/// <summary>
/// Choisit le recognizer selon le réglage LLM (partagé avec le générateur de phrases) :
///  - Off (défaut) -> grammaire ;
///  - Ollama / Cloud -> LLM avec repli grammaire.
/// Sans configuration, la grammaire seule suffit.
/// </summary>
public sealed class IntentRecognizerSelector : IIntentRecognizer
{
    private readonly SettingsService _settings;
    private readonly IIntentRecognizer _grammar;

    public IntentRecognizerSelector(SettingsService settings, IIntentRecognizer grammar)
    {
        _settings = settings;
        _grammar = grammar;
    }

    public Task<RecognizedIntent> RecognizeAsync(string text, CancellationToken ct = default)
    {
        var cfg = _settings.Current;
        ILlmClient? client = cfg.Llm switch
        {
            LlmMode.Ollama => new OllamaLlmClient(cfg.OllamaUrl, cfg.OllamaModel),
            LlmMode.Cloud => MakeCloud(cfg),
            _ => null,
        };

        if (client is null) return _grammar.RecognizeAsync(text, ct);
        return new LlmIntentRecognizer(client, _grammar).RecognizeAsync(text, ct);
    }

    private static ILlmClient? MakeCloud(AppSettings cfg)
    {
        string? key = Environment.GetEnvironmentVariable(cfg.CloudApiKeyEnvVar);
        return string.IsNullOrWhiteSpace(key) ? null : new OpenAiCompatibleLlmClient(cfg.CloudBaseUrl, cfg.CloudModel, key);
    }
}
