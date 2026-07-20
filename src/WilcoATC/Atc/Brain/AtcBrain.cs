using System.Text;
using FreqWatch.Atc;
using FreqWatch.Atc.Context;
using FreqWatch.Atc.Planning;
using FreqWatch.Atc.Understanding;
using FreqWatch.Common;
using FreqWatch.Formatting;
using FreqWatch.Settings;
using FreqWatch.Stations;

namespace FreqWatch.Atc.Brain;

/// <summary>
/// Valide une intention pilote (phase + contrôleur) selon la table JSON et produit la
/// réponse (clairance / refus) DANS LA LANGUE EFFECTIVE (anglais ou français). Compose
/// aussi les transmissions PROACTIVES de l'ATC (<see cref="Announce"/>).
/// </summary>
public sealed class AtcBrain
{
    private readonly AtcRuleSet _rules;
    private readonly IStationResolver _stations;
    private readonly FlightPlanStore _plans;
    private readonly CallsignFormatter _callsigns;
    private readonly SettingsService _settings;
    private readonly Func<AtcLanguage> _language;
    private readonly Random _rng = new();

    private static readonly string[] DirsEn = { "north", "south", "east", "west" };
    private static readonly string[] DirsFr = { "nord", "sud", "est", "ouest" };

    public AtcBrain(AtcRuleSet rules, IStationResolver stations, FlightPlanStore plans,
                    CallsignFormatter callsigns, SettingsService settings, Func<AtcLanguage> language)
    {
        _rules = rules;
        _stations = stations;
        _plans = plans;
        _callsigns = callsigns;
        _settings = settings;
        _language = language;
    }

    private bool Fr => _language() == AtcLanguage.French;

    /// <summary>Langue effective courante (pour aligner les libellés côté contrôleur).</summary>
    public AtcLanguage EffectiveLanguage => _language();

    public AtcDecision Evaluate(RecognizedIntent recognized, FlightContext ctx)
    {
        var intent = recognized.Intent;
        var rule = FindRule(intent);

        if (intent == PilotIntent.Unknown || rule is null)
            return Deny(intent, ctx, recognized, "unknown", "intention inconnue");

        // MODE TEST : on court-circuite la validation de contexte (phase + contrôleur +
        // sol/air). Toute requête est ACCEPTÉE. Les règles restent en place — seul le
        // franchissement des gardes est sauté. (Bascule OFF -> tout redevient strict.)
        bool testMode = _settings.Current.TestMode;

        if (!testMode)
        {
            if (rule.RequireOnGround && !ctx.OnGround)
                return Deny(intent, ctx, recognized, "airborne", "en vol alors que l'intention exige le sol");

            if (rule.AllowedControllers.Count > 0 && !MatchesController(rule.AllowedControllers, ctx.Controller))
                return Deny(intent, ctx, recognized, "wrongController", $"contrôleur {ctx.Controller} non autorisé", rule.AllowedControllers);

            if (rule.AllowedPhases.Count > 0 && !MatchesPhase(rule.AllowedPhases, ctx.Phase))
                return Deny(intent, ctx, recognized, "wrongPhase", $"phase {ctx.Phase} non autorisée");
        }

        if (intent == PilotIntent.RequestClearance && string.IsNullOrWhiteSpace(ResolveDestination(recognized)))
        {
            string sa = Fr ? "{callsign}, répétez votre destination." : "{callsign}, say again your destination.";
            return new AtcDecision(true, intent, Fill(sa, ctx, recognized, null, null), "clairance : destination inconnue", null);
        }

        string reason = testMode ? "autorisé (mode test)" : "autorisé";
        return new AtcDecision(true, intent, Fill(PickApproved(rule), ctx, recognized, null, null), reason, ParsePhase(rule.AdvanceToPhase));
    }

