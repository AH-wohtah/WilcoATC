using FreqWatch.Atc.Llm;

namespace FreqWatch.Atc;

/// <summary>
/// Adaptateur LLM : phrase la transmission plus naturellement, derrière la MÊME
/// interface que les templates. En cas d'échec (LLM injoignable, timeout, réponse
/// vide), il retombe systématiquement sur le générateur de templates fourni.
/// </summary>
public sealed class LlmAtcLineGenerator : IAtcLineGenerator
{
    private readonly ILlmClient _client;
    private readonly IAtcLineGenerator _fallback;
    private readonly Func<AtcLanguage> _language;

    public LlmAtcLineGenerator(ILlmClient client, IAtcLineGenerator fallback, Func<AtcLanguage> language)
    {
        _client = client;
        _fallback = fallback;
        _language = language;
    }

    public async Task<string> GenerateAsync(FlightSnapshot f, AtcTrigger trigger, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            string? text = await _client.CompleteAsync(SystemPrompt(_language()), UserPrompt(f, trigger), cts.Token);
            text = Sanitize(text);
            if (!string.IsNullOrWhiteSpace(text)) return text!;
        }
        catch { /* repli templates ci-dessous */ }

        return await _fallback.GenerateAsync(f, trigger, ct);
    }

    private static string SystemPrompt(AtcLanguage lang)
    {
        string language = lang switch { _ => "English" };   // une seule langue pour l'instant
        return $"You are an air traffic controller. Produce ONE short, realistic radio transmission " +
               $"addressed TO the pilot, in {language}, using standard ICAO phraseology. " +
               "Always include the aircraft callsign. Output ONLY the transmission text, one line, " +
               "no quotes, no explanations, no callsign of the controller station beyond the given one.";
    }

    private static string UserPrompt(FlightSnapshot f, AtcTrigger trigger)
    {
        string ctx = trigger == AtcTrigger.ManualTest ? "radio check / test" : "pilot just tuned in (initial contact)";
        return $"Context: {ctx}.\n" +
               $"Callsign: {f.Callsign}\n" +
               $"Station: {StationSpeech.Prettify(f.Station, f.NearestAirportIcao)}\n" +
               $"On ground: {f.OnGround}\n" +
               $"Altitude MSL: {f.AltitudeMslFeet:F0} ft\n" +
               $"Heading: {f.HeadingTrueDeg:F0}\n" +
               $"IAS: {f.IasKnots:F0} kt\n" +
               $"COM1: {f.Com1ActiveMhz} MHz";
    }

    private static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim().Trim('"', '\'', '`');
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) s = s[..nl].Trim();          // une seule ligne
        if (s.Length > 240) s = s[..240];         // garde-fou longueur
        return s;
    }
}
