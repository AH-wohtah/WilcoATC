using System.Globalization;

namespace FreqWatch.Atc;

/// <summary>
/// Générateur ATC par défaut : déterministe, gratuit, hors-ligne. Choisit un
/// template réaliste selon le contexte (sol/vol, type de déclencheur) et remplit
/// les champs {callsign}, {station}, {alt}, {qnh}. Bancs de templates EN et FR.
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
        bool en = _language() == AtcLanguage.English;
        string callsign = string.IsNullOrWhiteSpace(f.Callsign) ? (en ? "Aircraft" : "Trafic") : f.Callsign;
        string station = StationSpeech.Prettify(f.Station, f.NearestAirportIcao);

        string[] pool = Pool(trigger, f.OnGround, en);
        string template = pool[_rng.Next(pool.Length)];

        return template
            .Replace("{callsign}", callsign)
            .Replace("{station}", station)
            .Replace("{alt}", SpeakAltitude(f.AltitudeMslFeet, en))
            .Replace("{qnh}", en ? "one zero one three" : "1013");
    }

    private static string[] Pool(AtcTrigger trigger, bool onGround, bool en)
    {
        if (trigger == AtcTrigger.ManualTest)
            return en
                ? new[]
                {
                    "{callsign}, {station}, radio check, reading you five by five.",
                    "{callsign}, {station}, loud and clear, how do you read?",
                }
                : new[]
                {
                    "{callsign}, {station}, essai radio, je vous reçois cinq sur cinq.",
                    "{callsign}, {station}, fort et clair, comment me recevez-vous ?",
                };

        if (onGround)
            return en
                ? new[]
                {
                    "{callsign}, {station}, good day, taxi to holding point via alpha, QNH {qnh}.",
                    "{callsign}, {station}, hold position, expect departure in sequence.",
                    "{callsign}, {station}, radar contact on the ground, standby for clearance.",
                }
                : new[]
                {
                    "{callsign}, {station}, bonjour, roulez point d'arrêt via alpha, QNH {qnh}.",
                    "{callsign}, {station}, maintenez position, départ en séquence.",
                    "{callsign}, {station}, bien reçu au sol, restez à l'écoute pour la clairance.",
                };

        return en
            ? new[]
            {
                "{callsign}, {station}, radar contact, {alt}, altimeter two niner niner two.",
                "{callsign}, {station}, good day, radar identified, maintain VFR.",
                "{callsign}, {station}, roger, remain clear of controlled airspace, report field in sight.",
            }
            : new[]
            {
                "{callsign}, {station}, contact radar, {alt}, calez {qnh}.",
                "{callsign}, {station}, bonjour, identifié radar, maintenez VFR.",
                "{callsign}, {station}, bien reçu, restez en dehors de l'espace contrôlé, rappelez terrain en vue.",
            };
    }

    private static string SpeakAltitude(double feet, bool en)
    {
        if (feet >= 18000)
        {
            int fl = (int)Math.Round(feet / 100.0);
            return en ? $"flight level {fl}" : $"niveau de vol {fl}";
        }
        long rounded = (long)(Math.Round(feet / 100.0) * 100);
        string n = rounded.ToString("N0", CultureInfo.InvariantCulture);
        return en ? $"{n} feet" : $"{n} pieds";
    }
}