    // Choisit le template d'accord : variante SID si un SID est chargé, sinon le repli « as filed ».
    private string PickApproved(AtcRule rule)
    {
        bool hasSid = !string.IsNullOrWhiteSpace(_plans.Current?.SidName);
        if (!hasSid && (rule.ApprovedNoSid is not null || rule.ApprovedNoSidFr is not null))
            return Fr && !string.IsNullOrWhiteSpace(rule.ApprovedNoSidFr) ? rule.ApprovedNoSidFr! : (rule.ApprovedNoSid ?? rule.Approved);
        return Fr && !string.IsNullOrWhiteSpace(rule.ApprovedFr) ? rule.ApprovedFr! : rule.Approved;
    }

    /// <summary>Réponse « c'est déjà approuvé » (requête déjà accordée), au lieu d'un refus de phase.</summary>
    public string AlreadyApproved(FlightContext ctx)
        => Fill(Fr ? "{callsign}, c'est déjà approuvé." : "{callsign}, that is already approved.", ctx, null, null, null);

    /// <summary>Transfert de fréquence : « {callsign}, contactez {contrôleur} sur {fréquence}. ».</summary>
    public string AnnounceTransfer(FlightContext ctx, string controllerName, double freqHz)
    {
        string callsign = _callsigns.Speak(ctx.Callsign);
        if (freqHz <= 0) // fréquence inconnue (pas d'OurAirports) -> on transfère sans fréquence
            return (Fr ? "{callsign}, contactez {controller}." : "{callsign}, contact {controller}.")
                .Replace("{callsign}", callsign).Replace("{controller}", controllerName);

        return (Fr ? "{callsign}, contactez {controller} sur {freq}." : "{callsign}, contact {controller} on {freq}.")
            .Replace("{callsign}", callsign)
            .Replace("{controller}", controllerName)
            .Replace("{freq}", FrequencyFormatter.Speak(freqHz, Fr));
    }

    /// <summary>Compose une transmission PROACTIVE (clé d'événement -> texte prêt à jouer), ou null.</summary>
    public string? Announce(string eventKey, FlightContext ctx)
    {
        if (!_rules.Events.TryGetValue(eventKey, out var lt)) return null;
        string template = Fr && !string.IsNullOrWhiteSpace(lt.Fr) ? lt.Fr : lt.En;
        if (string.IsNullOrWhiteSpace(template)) return null;
        return Fill(template, ctx, null, null, HandoffFrequency(ctx));
    }

    private AtcDecision Deny(PilotIntent intent, FlightContext ctx, RecognizedIntent recognized,
                             string reasonKey, string debug, List<string>? expectedControllers = null)
    {
        var table = Fr ? _rules.DenialsFr : _rules.Denials;
        string template = table.TryGetValue(reasonKey, out var t) ? t
                          : _rules.Denials.TryGetValue(reasonKey, out var en) ? en
                          : "{callsign}, unable.";
        return new AtcDecision(false, intent, Fill(template, ctx, recognized, expectedControllers, null), debug, null);
    }

    private AtcRule? FindRule(PilotIntent intent)
    {
        string key = IntentKey(intent);
        return _rules.Rules.FirstOrDefault(r => Norm(r.Intent) == Norm(key));
    }

    private string Fill(string template, FlightContext ctx, RecognizedIntent? recognized,
                        List<string>? expectedControllers, string? freqOverride)
    {
        string callsign = _callsigns.Speak(ctx.Callsign);
        string station = StationSpeech.Prettify(ctx.StationName, ctx.AirportIcao);
        string expected = expectedControllers is { Count: > 0 }
            ? SpokenController(expectedControllers[0]) : (Fr ? "le bon contrôleur" : "the appropriate controller");
        string freq = freqOverride ?? ExpectedFrequency(ctx, expectedControllers);
        string destination = ResolveDestination(recognized) ?? (Fr ? "votre destination" : "your destination");
        string[] dirs = Fr ? DirsFr : DirsEn;

        return template
            .Replace("{callsign}", callsign)
            .Replace("{station}", station)
            .Replace("{expected}", expected)
            .Replace("{freq}", freq)
            .Replace("{destination}", destination)
            .Replace("{sid}", SidFormatter.Speak(_plans.Current?.SidName) ?? "")
            .Replace("{initial_altitude}", FormatAltitude(_settings.Current.DefaultInitialClimbFeet))
            .Replace("{squawk}", RandomSquawk())
            .Replace("{dir}", dirs[_rng.Next(dirs.Length)])
            .Replace("{runway}", Fr ? "deux sept" : "two seven");
    }

