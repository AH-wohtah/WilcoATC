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
/// réponse (clairance / refus). Compose aussi les transmissions PROACTIVES de l'ATC
/// (<see cref="Announce"/>). Phraséologie ANGLAISE (standard OACI).
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

    private static readonly string[] Dirs = { "north", "south", "east", "west" };

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

    /// <summary>Langue effective courante (pour aligner les libellés côté contrôleur).</summary>
    public AtcLanguage EffectiveLanguage => _language();

    public AtcDecision Evaluate(RecognizedIntent recognized, FlightContext ctx)
    {
        var intent = recognized.Intent;
        var rule = FindRule(intent);

        if (intent == PilotIntent.Unknown || rule is null)
            return Deny(intent, ctx, recognized, "unknown", "unknown intent");

        // PLUS AUCUN REFUS LIÉ À LA PHASE DE VOL. La phase est estimée à partir de SimVars
        // (vitesse, hauteur, frein de parc…) : c'est une DEVINETTE, et quand elle se trompait
        // le contrôleur répondait « unable at this time » à une demande parfaitement valable
        // — le pilote n'avait alors aucun moyen de s'en sortir. Les phases servent toujours à
        // DÉCLENCHER (annonces copilote, appels ATC d'approche, trafic ambiant cohérent),
        // jamais à INTERDIRE.
        //
        // MODE TEST : court-circuite aussi les deux gardes restantes (sol/air + contrôleur).
        bool testMode = _settings.Current.TestMode;

        if (!testMode)
        {
            if (rule.RequireOnGround && !ctx.OnGround)
                return Deny(intent, ctx, recognized, "airborne", "airborne but the request requires being on the ground");

            if (rule.AllowedControllers.Count > 0 && !MatchesController(rule.AllowedControllers, ctx.Controller))
                return Deny(intent, ctx, recognized, "wrongController", $"controller {ctx.Controller} not allowed", rule.AllowedControllers);
        }

        if (intent == PilotIntent.RequestClearance && string.IsNullOrWhiteSpace(ResolveDestination(recognized)))
        {
            const string sa = "{callsign}, say again your destination.";
            return new AtcDecision(true, intent, Fill(sa, ctx, recognized, null, null), "clearance: unknown destination", null);
        }

        string reason = testMode ? "approved (test mode)" : "approved";
        return new AtcDecision(true, intent, Fill(PickApproved(rule), ctx, recognized, null, null), reason, ParsePhase(rule.AdvanceToPhase));
    }

    // Choisit le template d'accord : variante SID si un SID est chargé, sinon le repli « as filed ».
    private string PickApproved(AtcRule rule)
    {
        bool hasSid = !string.IsNullOrWhiteSpace(_plans.Current?.SidName);
        if (!hasSid && rule.ApprovedNoSid is not null) return rule.ApprovedNoSid;
        return rule.Approved;
    }

    private static readonly string[] Hello = { "good day", "hello", "good morning", "good afternoon" };
    private static readonly string[] Bye = { "good day", "so long", "have a good flight", "see you" };

    /// <summary>Salutation en ARRIVANT sur une fréquence : « {indicatif}, {station}, good day. »</summary>
    public string Greeting(FlightContext ctx)
        => Fill("{callsign}, {station}, " + Pick(Hello) + ".", ctx, null, null, null);

    /// <summary>Adieu en QUITTANT une fréquence (dit par le contrôleur qu'on laisse).</summary>
    public string Farewell(FlightContext ctx)
        => Fill("{callsign}, " + Pick(Bye) + ".", ctx, null, null, null);

    private string Pick(string[] options) => options[_rng.Next(options.Length)];

    /// <summary>Réponse « c'est déjà approuvé » (requête déjà accordée), au lieu d'un refus de phase.</summary>
    public string AlreadyApproved(FlightContext ctx)
        => Fill("{callsign}, that is already approved.", ctx, null, null, null);

    /// <summary>Transfert de fréquence : « {callsign}, contactez {contrôleur} sur {fréquence}. ».</summary>
    public string AnnounceTransfer(FlightContext ctx, string controllerName, double freqHz)
    {
        string callsign = _callsigns.Speak(ctx.Callsign);
        // Un transfert se termine TOUJOURS par une formule d'adieu (« …, good day. »).
        string bye = Pick(Bye);

        if (freqHz <= 0) // fréquence inconnue (pas d'OurAirports) -> on transfère sans fréquence
            return "{callsign}, contact {controller}, {bye}."
                .Replace("{callsign}", callsign).Replace("{controller}", controllerName).Replace("{bye}", bye);

        return "{callsign}, contact {controller} on {freq}, {bye}."
            .Replace("{callsign}", callsign)
            .Replace("{controller}", controllerName)
            .Replace("{freq}", FrequencyFormatter.Speak(freqHz))
            .Replace("{bye}", bye);
    }

    /// <summary>Compose une transmission PROACTIVE (clé d'événement -> texte prêt à jouer), ou null.</summary>
    public string? Announce(string eventKey, FlightContext ctx)
    {
        if (!_rules.Events.TryGetValue(eventKey, out var lt)) return null;
        string template = lt.En;
        if (string.IsNullOrWhiteSpace(template)) return null;
        return Fill(template, ctx, null, null, HandoffFrequency(ctx), ArrivalEvents.Contains(eventKey));
    }

    private AtcDecision Deny(PilotIntent intent, FlightContext ctx, RecognizedIntent recognized,
                             string reasonKey, string debug, List<string>? expectedControllers = null)
    {
        string template = _rules.Denials.TryGetValue(reasonKey, out var t) ? t : "{callsign}, unable.";
        return new AtcDecision(false, intent, Fill(template, ctx, recognized, expectedControllers, null), debug, null);
    }

    private AtcRule? FindRule(PilotIntent intent)
    {
        string key = IntentKey(intent);
        return _rules.Rules.FirstOrDefault(r => Norm(r.Intent) == Norm(key));
    }

    /// <summary>Événements ATC qui concernent l'ARRIVÉE : eux seuls citent la piste de destination.</summary>
    private static readonly HashSet<string> ArrivalEvents =
        new(StringComparer.OrdinalIgnoreCase) { "approach", "landing", "taxi_in" };

    /// <summary>
    /// Piste à citer. Par défaut celle du DÉPART : une clairance, un roulage ou un décollage
    /// ne doivent jamais mentionner la piste d'arrivée. Seuls les événements d'arrivée
    /// basculent sur <see cref="FlightPlan.DestinationRunway"/>.
    /// </summary>
    private string RunwayPhrase(bool arrival)
        => RunwayFormatter.Speak(arrival ? _plans.Current?.DestinationRunway : _plans.Current?.OriginRunway);

    private string Fill(string template, FlightContext ctx, RecognizedIntent? recognized,
                        List<string>? expectedControllers, string? freqOverride,
                        bool arrival = false)
    {
        string callsign = _callsigns.Speak(ctx.Callsign);
        string station = StationSpeech.Prettify(ctx.StationName, ctx.AirportIcao);
        string expected = expectedControllers is { Count: > 0 }
            ? SpokenController(expectedControllers[0]) : "the appropriate controller";
        string freq = freqOverride ?? ExpectedFrequency(ctx, expectedControllers);
        string destination = ResolveDestination(recognized) ?? "your destination";

        return template
            .Replace("{callsign}", callsign)
            .Replace("{station}", station)
            .Replace("{expected}", expected)
            .Replace("{freq}", freq)
            .Replace("{destination}", destination)
            .Replace("{sid}", SidFormatter.Speak(_plans.Current?.SidName) ?? "")
            .Replace("{initial_altitude}", FormatAltitude(_settings.Current.DefaultInitialClimbFeet))
            .Replace("{squawk}", RandomSquawk())
            .Replace("{dir}", Dirs[_rng.Next(Dirs.Length)])
            .Replace("{runway}", RunwayPhrase(arrival));
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
        if (feet >= 18000) return $"flight level {feet / 100}";
        return $"{feet} feet";
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
        return "the published frequency";
    }

    private string ExpectedFrequency(FlightContext ctx, List<string>? expectedControllers)
    {
        const string fallback = "the appropriate frequency";
        if (expectedControllers is not { Count: > 0 } || ctx.AirportIcao is null) return fallback;
        if (!Enum.TryParse<ControllerType>(expectedControllers[0], ignoreCase: true, out var type)) return fallback;
        double? hz = _stations.FindFrequencyHz(ctx.AirportIcao, type);
        return hz is null ? fallback : FrequencyFormatter.FormatMHz(hz.Value);
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
