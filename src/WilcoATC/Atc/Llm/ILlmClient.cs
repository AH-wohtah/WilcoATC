namespace FreqWatch.Atc.Llm;

/// <summary>Client LLM minimal (chat system+user -> texte). Implémentations : Ollama, cloud OpenAI-compatible.</summary>
public interface ILlmClient
{
    Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}