    private string? ResolveDestination(RecognizedIntent? recognized)
    {
        var plan = _plans.Current;
        if (plan is not null && !string.IsNullOrWhiteSpace(plan.DestinationDisplay))
            return plan.DestinationDisplay;

        string? hint = recognized?.DestinationHint;
        if (string.IsNullOrWhiteSpace(hint)) return null;

        if (hint!.Length == 4 && hint.All(char.IsLetter))
        {
            string? name = _stations.LookupAirportName(hint.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(name)) return FlightPlan.CleanAirportName(name);
        }
        return hint;
    }

    private string FormatAltitude(int feet)
    {
        if (feet >= 18000) return Fr ? $"niveau de vol {feet / 100}" : $"flight level {feet / 100}";
        return Fr ? $"{feet} pieds" : $"{feet} feet";
    }

    private string RandomSquawk()
    {
        int code;
        do { code = _rng.Next(0, 8) * 1000 + _rng.Next(0, 8) * 100 + _rng.Next(0, 8) * 10 + _rng.Next(0, 8); }
        while (code is 0 or 7000 or 7500 or 7600 or 7700);
        return code.ToString("D4");
    }

    // Fréquence de transfert : départ / approche / centre de l'aéroport (OurAirports), sinon générique.
    private string HandoffFrequency(FlightContext ctx)
    {
        if (ctx.AirportIcao is not null)
        {
            foreach (var type in new[] { ControllerType.Departure, ControllerType.Approach, ControllerType.Center })
            {
                double? hz = _stations.FindFrequencyHz(ctx.AirportIcao, type);
                if (hz is not null) return FrequencyFormatter.FormatMHz(hz.Value);
            }
        }
        return Fr ? "la fréquence publiée" : "the published frequency";
    }

    private string ExpectedFrequency(FlightContext ctx, List<string>? expectedControllers)
    {
        if (expectedControllers is not { Count: > 0 } || ctx.AirportIcao is null)
            return Fr ? "la fréquence appropriée" : "the appropriate frequency";
        if (!Enum.TryParse<ControllerType>(expectedControllers[0], ignoreCase: true, out var type))
            return Fr ? "la fréquence appropriée" : "the appropriate frequency";
        double? hz = _stations.FindFrequencyHz(ctx.AirportIcao, type);
        return hz is null ? (Fr ? "la fréquence appropriée" : "the appropriate frequency") : FrequencyFormatter.FormatMHz(hz.Value);
    }

    private static string SpokenController(string c) => c.ToLowerInvariant() switch
    {
        "clearance" => "Clearance Delivery",
        "ground" => "Ground",
        "tower" => "Tower",
        "approach" => "Approach",
        "departure" => "Departure",
        "center" => "Center",
        _ => c,
    };

    private static bool MatchesController(List<string> allowed, ControllerType controller)
        => allowed.Any(a => Norm(a) == Norm(controller.ToString()));

    private static bool MatchesPhase(List<string> allowed, FlightPhase phase)
        => allowed.Any(a => Norm(a) == Norm(phase.ToString()));

    private static FlightPhase? ParsePhase(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (FlightPhase p in Enum.GetValues<FlightPhase>())
            if (Norm(p.ToString()) == Norm(s)) return p;
        return null;
    }

    private static string IntentKey(PilotIntent i) => i switch
    {
        PilotIntent.RequestClearance => "REQUEST_CLEARANCE",
        PilotIntent.RequestPushback => "REQUEST_PUSHBACK",
        PilotIntent.RequestTaxi => "REQUEST_TAXI",
        PilotIntent.ReadyForDeparture => "READY_FOR_DEPARTURE",
        PilotIntent.CheckIn => "CHECK_IN",
        PilotIntent.ReportApproach => "REPORT_APPROACH",
        PilotIntent.Readback => "READBACK",
        _ => "UNKNOWN",
    };

    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
