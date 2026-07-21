using FreqWatch.Atc.Llm;
using FreqWatch.Settings;

namespace FreqWatch.Atc;

/// <summary>
/// Choisit le générateur selon les réglages, à chaque appel :
///  - LLM désactivé (défaut) -> templates ;
///  - Ollama -> LLM local, repli templates ;
///  - Cloud -> BYOK si la clé (variable d'env) est présente, sinon templates.
/// Le LLM n'est donc JAMAIS obligatoire : sans configuration, on reste sur les templates.
/// </summary>
public sealed class AtcLineGeneratorSelector : IAtcLineGenerator
{
    private readonly SettingsService _settings;
    private readonly IAtcLineGenerator _template;

    public AtcLineGeneratorSelector(SettingsService settings, IAtcLineGenerator template)
    {
        _settings = settings;
        _template = template;
    }

    public Task<string> GenerateAsync(FlightSnapshot flight, AtcTrigger trigger, CancellationToken ct = default)
    {
        var cfg = _settings.Current;
        ILlmClient? client = cfg.Llm switch
        {
            LlmMode.Ollama => new OllamaLlmClient(cfg.OllamaUrl, cfg.OllamaModel),
            LlmMode.Cloud => MakeCloudClient(cfg),
            _ => null,
        };

        if (client is null)
            return _template.GenerateAsync(flight, trigger, ct);

        var llm = new LlmAtcLineGenerator(client, _template, () => AtcLanguage.English);
        return llm.GenerateAsync(flight, trigger, ct);
    }

    private static ILlmClient? MakeCloudClient(AppSettings cfg)
    {
        string? key = Environment.GetEnvironmentVariable(cfg.CloudApiKeyEnvVar);
        return string.IsNullOrWhiteSpace(key)
            ? null // pas de clé -> on retombe sur les templates
            : new OpenAiCompatibleLlmClient(cfg.CloudBaseUrl, cfg.CloudModel, key);
    }
}
