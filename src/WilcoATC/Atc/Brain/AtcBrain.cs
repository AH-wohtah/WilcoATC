using System.Text;
using WilcoATC.Atc;
using WilcoATC.Atc.Context;
using WilcoATC.Atc.Localization;
using WilcoATC.Atc.Planning;
using WilcoATC.Atc.Understanding;
using WilcoATC.Common;
using WilcoATC.Formatting;
using WilcoATC.Settings;
using WilcoATC.Stations;

namespace WilcoATC.Atc.Brain;

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

    /// <summary>
    /// Pistes réellement publiées. Sans elles, l'ATC déduisait un numéro du cap de l'avion
    /// et annonçait des pistes inexistantes (voir <see cref="RunwayPhrase"/>).
    /// </summary>
    private readonly RunwayRepository _runways;
    private readonly Random _rng = new();

    // Les points cardinaux et les sens de tour de piste vivent dans AtcPhrases : ils
    // changent avec la langue du contrôleur, comme le reste de la phraséologie.

    public AtcBrain(AtcRuleSet rules, IStationResolver stations, FlightPlanStore plans,
                    CallsignFormatter callsigns, SettingsService settings, Func<AtcLanguage> language,
                    RunwayRepository runways)
    {
        _runways = runways;
        _rules = rules;
        _stations = stations;
        _plans = plans;
        _callsigns = callsigns;
        _settings = settings;
        _language = language;
    }

    /// <summary>Langue effective courante (pour aligner les libellés côté contrôleur).</summary>
    public AtcLanguage EffectiveLanguage => _language();

    /// <param name="readbackOf">
    /// Intention à l'origine de l'instruction que le pilote vient de collationner, quand
    /// <paramref name="recognized"/> EST ce collationnement. Elle décide de la suite donnée :
    /// après une clairance de départ, le contrôleur ne se contente pas de « readback correct »,
    /// il renvoie au Sol. Null (le cas courant) -> réponse générique.
    /// </param>
    public AtcDecision Evaluate(RecognizedIntent recognized, FlightContext ctx,
                                PilotIntent? readbackOf = null)
    {
        var intent = recognized.Intent;
        var rule = FindRule(intent, ctx.Rules);

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

            // CONTRÔLEUR INADAPTÉ — la garde ne s'applique QUE si elle peut se justifier.
            //
            // Comme la phase, « sur quelle position suis-je ? » est une DÉDUCTION : elle
            // dépend de la résolution fréquence -> station, qui échoue dès que le canal est
            // absent du jeu OurAirports (espacement 8.33, terrain récent…). Refuser sur une
            // déduction ratée, c'est répéter l'erreur que la release du 21 juillet a corrigée
            // pour les phases. Deux conditions cumulatives, donc :
            //
            //  1. on SAIT à qui on parle (position résolue, pas Unknown) ;
            //  2. la position attendue existe VRAIMENT ici, avec une fréquence publiée.
            //
            // La condition 2 compte lourd : 52 % des aéroports à tour ne publient AUCUNE
            // fréquence Sol. Sur ceux-là, c'est la tour qui gère le roulage — répondre
            // « contactez le Sol » désigne un interlocuteur qui n'existe pas.
            if (rule.AllowedControllers.Count > 0 && !MatchesController(rule.AllowedControllers, ctx.Controller))
            {
                double? expectedHz = ExpectedFrequencyHz(ctx, rule.AllowedControllers);

                if (ctx.Controller != ControllerType.Unknown && expectedHz is not null)
                    return Deny(intent, ctx, recognized, "wrongController",
                                $"controller {ctx.Controller} not allowed", rule.AllowedControllers);

                System.Diagnostics.Debug.WriteLine(
                    $"[WilcoATC/Brain] garde contrôleur ignorée ({intent}) : position={ctx.Controller}, " +
                    $"fréquence attendue={(expectedHz is null ? "non publiée" : "connue")} -> on accorde.");
            }
        }

        if (intent == PilotIntent.RequestClearance && string.IsNullOrWhiteSpace(ResolveDestination(recognized)))
        {
            const string sa = "{callsign}, say again your destination.";
            return new AtcDecision(true, intent, Fill(sa, ctx, recognized, null, null), "clearance: unknown destination", null);
        }

        string reason = testMode ? "approved (test mode)" : "approved";
        return new AtcDecision(true, intent, Fill(PickApproved(rule, ctx, recognized, readbackOf), ctx, recognized, null, null), reason, ParsePhase(rule.AdvanceToPhase));
    }

    /// <summary>
    /// L'appareil vient-il de se poser ? <c>Landing</c> couvre la décélération sur la piste,
    /// <c>TaxiIn</c> le roulage qui suit. Ce sont les deux seules phases où une demande de
    /// roulage signifie « je rentre au parking ».
    /// </summary>
    private static bool IsArriving(FlightPhase phase)
        => phase is FlightPhase.Landing or FlightPhase.TaxiIn;

    // Choisit le template d'accord : variante SID si un SID est chargé ET qu'on est à l'aéroport
    // de départ prévu, sinon le repli « as filed » (pas de SID d'un terrain où l'on n'est pas).
    /// <summary>Le pilote a-t-il demandé la mise en route ? (« request startup », « start up »)</summary>
    private static bool AskedForStartup(RecognizedIntent? recognized)
        => recognized?.RawText is { Length: > 0 } t
           && System.Text.RegularExpressions.Regex.IsMatch(
                  t, @"\bstart\s*-?\s*up\b|\bstart\b",
                  System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string PickApproved(AtcRule rule, FlightContext ctx, RecognizedIntent? recognized = null,
                                PilotIntent? readbackOf = null)
    {
        var lang = _language();
        bool atOrigin = SameIcao(_plans.Current?.OriginIcao, ctx.AirportIcao);
        bool hasSid = atOrigin && !string.IsNullOrWhiteSpace(_plans.Current?.SidName);

        // COLLATIONNEMENT D'UNE CLAIRANCE DE DÉPART : la délivrance enchaîne sur le passage
        // au Sol. Deux conditions, et elles comptent toutes les deux.
        //
        //  1. L'APPAREIL EST AU SOL. Une clairance IFR s'obtient aussi EN VOL (départ d'un
        //     terrain non contrôlé, puis reprise du plan) : proposer un repoussage à un avion
        //     déjà en l'air n'aurait aucun sens.
        //  2. LE TERRAIN PUBLIE UNE FRÉQUENCE SOL — sinon on renverrait le pilote vers un
        //     interlocuteur qui n'existe pas (52 % des terrains à tour n'ont pas de Sol,
        //     c'est la tour qui gère le roulage).
        if (readbackOf == PilotIntent.RequestClearance && ctx.OnGround && HasGroundFrequency(ctx)
            && rule.ApprovedAfterClearanceFor(lang) is { Length: > 0 } handoff)
            return handoff;

        // MISE EN ROUTE DEMANDÉE : on l'accorde avec la clairance, comme le fait une
        // délivrance européenne. Sinon le pilote n'obtient que la moitié de sa demande.
        if (AskedForStartup(recognized))
        {
            // Sans SID chargé, la variante « {sid} departure » n'a rien à citer : on annonce
            // la clairance selon le plan déposé plutôt qu'un départ vide.
            if (!hasSid && rule.ApprovedStartupNoSidFor(lang) is { Length: > 0 } startupNoSid)
                return startupNoSid;
            if (rule.ApprovedStartupFor(lang) is { Length: > 0 } startup)
                return startup;
        }

        // L'APPAREIL ARRIVE-T-IL ? Une demande de roulage après l'atterrissage veut dire
        // « je rentre au parking », pas « je vais décoller ». Sans cette variante, un pilote
        // qui venait de dégager la piste et demandait le roulage se voyait attribuer un
        // départ : direction le point d'attente, pour redécoller.
        if (IsArriving(ctx.Phase) && rule.ApprovedArrivingFor(lang) is { Length: > 0 } arriving)
            return arriving;

        if (!hasSid && rule.ApprovedNoSidFor(lang) is { Length: > 0 } noSid) return noSid;
        return rule.ApprovedFor(lang);
    }

    /// <summary>Le terrain courant publie-t-il une fréquence Sol ?</summary>
    private bool HasGroundFrequency(FlightContext ctx)
        => ctx.AirportIcao is { Length: > 0 } icao
           && _stations.FindFrequencyHz(icao, ControllerType.Ground) is not null;

    /// <summary>Salutation en ARRIVANT sur une fréquence : « {indicatif}, {station}, bonjour. »</summary>
    public string Greeting(FlightContext ctx)
        => Fill("{callsign}, {station}, " + Pick(AtcPhrases.Hello(_language())) + ".", ctx, null, null, null);

    /// <summary>Adieu en QUITTANT une fréquence (dit par le contrôleur qu'on laisse).</summary>
    public string Farewell(FlightContext ctx)
        => Fill("{callsign}, " + Pick(AtcPhrases.Bye(_language())) + ".", ctx, null, null, null);

    private string Pick(string[] options) => options[_rng.Next(options.Length)];

    /// <summary>Réponse « c'est déjà approuvé » (requête déjà accordée), au lieu d'un refus de phase.</summary>
    public string AlreadyApproved(FlightContext ctx)
        => Fill(AtcPhrases.AlreadyApproved(_language()), ctx, null, null, null);

    /// <summary>
    /// Transfert de fréquence : « {callsign}, contactez {contrôleur} sur {fréquence}. ».
    ///
    /// La fréquence est OBLIGATOIRE. Sans elle l'instruction est inapplicable — le pilote
    /// ne peut pas « contacter » un numéro qu'on ne lui donne pas. L'appelant doit avoir
    /// basculé sur <see cref="AnnounceRemainThisFrequency"/> ; ce garde-fou est la ceinture
    /// et les bretelles, pour qu'un futur appelant ne réintroduise pas le défaut en silence.
    /// </summary>
    public string AnnounceTransfer(FlightContext ctx, string controllerName, double freqHz)
    {
        if (freqHz <= 0) return AnnounceRemainThisFrequency(ctx);

        var lang = _language();
        string callsign = _callsigns.Speak(ctx.Callsign);
        // Un transfert se termine TOUJOURS par une formule d'adieu (« …, bonne journée. »).
        string bye = Pick(AtcPhrases.Bye(lang));

        return AtcPhrases.Transfer(lang)
            .Replace("{callsign}", callsign)
            .Replace("{controller}", controllerName)
            .Replace("{freq}", FrequencyFormatter.Speak(freqHz))
            .Replace("{bye}", bye);
    }

    /// <summary>
    /// Aucune fréquence suivante connue : on garde le pilote où il est. C'est de la vraie
    /// phraséologie (« remain this frequency »), et surtout c'est APPLICABLE — contrairement
    /// à un « contactez X » sans numéro, qui laisse le pilote sans rien à composer.
    /// </summary>
    public string AnnounceRemainThisFrequency(FlightContext ctx)
        => Fill(Pick(AtcPhrases.RemainThisFrequency(_language())), ctx, null, null, null);

    /// <summary>
    /// Compose une transmission PROACTIVE (clé d'événement -> texte prêt à jouer), ou null.
    /// En VFR on cherche d'abord une variante « clé.vfr » : quand elle existe elle gagne,
    /// sinon on retombe sur la formulation commune. Ça évite de dupliquer toute la table
    /// pour les événements dont le texte convient aux deux régimes.
    /// </summary>
    public string? Announce(string eventKey, FlightContext ctx)
    {
        string template = EventTemplate(eventKey, ctx.Rules);
        if (string.IsNullOrWhiteSpace(template)) return null;

        string? freq = HandoffFrequency(ctx);

        // Un événement qui cite une fréquence qu'on ne connaît pas ne doit PAS être joué :
        // « contact departure on . » est pire que le silence. Même règle que pour les
        // transferts — une instruction sans numéro n'est pas exécutable.
        if (template.Contains("{freq}") && freq is null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[WilcoATC/Brain] événement « {eventKey} » ignoré : aucune fréquence à annoncer.");
            return null;
        }

        return Fill(template, ctx, null, null, freq, ArrivalEvents.Contains(eventKey));
    }

    private string EventTemplate(string eventKey, FlightRules rules)
    {
        var lang = _language();

        if (rules == FlightRules.Vfr &&
            _rules.Events.TryGetValue(eventKey + ".vfr", out var vfr) &&
            !string.IsNullOrWhiteSpace(vfr.For(lang)))
            return vfr.For(lang);

        return _rules.Events.TryGetValue(eventKey, out var lt) ? lt.For(lang) : "";
    }

    /// <summary>
    /// VFR : la tour ne TRANSFÈRE pas quand on quitte la zone, elle LIBÈRE. Il n'y a aucun
    /// contrôleur suivant à annoncer — dire « contactez… » sans destinataire n'aurait aucun sens.
    /// </summary>
    public string AnnounceLeavingZone(FlightContext ctx)
        => Fill(AtcPhrases.LeavingZone(_language()).Replace("{bye}", Pick(AtcPhrases.Bye(_language()))),
                ctx, null, null, null);

    private AtcDecision Deny(PilotIntent intent, FlightContext ctx, RecognizedIntent recognized,
                             string reasonKey, string debug, List<string>? expectedControllers = null)
    {
        string template = _rules.Denial(reasonKey, _language()) ?? AtcPhrases.UnableFallback(_language());
        return new AtcDecision(false, intent, Fill(template, ctx, recognized, expectedControllers, null), debug, null);
    }

    /// <summary>
    /// Règle applicable à une intention. Une règle SPÉCIFIQUE aux règles de vol courantes
    /// l'emporte sur une règle générique : c'est ce qui fait qu'une demande de clairance
    /// donne un SID en IFR et une sortie de zone en VFR, sans dupliquer toute la table.
    /// </summary>
    private AtcRule? FindRule(PilotIntent intent, FlightRules rules)
    {
        string key = Norm(IntentKey(intent));
        var candidates = _rules.Rules.Where(r => Norm(r.Intent) == key).ToList();

        string wanted = rules == FlightRules.Vfr ? "vfr" : "ifr";
        return candidates.FirstOrDefault(r => Norm(r.FlightRules ?? "") == wanted)
            ?? candidates.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.FlightRules));
    }

    /// <summary>Événements ATC qui concernent l'ARRIVÉE : eux seuls citent la piste de destination.</summary>
    private static readonly HashSet<string> ArrivalEvents =
        new(StringComparer.OrdinalIgnoreCase) { "approach", "landing", "taxi_in" };

    /// <summary>
    /// Piste à citer. On ne prend la piste du PLAN que si l'avion est réellement à l'aéroport
    /// prévu (départ, ou arrivée). Sinon — cas typique : on décolle d'un tout autre terrain que
    /// celui du plan de vol — on nomme la piste RÉELLE, déduite du cap de l'avion (celle sur
    /// laquelle il est aligné), plutôt qu'une piste d'un aéroport où l'on n'est pas.
    /// </summary>
    private string RunwayPhrase(FlightContext ctx, bool arrival)
    {
        var plan = _plans.Current;
        string? plannedRunway = arrival ? plan?.DestinationRunway : plan?.OriginRunway;
        string? plannedIcao = arrival ? plan?.DestinationIcao : plan?.OriginIcao;
        bool atPlanAirport = SameIcao(plannedIcao, ctx.AirportIcao);

        var lang = _language();

        // 1. La piste du PLAN DE VOL, si on est bien à l'aéroport prévu ET qu'elle existe
        //    réellement là-bas. La vérification compte : un OFP peut citer une piste rénumérotée
        //    depuis (dérive magnétique) ou tout simplement fausse.
        if (atPlanAirport && !string.IsNullOrWhiteSpace(plannedRunway)
            && _runways.Exists(ctx.AirportIcao, plannedRunway))
            return RunwayFormatter.Speak(plannedRunway, lang);

        // 2. La piste EN SERVICE parmi celles qui existent vraiment : face au vent, sinon
        //    dans l'axe de l'avion, sinon la principale. Plus aucun numéro inventé.
        var active = _runways.Active(ctx.AirportIcao, ctx.WindFromDeg, ctx.WindKnots, ctx.HeadingDeg);
        if (active is not null) return RunwayFormatter.Speak(active.Ident, lang);

        // 3. Terrain sans piste publiée : on le dit, au lieu de nommer un numéro au hasard.
        return RunwayFormatter.Speak(null, lang);
    }

    /// <summary>L'avion est-il réellement à l'aéroport prévu par le plan (départ ou arrivée) ?</summary>
    private static bool SameIcao(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a!.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private string Fill(string template, FlightContext ctx, RecognizedIntent? recognized,
                        List<string>? expectedControllers, string? freqOverride,
                        bool arrival = false)
    {
        var lang = _language();
        string callsign = _callsigns.Speak(ctx.Callsign);
        string station = StationSpeech.Prettify(ctx.StationName, ctx.AirportIcao, lang);
        string expected = expectedControllers is { Count: > 0 }
            ? SpokenController(expectedControllers[0], lang) : AtcPhrases.AppropriateController(lang);
        string freq = freqOverride ?? ExpectedFrequency(ctx, expectedControllers);
        string destination = ResolveDestination(recognized) ?? AtcPhrases.YourDestination(lang);

        // Le SID n'est valable qu'au DÉPART DE L'AÉROPORT PRÉVU : hors de là, on ne cite pas
        // une procédure de départ d'un terrain où l'on n'est pas.
        bool atOrigin = SameIcao(_plans.Current?.OriginIcao, ctx.AirportIcao);
        string sid = atOrigin ? (SidFormatter.Speak(_plans.Current?.SidName) ?? "") : "";

        return template
            .Replace("{callsign}", callsign)
            .Replace("{station}", station)
            .Replace("{expected}", expected)
            .Replace("{freq}", freq)
            .Replace("{destination}", destination)
            .Replace("{sid}", sid)
            .Replace("{initial_altitude}", FormatAltitude(_settings.Current.DefaultInitialClimbFeet))
            .Replace("{initial_level}", AtcPhrases.Level(lang, _settings.Current.DefaultInitialClimbFeet / 100))
            .Replace("{ground_station}", AtcPhrases.GroundStation(lang, AirportSpokenName(ctx)))
            .Replace("{squawk}", RandomSquawk())
            .Replace("{dir}", Pick(AtcPhrases.Directions(lang)))
            .Replace("{circuit}", Pick(AtcPhrases.Circuits(lang)))
            .Replace("{runway}", RunwayPhrase(ctx, arrival))
            .Replace("{ground_freq}", GroundFreqSuffix(ctx))
            .Replace("{altitude_clearance}", AltitudeClearance(recognized, lang));
    }

    /// <summary>
    /// Réponse à une demande de changement d'altitude. La cible et la direction sont
    /// (re)lues depuis le message brut du pilote via <see cref="AltitudeParser"/> : même
    /// source de vérité pour la voie grammaire ET la voie LLM (toutes deux fournissent le
    /// texte brut). Sans chiffre — « request higher » — on répond « at your discretion ».
    /// </summary>
    private static string AltitudeClearance(RecognizedIntent? recognized, AtcLanguage lang)
    {
        var r = AltitudeParser.Parse(recognized?.RawText ?? "");

        if (r.Feet is null) return AtcPhrases.AltitudeDiscretion(lang, r.Climb);

        string alt = r.AsFlightLevel
            ? AtcPhrases.FlightLevel(lang, r.Feet.Value / 100)
            : AtcPhrases.Feet(lang, r.Feet.Value);

        return $"{AtcPhrases.AltitudeVerb(lang, r.Climb)} {alt}";
    }

    /// <summary>
    /// Nom PARLÉ du terrain seul (« Brussels »), sans la position de contrôle ni les mots
    /// passe-partout du fichier de données (« International », « Airport »). C'est le nom
    /// qu'on accole à une position qu'on cite : « Brussels Ground ».
    /// </summary>
    private string AirportSpokenName(FlightContext ctx)
    {
        string? name = ctx.StationName;

        // Certains résolveurs renvoient « Terrain · GND » : la position, on la redit nous-mêmes.
        if (!string.IsNullOrWhiteSpace(name))
        {
            int cut = name!.IndexOf('·');
            if (cut > 0) name = name[..cut];
        }

        if (string.IsNullOrWhiteSpace(name) && ctx.AirportIcao is { Length: > 0 } icao)
            name = _stations.LookupAirportName(icao);

        return FlightPlan.CleanAirportName(name) ?? ctx.AirportIcao ?? "";
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
        var lang = _language();
        return feet >= 18000
            ? AtcPhrases.FlightLevel(lang, feet / 100)
            : AtcPhrases.Feet(lang, feet);
    }

    private string RandomSquawk()
    {
        int code;
        do { code = _rng.Next(0, 8) * 1000 + _rng.Next(0, 8) * 100 + _rng.Next(0, 8) * 10 + _rng.Next(0, 8); }
        while (code is 0 or 7000 or 7500 or 7600 or 7700);
        return code.ToString("D4");
    }

    // Fréquence de transfert : départ / approche / centre de l'aéroport (OurAirports).
    // Renvoie null si rien n'est publié — l'appelant renonce alors à l'annonce plutôt que
    // de citer « the published frequency », qui ne désigne aucun numéro.
    /// <summary>
    /// Suffixe « on {fréquence Sol} » pour l'arrivée (« contact ground »), ou vide si l'aéroport
    /// ne publie pas de fréquence Sol. On ne cite QUE la vraie fréquence Sol (pas de repli Tour :
    /// ce serait donner un numéro de Tour en disant « ground »). Corrige le cas où l'ATC disait
    /// « contact ground » sans numéro alors que la fréquence existe et est affichée.
    /// </summary>
    private string GroundFreqSuffix(FlightContext ctx)
    {
        if (ctx.AirportIcao is null) return "";
        double? hz = _stations.FindFrequencyHz(ctx.AirportIcao, ControllerType.Ground);
        return hz is null ? "" : AtcPhrases.OnFrequency(_language(), FrequencyFormatter.Speak(hz.Value));
    }

    private string? HandoffFrequency(FlightContext ctx)
    {
        if (ctx.AirportIcao is null) return null;

        foreach (var type in new[] { ControllerType.Departure, ControllerType.Approach, ControllerType.Center })
        {
            double? hz = _stations.FindFrequencyHz(ctx.AirportIcao, type);
            if (hz is not null) return FrequencyFormatter.Speak(hz.Value);
        }
        return null;
    }

    /// <summary>
    /// Fréquence PUBLIÉE de la première position attendue, ou null. C'est elle qui décide si
    /// un refus « mauvais contrôleur » est seulement légitime : sans numéro à donner, le
    /// refus n'est pas actionnable et la garde est levée (voir <see cref="Evaluate"/>).
    /// </summary>
    private double? ExpectedFrequencyHz(FlightContext ctx, List<string>? expectedControllers)
    {
        if (expectedControllers is not { Count: > 0 } || ctx.AirportIcao is null) return null;

        // On essaie TOUTES les positions autorisées, pas seulement la première : une règle
        // qui accepte « Ground » ou « Clearance » est satisfaite par l'une ou l'autre.
        foreach (var name in expectedControllers)
        {
            if (!Enum.TryParse<ControllerType>(name, ignoreCase: true, out var type)) continue;
            double? hz = _stations.FindFrequencyHz(ctx.AirportIcao, type);
            if (hz is not null) return hz;
        }
        return null;
    }

    /// <summary>
    /// Fréquence à citer dans un refus. Un refus n'est produit que lorsqu'elle EXISTE
    /// (cf. <see cref="Evaluate"/>) : le repli textuel « the appropriate frequency » a été
    /// supprimé, il produisait des instructions inapplicables du type « contact Ground on
    /// the appropriate frequency ».
    /// </summary>
    private string ExpectedFrequency(FlightContext ctx, List<string>? expectedControllers)
    {
        double? hz = ExpectedFrequencyHz(ctx, expectedControllers);
        return hz is null ? "" : FrequencyFormatter.Speak(hz.Value);
    }

    private static string SpokenController(string c, AtcLanguage lang)
        => Enum.TryParse<ControllerType>(c, ignoreCase: true, out var type)
            ? AtcPhrases.Controller(lang, type)
            : c;

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
        PilotIntent.RequestAltitude => "REQUEST_ALTITUDE",
        PilotIntent.CheckIn => "CHECK_IN",
        PilotIntent.ReportApproach => "REPORT_APPROACH",
        PilotIntent.ReportFinal => "REPORT_FINAL",
        PilotIntent.DeclareMayday => "DECLARE_MAYDAY",
        PilotIntent.DeclarePanPan => "DECLARE_PAN_PAN",
        PilotIntent.CancelEmergency => "CANCEL_EMERGENCY",
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
