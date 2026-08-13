using WilcoATC.Atc.Context;
using WilcoATC.Atc.Localization;

namespace WilcoATC.Atc;

/// <summary>
/// Générateur ATC par défaut : déterministe, gratuit, hors-ligne. Choisit un
/// template réaliste selon le contexte (sol/vol, type de déclencheur) et remplit
/// les champs {callsign}, {station}, {alt}, {qnh}, dans la langue du contrôleur.
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
        var lang = _language();
        string callsign = string.IsNullOrWhiteSpace(f.Callsign) ? Aircraft(lang) : f.Callsign;
        string station = StationSpeech.Prettify(f.Station, f.NearestAirportIcao, lang);

        string[] pool = Pool(lang, trigger, f.OnGround, f.Rules);
        string template = pool[_rng.Next(pool.Length)];

        return template
            .Replace("{callsign}", callsign)
            .Replace("{station}", station)
            .Replace("{alt}", SpeakAltitude(lang, f.AltitudeMslFeet, f.Rules))
            .Replace("{qnh}", "1013");
    }

    /// <summary>Interpellation d'un appareil dont on ignore l'indicatif.</summary>
    private static string Aircraft(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "Appareil",
        AtcLanguage.German => "Luftfahrzeug",
        AtcLanguage.Spanish => "Aeronave",
        AtcLanguage.Italian => "Aeromobile",
        _ => "Aircraft",
    };

    private static string[] Pool(AtcLanguage lang, AtcTrigger trigger, bool onGround, FlightRules rules)
    {
        if (trigger == AtcTrigger.ManualTest)
            return lang switch
            {
                AtcLanguage.French => new[]
                {
                    "{callsign}, {station}, essai radio, je vous reçois cinq sur cinq.",
                    "{callsign}, {station}, fort et clair, comment me recevez-vous ?",
                },
                AtcLanguage.German => new[]
                {
                    "{callsign}, {station}, Funkprobe, ich höre Sie laut und deutlich.",
                    "{callsign}, {station}, laut und deutlich, wie hören Sie mich?",
                },
                AtcLanguage.Spanish => new[]
                {
                    "{callsign}, {station}, prueba de radio, le recibo cinco sobre cinco.",
                    "{callsign}, {station}, fuerte y claro, ¿cómo me recibe?",
                },
                AtcLanguage.Italian => new[]
                {
                    "{callsign}, {station}, prova radio, la ricevo cinque su cinque.",
                    "{callsign}, {station}, forte e chiaro, come mi riceve?",
                },
                _ => new[]
                {
                    "{callsign}, {station}, radio check, reading you five by five.",
                    "{callsign}, {station}, loud and clear, how do you read?",
                },
            };

        if (lang != AtcLanguage.English) return LocalPool(lang, onGround, rules);

        // Les deux mondes ne partagent AUCUNE phrase. Avant, le pool en vol mélangeait
        // « radar contact » et « maintain VFR » et tirait au hasard : un airbus pouvait
        // s'entendre dire de rester en VFR, et un Cessna recevoir un service radar.
        return (rules, onGround) switch
        {
            (FlightRules.Vfr, true) => new[]
            {
                "{callsign}, {station}, good day, taxi to holding point via alpha, QNH {qnh}.",
                "{callsign}, {station}, hold position, one aircraft on final.",
                "{callsign}, {station}, report ready for departure.",
            },
            (FlightRules.Vfr, false) => new[]
            {
                "{callsign}, {station}, good day, report field in sight.",
                "{callsign}, {station}, join right hand downwind runway in use, QNH {qnh}.",
                "{callsign}, {station}, remain clear of controlled airspace, report your intentions.",
                "{callsign}, {station}, roger, maintain VFR, {alt}.",
            },
            (_, true) => new[]
            {
                "{callsign}, {station}, good day, taxi to holding point via alpha, QNH {qnh}.",
                "{callsign}, {station}, hold position, expect departure in sequence.",
                "{callsign}, {station}, radar contact on the ground, standby for clearance.",
            },
            _ => new[]
            {
                "{callsign}, {station}, radar contact, {alt}, altimeter two niner niner two.",
                "{callsign}, {station}, good day, radar identified, climb as cleared.",
                "{callsign}, {station}, roger, maintain {alt}, expect further climb shortly.",
            },
        };
    }

    /// <summary>
    /// Contreparties locales des pools ci-dessus. Même découpage (VFR/IFR × sol/vol) : ce
    /// qui n'a aucun sens en anglais n'en a pas davantage en français.
    /// </summary>
    private static string[] LocalPool(AtcLanguage lang, bool onGround, FlightRules rules) => (lang, rules, onGround) switch
    {
        // ---------------------------------------------------------------- français
        (AtcLanguage.French, FlightRules.Vfr, true) => new[]
        {
            "{callsign}, {station}, bonjour, roulez au point d'attente par alpha, QNH {qnh}.",
            "{callsign}, {station}, maintenez position, un appareil en finale.",
            "{callsign}, {station}, rappelez prêt au départ.",
        },
        (AtcLanguage.French, FlightRules.Vfr, false) => new[]
        {
            "{callsign}, {station}, bonjour, rappelez terrain en vue.",
            "{callsign}, {station}, intégrez la vent arrière main droite, QNH {qnh}.",
            "{callsign}, {station}, restez en dehors de l'espace contrôlé, rappelez vos intentions.",
            "{callsign}, {station}, reçu, maintenez VFR, {alt}.",
        },
        (AtcLanguage.French, _, true) => new[]
        {
            "{callsign}, {station}, bonjour, roulez au point d'attente par alpha, QNH {qnh}.",
            "{callsign}, {station}, maintenez position, départ dans la séquence.",
            "{callsign}, {station}, identifié au sol, attendez pour la clairance.",
        },
        (AtcLanguage.French, _, false) => new[]
        {
            "{callsign}, {station}, contact radar, {alt}.",
            "{callsign}, {station}, bonjour, identifié radar, poursuivez la montée.",
            "{callsign}, {station}, reçu, maintenez {alt}, montée ultérieure dans un instant.",
        },

        // ---------------------------------------------------------------- allemand
        (AtcLanguage.German, FlightRules.Vfr, true) => new[]
        {
            "{callsign}, {station}, guten Tag, rollen Sie zum Rollhalt über Alpha, QNH {qnh}.",
            "{callsign}, {station}, Position halten, ein Luftfahrzeug im Endteil.",
            "{callsign}, {station}, melden Sie startbereit.",
        },
        (AtcLanguage.German, FlightRules.Vfr, false) => new[]
        {
            "{callsign}, {station}, guten Tag, melden Sie Platz in Sicht.",
            "{callsign}, {station}, rechte Platzrunde für die aktive Piste, QNH {qnh}.",
            "{callsign}, {station}, bleiben Sie außerhalb des kontrollierten Luftraums, melden Sie Ihre Absichten.",
            "{callsign}, {station}, verstanden, halten Sie VFR, {alt}.",
        },
        (AtcLanguage.German, _, true) => new[]
        {
            "{callsign}, {station}, guten Tag, rollen Sie zum Rollhalt über Alpha, QNH {qnh}.",
            "{callsign}, {station}, Position halten, Abflug in der Reihenfolge.",
            "{callsign}, {station}, am Boden identifiziert, warten Sie auf die Freigabe.",
        },
        (AtcLanguage.German, _, false) => new[]
        {
            "{callsign}, {station}, Radarkontakt, {alt}.",
            "{callsign}, {station}, guten Tag, radaridentifiziert, setzen Sie den Steigflug fort.",
            "{callsign}, {station}, verstanden, halten Sie {alt}, weiterer Steigflug in Kürze.",
        },

        // ---------------------------------------------------------------- espagnol
        (AtcLanguage.Spanish, FlightRules.Vfr, true) => new[]
        {
            "{callsign}, {station}, buenos días, ruede al punto de espera por alfa, QNH {qnh}.",
            "{callsign}, {station}, mantenga posición, una aeronave en final.",
            "{callsign}, {station}, notifique listo para salida.",
        },
        (AtcLanguage.Spanish, FlightRules.Vfr, false) => new[]
        {
            "{callsign}, {station}, buenos días, notifique campo a la vista.",
            "{callsign}, {station}, incorpórese al viento en cola por la derecha, QNH {qnh}.",
            "{callsign}, {station}, permanezca fuera del espacio aéreo controlado, notifique intenciones.",
            "{callsign}, {station}, recibido, mantenga VFR, {alt}.",
        },
        (AtcLanguage.Spanish, _, true) => new[]
        {
            "{callsign}, {station}, buenos días, ruede al punto de espera por alfa, QNH {qnh}.",
            "{callsign}, {station}, mantenga posición, salida en secuencia.",
            "{callsign}, {station}, identificado en tierra, espere la autorización.",
        },
        (AtcLanguage.Spanish, _, false) => new[]
        {
            "{callsign}, {station}, contacto radar, {alt}.",
            "{callsign}, {station}, buenos días, identificado radar, continúe el ascenso.",
            "{callsign}, {station}, recibido, mantenga {alt}, ascenso posterior en breve.",
        },

        // ---------------------------------------------------------------- italien
        (AtcLanguage.Italian, FlightRules.Vfr, true) => new[]
        {
            "{callsign}, {station}, buongiorno, rulli al punto attesa via alfa, QNH {qnh}.",
            "{callsign}, {station}, mantenga la posizione, un aeromobile in finale.",
            "{callsign}, {station}, riporti pronto al decollo.",
        },
        (AtcLanguage.Italian, FlightRules.Vfr, false) => new[]
        {
            "{callsign}, {station}, buongiorno, riporti campo in vista.",
            "{callsign}, {station}, si inserisca in sottovento destro, QNH {qnh}.",
            "{callsign}, {station}, rimanga fuori dallo spazio aereo controllato, riporti le intenzioni.",
            "{callsign}, {station}, ricevuto, mantenga VFR, {alt}.",
        },
        (AtcLanguage.Italian, _, true) => new[]
        {
            "{callsign}, {station}, buongiorno, rulli al punto attesa via alfa, QNH {qnh}.",
            "{callsign}, {station}, mantenga la posizione, partenza in sequenza.",
            "{callsign}, {station}, identificato al suolo, attenda l'autorizzazione.",
        },
        (AtcLanguage.Italian, _, false) => new[]
        {
            "{callsign}, {station}, contatto radar, {alt}.",
            "{callsign}, {station}, buongiorno, identificato radar, prosegua la salita.",
            "{callsign}, {station}, ricevuto, mantenga {alt}, ulteriore salita a breve.",
        },

        _ => Pool(AtcLanguage.English, AtcTrigger.InitialContact, onGround, rules),
    };

    /// <summary>
    /// Altitude parlée. Le niveau de vol n'a de sens qu'aux instruments : en VFR on reste
    /// en pieds, quelle que soit l'altitude (le pilote lit son altimètre, pas un FL).
    /// </summary>
    private static string SpeakAltitude(AtcLanguage lang, double feet, FlightRules rules)
    {
        if (rules == FlightRules.Ifr && feet >= 18000)
            return AtcPhrases.FlightLevel(lang, (int)Math.Round(feet / 100.0));

        return AtcPhrases.Feet(lang, (long)(Math.Round(feet / 100.0) * 100));
    }
}
