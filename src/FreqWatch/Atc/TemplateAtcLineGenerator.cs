using System.Globalization;

namespace FreqWatch.Atc;

/// <summary>
/// Générateur ATC par défaut : déterministe, gratuit, hors-ligne. Choisit un
/// template réaliste selon le contexte (sol/vol, type de déclencheur) et remplit
/// les champs {callsign}, {station}, {alt}, {qnh}. Phraséologie ANGLAISE.
/// </summary>
public sealed class TemplateAtcLineGenerator : IAtcLineGenerator
{
    private readonly Random _rng = new();
    private readonly Func<AtcLanguage> _language;

    public TemplateAtcLineGenerator(Func<AtcLanguage> language) => _language = language;

    public Task<string> GenerateAsync(FlightSnapshot f, AtcTrigger trigger, CancellationToken ct = default)
        => Task.FromResult(Build(f, trigger));

    private string Build(FlightSnapshot f, AtcTrigger trigger)
    {
        string callsign = string.IsNullOrWhiteSpace(f.Callsign) ? "Aircraft" : f.Callsign;
        string station = StationSpeech.Prettify(f.Station, f.NearestAirportIcao);

        string[] pool = Pool(trigger, f.OnGround);
        string template = pool[_rng.Next(pool.Length)];

        return template
            .Replace("{callsign}", callsign)
            .Replace("{station}", station)
            .Replace("{alt}", SpeakAltitude(f.AltitudeMslFeet))
            .Replace("{qnh}", "one zero one three");
    }

    private static string[] Pool(AtcTrigger trigger, bool onGround)
    {
        if (trigger == AtcTrigger.ManualTest)
            return new[]
            {
                "{callsign}, {station}, radio check, reading you five by five.",
                "{callsign}, {station}, loud and clear, how do you read?",
            };

        if (onGround)
            return new[]
            {
                "{callsign}, {station}, good day, taxi to holding point via alpha, QNH {qnh}.",
                "{callsign}, {station}, hold position, expect departure in sequence.",
                "{callsign}, {station}, radar contact on the ground, standby for clearance.",
            };

        return new[]
        {
            "{callsign}, {station}, radar contact, {alt}, altimeter two niner niner two.",
            "{callsign}, {station}, good day, radar identified, maintain VFR.",
            "{callsign}, {station}, roger, remain clear of controlled airspace, report field in sight.",
        };
    }

    private static string SpeakAltitude(double feet)
    {
        if (feet >= 18000)
        {
            int fl = (int)Math.Round(feet / 100.0);
            return $"flight level {fl}";
        }
        long rounded = (long)(Math.Round(feet / 100.0) * 100);
        return $"{rounded.ToString("N0", CultureInfo.InvariantCulture)} feet";
    }
}
