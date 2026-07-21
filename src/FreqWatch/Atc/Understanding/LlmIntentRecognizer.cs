using FreqWatch.Atc.Llm;

namespace FreqWatch.Atc.Understanding;

/// <summary>
/// (Optionnel) Classe les formulations tordues dans une intention connue via un LLM
/// (Ollama local ou cloud BYOK). En cas d'échec/timeout/réponse invalide, repli sur
/// le recognizer de grammaire fourni. Jamais requis.
/// </summary>
public sealed class LlmIntentRecognizer : IIntentRecognizer
{
    private readonly ILlmClient _client;
    private readonly IIntentRecognizer _fallback;

    public LlmIntentRecognizer(ILlmClient client, IIntentRecognizer fallback)
    {
        _client = client;
        _fallback = fallback;
    }

    public async Task<RecognizedIntent> RecognizeAsync(string text, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            string? label = await _client.CompleteAsync(System, User(text), cts.Token);
            if (TryParse(label, out var intent))
                return new RecognizedIntent(intent, text, "llm");
        }
        catch { /* repli grammaire ci-dessous */ }

        return await _fallback.RecognizeAsync(text, ct);
    }

    private const string System =
        "You classify a pilot's radio message (English) into EXACTLY one label from this " +
        "closed set: REQUEST_CLEARANCE, REQUEST_PUSHBACK, REQUEST_TAXI, READY_FOR_DEPARTURE, " +
        "CHECK_IN, REPORT_APPROACH, READBACK, UNKNOWN. Reply with ONLY the label, nothing else.";

    private static string User(string text) => $"Message: {text}\nLabel:";

    private static bool TryParse(string? label, out PilotIntent intent)
    {
        intent = PilotIntent.Unknown;
        if (string.IsNullOrWhiteSpace(label)) return false;
        string l = label.Trim().ToUpperInvariant();

        intent = l switch
        {
            _ when l.Contains("CLEARANCE") => PilotIntent.RequestClearance,
            _ when l.Contains("PUSHBACK") => PilotIntent.RequestPushback,
            _ when l.Contains("TAXI") => PilotIntent.RequestTaxi,
            _ when l.Contains("DEPARTURE") => PilotIntent.ReadyForDeparture,
            _ when l.Contains("CHECK") => PilotIntent.CheckIn,
            _ when l.Contains("APPROACH") || l.Contains("REPORT") => PilotIntent.ReportApproach,
            _ when l.Contains("READBACK") => PilotIntent.Readback,
            _ => PilotIntent.Unknown,
        };
        return true; // même UNKNOWN est une classification valide
    }
}
