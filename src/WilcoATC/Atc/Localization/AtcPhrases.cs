using WilcoATC.Common;

namespace WilcoATC.Atc.Localization;

/// <summary>
/// Phrases FIXES du contrôleur, par langue : celles qui ne vivent pas dans
/// <c>atc-rules.json</c> parce qu'elles sont assemblées en code (salutations, unités
/// d'altitude, nom des positions de contrôle, mots de piste…).
///
/// PHRASÉOLOGIE, PAS TRADUCTION MOT À MOT. Chaque langue emploie ce que ses contrôleurs
/// disent réellement : « autorisé au décollage » et non « effacé pour le décollage »,
/// « Rollhalt » et non « point d'attente ». Les termes qui restent en anglais dans la
/// vraie vie le restent ici (squawk, QNH, roger, wilco).
///
/// NOMBRES : jamais épelés en dur. Les chiffres partent tels quels vers la synthèse, qui
/// les lit dans SA langue — la voix française dit « cent dix-huit virgule sept » pour
/// « 118.7 » sans qu'on ait à l'écrire. C'est ce qui rend l'ajout d'une langue léger.
/// </summary>
public static class AtcPhrases
{
    // ------------------------------------------------------------------ salutations

    /// <summary>Salutations d'arrivée sur fréquence, tirées au sort.</summary>
    public static string[] Hello(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => new[] { "bonjour", "bonjour à vous" },
        AtcLanguage.German => new[] { "guten Tag", "grüß Gott" },
        AtcLanguage.Spanish => new[] { "buenos días", "muy buenos días" },
        AtcLanguage.Italian => new[] { "buongiorno", "buongiorno a lei" },
        _ => new[] { "good day", "good morning", "hello" },
    };

    /// <summary>Formules d'adieu, employées à chaque transfert de fréquence.</summary>
    public static string[] Bye(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => new[] { "bonne journée", "au revoir", "bon vol" },
        AtcLanguage.German => new[] { "schönen Tag", "auf Wiederhören", "guten Flug" },
        AtcLanguage.Spanish => new[] { "buen día", "hasta luego", "buen vuelo" },
        AtcLanguage.Italian => new[] { "buona giornata", "arrivederci", "buon volo" },
        _ => new[] { "good day", "so long", "have a good flight" },
    };

    // ------------------------------------------------------------------ gabarits assemblés en code

    /// <summary>Transfert : « {callsign}, contactez {controller} sur {freq}, {bye}. »</summary>
    public static string Transfer(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, contactez {controller} sur {freq}, {bye}.",
        AtcLanguage.German => "{callsign}, kontaktieren Sie {controller} auf {freq}, {bye}.",
        AtcLanguage.Spanish => "{callsign}, contacte {controller} en {freq}, {bye}.",
        AtcLanguage.Italian => "{callsign}, contatti {controller} su {freq}, {bye}.",
        _ => "{callsign}, contact {controller} on {freq}, {bye}.",
    };

    /// <summary>Aucune fréquence suivante connue : on garde le pilote sur place.</summary>
    public static string[] RemainThisFrequency(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => new[]
        {
            "{callsign}, pas de changement de fréquence, restez sur cette fréquence.",
            "{callsign}, restez sur cette fréquence, {station}.",
            "{callsign}, aucune fréquence suivante publiée, restez avec moi.",
        },
        AtcLanguage.German => new[]
        {
            "{callsign}, kein Frequenzwechsel erforderlich, bleiben Sie auf dieser Frequenz.",
            "{callsign}, bleiben Sie auf dieser Frequenz, {station}.",
            "{callsign}, keine Folgefrequenz veröffentlicht, bleiben Sie bei mir.",
        },
        AtcLanguage.Spanish => new[]
        {
            "{callsign}, sin cambio de frecuencia, mantenga esta frecuencia.",
            "{callsign}, mantenga esta frecuencia, {station}.",
            "{callsign}, no hay frecuencia siguiente publicada, siga conmigo.",
        },
        AtcLanguage.Italian => new[]
        {
            "{callsign}, nessun cambio di frequenza, mantenga questa frequenza.",
            "{callsign}, mantenga questa frequenza, {station}.",
            "{callsign}, nessuna frequenza successiva pubblicata, resti con me.",
        },
        _ => new[]
        {
            "{callsign}, no further frequency change required, remain this frequency.",
            "{callsign}, remain this frequency, {station}.",
            "{callsign}, no onward frequency published, stay with me.",
        },
    };

    /// <summary>VFR : la tour libère au lieu de transférer quand on quitte la zone.</summary>
    public static string LeavingZone(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, sortie de zone, squawk VFR, changement de fréquence approuvé, {bye}.",
        AtcLanguage.German => "{callsign}, verlassen der Kontrollzone, squawk VFR, Frequenzwechsel genehmigt, {bye}.",
        AtcLanguage.Spanish => "{callsign}, abandonando la zona, squawk VFR, cambio de frecuencia aprobado, {bye}.",
        AtcLanguage.Italian => "{callsign}, in uscita dalla zona, squawk VFR, cambio frequenza approvato, {bye}.",
        _ => "{callsign}, leaving the zone, squawk VFR, frequency change approved, {bye}.",
    };

    /// <summary>Requête déjà accordée.</summary>
    public static string AlreadyApproved(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, c'est déjà approuvé.",
        AtcLanguage.German => "{callsign}, das ist bereits genehmigt.",
        AtcLanguage.Spanish => "{callsign}, ya está aprobado.",
        AtcLanguage.Italian => "{callsign}, è già approvato.",
        _ => "{callsign}, that is already approved.",
    };

    /// <summary>Refus générique de dernier recours (table de refus incomplète).</summary>
    public static string UnableFallback(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, négatif.",
        AtcLanguage.German => "{callsign}, negativ.",
        AtcLanguage.Spanish => "{callsign}, negativo.",
        AtcLanguage.Italian => "{callsign}, negativo.",
        _ => "{callsign}, unable.",
    };

    // ------------------------------------------------------------------ collationnement

    /// <summary>Relance quand le pilote n'a rien collationné : « {callsign}, collationnez. »</summary>
    public static string ReadbackRequest(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, collationnez.",
        AtcLanguage.German => "{callsign}, lesen Sie zurück.",
        AtcLanguage.Spanish => "{callsign}, colacione.",
        AtcLanguage.Italian => "{callsign}, riporti.",
        _ => "{callsign}, read back.",
    };

    /// <summary>
    /// Deuxième relance : on REDIT l'instruction en entier. Un pilote qui ne collationne pas
    /// ne l'a le plus souvent pas comprise — la répéter vaut mieux que la réclamer.
    /// </summary>
    public static string SayAgain(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, je répète : {instruction}",
        AtcLanguage.German => "{callsign}, ich wiederhole: {instruction}",
        AtcLanguage.Spanish => "{callsign}, repito: {instruction}",
        AtcLanguage.Italian => "{callsign}, ripeto: {instruction}",
        _ => "{callsign}, I say again: {instruction}",
    };

    /// <summary>Troisième et dernier appel, sec : après, c'est la défense aérienne.</summary>
    public static string ReadbackLastCall(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, répondez immédiatement, dernier appel.",
        AtcLanguage.German => "{callsign}, antworten Sie sofort, letzter Aufruf.",
        AtcLanguage.Spanish => "{callsign}, responda inmediatamente, última llamada.",
        AtcLanguage.Italian => "{callsign}, risponda immediatamente, ultima chiamata.",
        _ => "{callsign}, respond immediately, last call.",
    };

    /// <summary>
    /// DERNIÈRE TRANSMISSION DU CONTRÔLEUR. Après elle, il se tait définitivement : plus de
    /// clairances, plus de transferts, plus rien — seule l'armée parle.
    ///
    /// Ce n'est pas une figure de style, c'est la procédure. Un contrôleur qui a conclu à la
    /// perte de communication et fait décoller une patrouille ne continue pas à donner des
    /// instructions dans le vide ; il passe la main. Et pour le pilote, la rupture doit
    /// s'entendre : tant que l'ATC continuait de parler comme si de rien n'était, la menace
    /// n'en était pas une.
    /// </summary>
    public static string NoRadioAlert(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French =>
            "{callsign}, nous n'avons pas reçu votre collationnement. Nous ne parvenons pas à "
            + "établir le contact radio avec vous. Les autorités militaires ont été prévenues, "
            + "et un appareil militaire a décollé pour intercepter votre avion. Si vous recevez "
            + "cette transmission, accusez réception immédiatement.",
        AtcLanguage.German =>
            "{callsign}, wir haben Ihre Rückmeldung nicht erhalten. Wir können keine "
            + "Funkverbindung zu Ihnen herstellen. Die militärischen Behörden wurden "
            + "benachrichtigt, und ein Militärflugzeug wurde zum Abfangen Ihres Luftfahrzeugs "
            + "gestartet. Wenn Sie diese Übermittlung empfangen, bestätigen Sie sofort.",
        AtcLanguage.Spanish =>
            "{callsign}, no hemos recibido su colación. No conseguimos establecer "
            + "comunicación con usted. Se ha notificado a las autoridades militares, y una "
            + "aeronave militar ha despegado para interceptar su avión. Si recibe esta "
            + "transmisión, acuse recibo inmediatamente.",
        AtcLanguage.Italian =>
            "{callsign}, non abbiamo ricevuto il suo riporto. Non riusciamo a stabilire "
            + "comunicazione con lei. Le autorità militari sono state avvisate, e un "
            + "velivolo militare è decollato per intercettare il suo aeromobile. Se riceve "
            + "questa trasmissione, accusi ricevuta immediatamente.",
        _ =>
            "{callsign}, we have not received your readback. We are unable to establish "
            + "communications with you. The military authorities have been notified, and a "
            + "military aircraft has been scrambled to intercept your aircraft. If you are "
            + "receiving this transmission, acknowledge immediately.",
    };

    /// <summary>
    /// L'INTERCEPTEUR, sur la fréquence. Signaux OACI réels : battement d'ailes pour
    /// « compris, je vous suis » — c'est ce qui se dit à un appareil qui n'a plus de radio,
    /// et le seul ordre qu'il puisse encore exécuter.
    /// </summary>
    public static string MilitaryIntercept(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French =>
            "{callsign}, ici la défense aérienne, sur votre aile gauche. Vous êtes intercepté. "
            + "Battez des ailes et suivez-moi. Ne manœuvrez pas brusquement.",
        AtcLanguage.German =>
            "{callsign}, hier ist die Luftverteidigung, an Ihrer linken Tragfläche. Sie werden abgefangen. "
            + "Wackeln Sie mit den Flügeln und folgen Sie mir. Keine abrupten Manöver.",
        AtcLanguage.Spanish =>
            "{callsign}, aquí la defensa aérea, sobre su ala izquierda. Está siendo interceptado. "
            + "Balancee las alas y sígame. No maniobre bruscamente.",
        AtcLanguage.Italian =>
            "{callsign}, qui la difesa aerea, sulla sua ala sinistra. È intercettato. "
            + "Faccia oscillare le ali e mi segua. Nessuna manovra brusca.",
        _ =>
            "{callsign}, this is Air Defence, on your left wing. You are being intercepted. "
            + "Rock your wings and follow me. Do not manoeuvre abruptly.",
    };

    /// <summary>
    /// LA SUITE DE L'INTERCEPTION, dans l'ordre. Une seule phrase répétée à l'identique ne
    /// ressemblait à rien : une interception RÉELLE progresse — on se signale, on ordonne, on
    /// constate l'absence de réaction, on annonce le déroutement, puis on accompagne.
    ///
    /// La phraséologie suit l'Annexe 2 de l'OACI, qui est le seul document où ces échanges
    /// existent : « you have been intercepted », « follow me », « you land », et le battement
    /// d'ailes comme accusé de réception — le seul ordre qu'un appareil sans radio puisse
    /// encore exécuter et confirmer.
    /// </summary>
    public static IReadOnlyList<string> MilitaryFollowUp(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => new[]
        {
            "{callsign}, aucune réaction observée. Battez des ailes pour accuser réception.",
            "{callsign}, défense aérienne. Affichez 7600. Ne manœuvrez pas, nous restons à votre gauche.",
            "{callsign}, vous êtes escorté. Suivez-moi, virez à gauche, descendez au niveau 100.",
            "{callsign}, nous vous accompagnons jusqu'à l'atterrissage. Ne vous écartez pas de mon cap.",
        },
        AtcLanguage.German => new[]
        {
            "{callsign}, keine Reaktion festgestellt. Wackeln Sie mit den Flügeln zur Bestätigung.",
            "{callsign}, Luftverteidigung. Squawk 7600. Keine Manöver, wir bleiben links von Ihnen.",
            "{callsign}, Sie werden eskortiert. Folgen Sie mir, links abdrehen, sinken auf Flugfläche 100.",
            "{callsign}, wir begleiten Sie bis zur Landung. Weichen Sie nicht von meinem Kurs ab.",
        },
        AtcLanguage.Spanish => new[]
        {
            "{callsign}, sin reacción observada. Balancee las alas para acusar recibo.",
            "{callsign}, defensa aérea. Ponga 7600. No maniobre, permanecemos a su izquierda.",
            "{callsign}, está siendo escoltado. Sígame, vire a la izquierda, descienda al nivel 100.",
            "{callsign}, le acompañamos hasta el aterrizaje. No se desvíe de mi rumbo.",
        },
        AtcLanguage.Italian => new[]
        {
            "{callsign}, nessuna reazione osservata. Faccia oscillare le ali per accusare ricevuta.",
            "{callsign}, difesa aerea. Metta 7600. Nessuna manovra, restiamo alla sua sinistra.",
            "{callsign}, è scortato. Mi segua, viri a sinistra, scenda al livello 100.",
            "{callsign}, la accompagniamo fino all'atterraggio. Non si scosti dalla mia prua.",
        },
        _ => new[]
        {
            "{callsign}, no response observed. Rock your wings to acknowledge.",
            "{callsign}, Air Defence. Squawk seven six zero zero. Do not manoeuvre, we are holding on your left.",
            "{callsign}, you are under escort. Follow me, turn left, descend flight level one zero zero.",
            "{callsign}, we will stay with you to landing. Do not deviate from my heading.",
        },
    };

    /// <summary>
    /// Collationnement incomplet, en NOMMANT CE QUI MANQUE : « négatif, collationnez la
    /// piste 0 9 ».
    ///
    /// Redire l'instruction entière — ce que faisait la version précédente — n'apprend rien
    /// au pilote : il vient de l'entendre, et rien ne lui indique lequel des éléments il a
    /// omis. Un vrai contrôleur ne réclame que la partie manquante, et c'est précisément
    /// l'information dont on a besoin pour se corriger.
    /// </summary>
    public static string ReadbackMissing(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "{callsign}, négatif, collationnez {items}.",
        AtcLanguage.German => "{callsign}, negativ, lesen Sie {items} zurück.",
        AtcLanguage.Spanish => "{callsign}, negativo, colacione {items}.",
        AtcLanguage.Italian => "{callsign}, negativo, riporti {items}.",
        _ => "{callsign}, negative, read back {items}.",
    };

    /// <summary>Nom de l'élément à relire, tel qu'un contrôleur le prononce.</summary>
    public static string ReadbackItemLabel(AtcLanguage lang, ReadbackItemKind kind) => (lang, kind) switch
    {
        (AtcLanguage.French, ReadbackItemKind.Runway) => "la piste",
        (AtcLanguage.French, ReadbackItemKind.Squawk) => "le transpondeur",
        (AtcLanguage.French, ReadbackItemKind.Frequency) => "la fréquence",
        (AtcLanguage.French, ReadbackItemKind.Altitude) => "l'altitude",

        (AtcLanguage.German, ReadbackItemKind.Runway) => "die Piste",
        (AtcLanguage.German, ReadbackItemKind.Squawk) => "den Squawk",
        (AtcLanguage.German, ReadbackItemKind.Frequency) => "die Frequenz",
        (AtcLanguage.German, ReadbackItemKind.Altitude) => "die Höhe",

        (AtcLanguage.Spanish, ReadbackItemKind.Runway) => "la pista",
        (AtcLanguage.Spanish, ReadbackItemKind.Squawk) => "el transpondedor",
        (AtcLanguage.Spanish, ReadbackItemKind.Frequency) => "la frecuencia",
        (AtcLanguage.Spanish, ReadbackItemKind.Altitude) => "la altitud",

        (AtcLanguage.Italian, ReadbackItemKind.Runway) => "la pista",
        (AtcLanguage.Italian, ReadbackItemKind.Squawk) => "il transponder",
        (AtcLanguage.Italian, ReadbackItemKind.Frequency) => "la frequenza",
        (AtcLanguage.Italian, ReadbackItemKind.Altitude) => "la quota",

        (_, ReadbackItemKind.Runway) => "runway",
        (_, ReadbackItemKind.Squawk) => "squawk",
        (_, ReadbackItemKind.Frequency) => "the frequency",
        _ => "the altitude",
    };

    // ------------------------------------------------------------------ altitudes

    /// <summary>« flight level 250 » / « niveau de vol 250 ».</summary>
    public static string FlightLevel(AtcLanguage lang, int level) => lang switch
    {
        AtcLanguage.French => $"niveau de vol {level}",
        AtcLanguage.German => $"Flugfläche {level}",
        AtcLanguage.Spanish => $"nivel de vuelo {level}",
        AtcLanguage.Italian => $"livello di volo {level}",
        _ => $"flight level {level}",
    };

    /// <summary>
    /// Niveau initial d'une clairance de départ : « level 70 », « niveau 70 ».
    ///
    /// PAS <see cref="FlightLevel"/>. La délivrance annonce le palier initial sans le mot
    /// « flight » — « startup approved, CIV 2 Delta departure, LEVEL 70, squawk 3400 » — là où
    /// « flight level » se dit en route. Le mot « level » reste présent, donc le vérificateur
    /// de collationnement continue d'exiger la relecture du chiffre.
    /// </summary>
    public static string Level(AtcLanguage lang, int level) => lang switch
    {
        AtcLanguage.French => $"niveau {level}",
        AtcLanguage.German => $"Flugfläche {level}",
        AtcLanguage.Spanish => $"nivel {level}",
        AtcLanguage.Italian => $"livello {level}",
        _ => $"level {level}",
    };

    /// <summary>« 5000 feet » / « 5000 pieds ».</summary>
    public static string Feet(AtcLanguage lang, long feet) => lang switch
    {
        AtcLanguage.French => $"{feet} pieds",
        AtcLanguage.German => $"{feet} Fuß",
        AtcLanguage.Spanish => $"{feet} pies",
        AtcLanguage.Italian => $"{feet} piedi",
        _ => $"{feet} feet",
    };

    /// <summary>Verbe d'une clairance d'altitude : monter / descendre / maintenir.</summary>
    public static string AltitudeVerb(AtcLanguage lang, bool? climb) => (lang, climb) switch
    {
        (AtcLanguage.French, true) => "montez et maintenez",
        (AtcLanguage.French, false) => "descendez et maintenez",
        (AtcLanguage.French, _) => "maintenez",
        (AtcLanguage.German, true) => "steigen und halten Sie",
        (AtcLanguage.German, false) => "sinken und halten Sie",
        (AtcLanguage.German, _) => "halten Sie",
        (AtcLanguage.Spanish, true) => "ascienda y mantenga",
        (AtcLanguage.Spanish, false) => "descienda y mantenga",
        (AtcLanguage.Spanish, _) => "mantenga",
        (AtcLanguage.Italian, true) => "salga e mantenga",
        (AtcLanguage.Italian, false) => "scenda e mantenga",
        (AtcLanguage.Italian, _) => "mantenga",
        (_, true) => "climb and maintain",
        (_, false) => "descend and maintain",
        (_, _) => "maintain",
    };

    /// <summary>Demande d'altitude SANS chiffre : « montez à votre discrétion ».</summary>
    public static string AltitudeDiscretion(AtcLanguage lang, bool? climb) => (lang, climb) switch
    {
        (AtcLanguage.French, true) => "montez à votre discrétion",
        (AtcLanguage.French, false) => "descendez à votre discrétion",
        (AtcLanguage.French, _) => "maintenez votre niveau actuel",
        (AtcLanguage.German, true) => "steigen Sie nach eigenem Ermessen",
        (AtcLanguage.German, false) => "sinken Sie nach eigenem Ermessen",
        (AtcLanguage.German, _) => "halten Sie Ihre derzeitige Höhe",
        (AtcLanguage.Spanish, true) => "ascienda a su discreción",
        (AtcLanguage.Spanish, false) => "descienda a su discreción",
        (AtcLanguage.Spanish, _) => "mantenga su nivel actual",
        (AtcLanguage.Italian, true) => "salga a sua discrezione",
        (AtcLanguage.Italian, false) => "scenda a sua discrezione",
        (AtcLanguage.Italian, _) => "mantenga il livello attuale",
        (_, true) => "climb at your discretion",
        (_, false) => "descend at your discretion",
        (_, _) => "maintain your present level",
    };

    // ------------------------------------------------------------------ pistes

    /// <summary>Piste inconnue : formule qui s'insère là où un numéro serait allé.</summary>
    public static string ActiveRunway(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "la piste en service",
        AtcLanguage.German => "die aktive Piste",
        AtcLanguage.Spanish => "la pista en uso",
        AtcLanguage.Italian => "la pista in uso",
        _ => "the active runway",
    };

    /// <summary>« runway 3 4 right » — le mot piste et le côté.</summary>
    public static string Runway(AtcLanguage lang, string digits, char side)
    {
        string word = lang switch
        {
            AtcLanguage.French => "piste",
            AtcLanguage.German => "Piste",
            AtcLanguage.Spanish => "pista",
            AtcLanguage.Italian => "pista",
            _ => "runway",
        };

        string suffix = (lang, side) switch
        {
            (AtcLanguage.French, 'L') => " gauche",
            (AtcLanguage.French, 'R') => " droite",
            (AtcLanguage.French, 'C') => " centre",
            (AtcLanguage.German, 'L') => " links",
            (AtcLanguage.German, 'R') => " rechts",
            (AtcLanguage.German, 'C') => " mitte",
            (AtcLanguage.Spanish, 'L') => " izquierda",
            (AtcLanguage.Spanish, 'R') => " derecha",
            (AtcLanguage.Spanish, 'C') => " centro",
            (AtcLanguage.Italian, 'L') => " sinistra",
            (AtcLanguage.Italian, 'R') => " destra",
            (AtcLanguage.Italian, 'C') => " centro",
            (_, 'L') => " left",
            (_, 'R') => " right",
            (_, 'C') => " center",
            _ => "",
        };

        return $"{word} {digits}{suffix}";
    }

    // ------------------------------------------------------------------ positions de contrôle

    /// <summary>
    /// Nom PARLÉ d'une position de contrôle. En vrai, ces noms restent souvent en anglais
    /// même en phraséologie locale (« Ground », « Tower ») — sauf en France et en Espagne,
    /// où le nom local est la norme sur le trafic intérieur.
    /// </summary>
    public static string Controller(AtcLanguage lang, ControllerType type) => (lang, type) switch
    {
        (AtcLanguage.French, ControllerType.Clearance) => "la prévol",
        (AtcLanguage.French, ControllerType.Ground) => "le sol",
        (AtcLanguage.French, ControllerType.Tower) => "la tour",
        (AtcLanguage.French, ControllerType.Approach) => "l'approche",
        (AtcLanguage.French, ControllerType.Departure) => "le départ",
        (AtcLanguage.French, ControllerType.Center) => "le contrôle",
        (AtcLanguage.German, ControllerType.Clearance) => "Clearance Delivery",
        (AtcLanguage.German, ControllerType.Ground) => "Rollkontrolle",
        (AtcLanguage.German, ControllerType.Tower) => "Turm",
        (AtcLanguage.German, ControllerType.Approach) => "Anflug",
        (AtcLanguage.German, ControllerType.Departure) => "Abflug",
        (AtcLanguage.German, ControllerType.Center) => "Radar",
        (AtcLanguage.Spanish, ControllerType.Clearance) => "autorizaciones",
        (AtcLanguage.Spanish, ControllerType.Ground) => "rodadura",
        (AtcLanguage.Spanish, ControllerType.Tower) => "torre",
        (AtcLanguage.Spanish, ControllerType.Approach) => "aproximación",
        (AtcLanguage.Spanish, ControllerType.Departure) => "salida",
        (AtcLanguage.Spanish, ControllerType.Center) => "control",
        (AtcLanguage.Italian, ControllerType.Clearance) => "clearance delivery",
        (AtcLanguage.Italian, ControllerType.Ground) => "ground",
        (AtcLanguage.Italian, ControllerType.Tower) => "torre",
        (AtcLanguage.Italian, ControllerType.Approach) => "avvicinamento",
        (AtcLanguage.Italian, ControllerType.Departure) => "partenze",
        (AtcLanguage.Italian, ControllerType.Center) => "controllo",
        (_, ControllerType.Clearance) => "Clearance Delivery",
        (_, ControllerType.Ground) => "Ground",
        (_, ControllerType.Tower) => "Tower",
        (_, ControllerType.Approach) => "Approach",
        (_, ControllerType.Departure) => "Departure",
        (_, ControllerType.Center) => "Center",
        _ => type.ToString(),
    };

    /// <summary>
    /// Nom du SOL tel qu'on le donne en rendant la main : « Brussels Ground ».
    ///
    /// En anglais, en allemand et en italien la position est un nom propre, qu'on accole au
    /// terrain. En français et en espagnol, c'est un nom commun avec article (« le sol »,
    /// « rodadura ») : « Bruxelles le sol » ne se dit pas, on garde la position seule.
    /// </summary>
    public static string GroundStation(AtcLanguage lang, string? airport)
    {
        string position = Controller(lang, ControllerType.Ground);

        if (string.IsNullOrWhiteSpace(airport)) return position;

        return lang switch
        {
            AtcLanguage.French or AtcLanguage.Spanish => position,
            _ => $"{airport!.Trim()} {position}",
        };
    }

    /// <summary>Destinataire inconnu d'un refus « mauvais contrôleur ».</summary>
    public static string AppropriateController(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "le contrôleur approprié",
        AtcLanguage.German => "die zuständige Stelle",
        AtcLanguage.Spanish => "el controlador correspondiente",
        AtcLanguage.Italian => "il controllore competente",
        _ => "the appropriate controller",
    };

    /// <summary>Destination inconnue dans une clairance.</summary>
    public static string YourDestination(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "votre destination",
        AtcLanguage.German => "Ihr Ziel",
        AtcLanguage.Spanish => "su destino",
        AtcLanguage.Italian => "la sua destinazione",
        _ => "your destination",
    };

    // ------------------------------------------------------------------ divers gabarits

    /// <summary>Points cardinaux (sortie de zone VFR, orientation du repoussage).</summary>
    public static string[] Directions(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => new[] { "le nord", "le sud", "l'est", "l'ouest" },
        AtcLanguage.German => new[] { "Norden", "Süden", "Osten", "Westen" },
        AtcLanguage.Spanish => new[] { "el norte", "el sur", "el este", "el oeste" },
        AtcLanguage.Italian => new[] { "nord", "sud", "est", "ovest" },
        _ => new[] { "north", "south", "east", "west" },
    };

    /// <summary>Sens du tour de piste. Le gauche est le standard, d'où sa fréquence double.</summary>
    public static string[] Circuits(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => new[] { "main gauche", "main gauche", "main droite" },
        AtcLanguage.German => new[] { "linke Platzrunde", "linke Platzrunde", "rechte Platzrunde" },
        AtcLanguage.Spanish => new[] { "circuito por la izquierda", "circuito por la izquierda", "circuito por la derecha" },
        AtcLanguage.Italian => new[] { "circuito sinistro", "circuito sinistro", "circuito destro" },
        _ => new[] { "left hand", "left hand", "right hand" },
    };

    /// <summary>Suffixe « sur {fréquence} » d'un « contactez le sol ».</summary>
    public static string OnFrequency(AtcLanguage lang, string freq) => lang switch
    {
        AtcLanguage.French => $" sur {freq}",
        AtcLanguage.German => $" auf {freq}",
        AtcLanguage.Spanish => $" en {freq}",
        AtcLanguage.Italian => $" su {freq}",
        _ => $" on {freq}",
    };

    // ------------------------------------------------------------------ abréviations de station

    /// <summary>
    /// Développement des abréviations OurAirports (TWR, GND…) en mots prononçables, dans
    /// la langue du contrôleur. Employé par <see cref="StationSpeech"/>.
    /// </summary>
    public static (string Abbr, string Word)[] StationAbbreviations(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => new[]
        {
            ("TWR", "Tour"), ("GND", "Sol"), ("APP", "Approche"), ("DEP", "Départ"),
            ("ATIS", "ATIS"), ("CTR", "Contrôle"), ("CLR", "Prévol"), ("DEL", "Prévol"),
            ("UNIC", "Unicom"), ("FSS", "Radio"), ("APRON", "Trafic"), ("RDO", "Radio"),
            ("CTAF", "Auto-information"),
        },
        AtcLanguage.German => new[]
        {
            ("TWR", "Turm"), ("GND", "Rollkontrolle"), ("APP", "Anflug"), ("DEP", "Abflug"),
            ("ATIS", "ATIS"), ("CTR", "Radar"), ("CLR", "Clearance"), ("DEL", "Delivery"),
            ("UNIC", "Unicom"), ("FSS", "Radio"), ("APRON", "Vorfeld"), ("RDO", "Radio"),
            ("CTAF", "Verkehr"),
        },
        AtcLanguage.Spanish => new[]
        {
            ("TWR", "Torre"), ("GND", "Rodadura"), ("APP", "Aproximación"), ("DEP", "Salida"),
            ("ATIS", "ATIS"), ("CTR", "Control"), ("CLR", "Autorizaciones"), ("DEL", "Autorizaciones"),
            ("UNIC", "Unicom"), ("FSS", "Radio"), ("APRON", "Plataforma"), ("RDO", "Radio"),
            ("CTAF", "Tráfico"),
        },
        AtcLanguage.Italian => new[]
        {
            ("TWR", "Torre"), ("GND", "Ground"), ("APP", "Avvicinamento"), ("DEP", "Partenze"),
            ("ATIS", "ATIS"), ("CTR", "Controllo"), ("CLR", "Clearance"), ("DEL", "Delivery"),
            ("UNIC", "Unicom"), ("FSS", "Radio"), ("APRON", "Piazzale"), ("RDO", "Radio"),
            ("CTAF", "Traffico"),
        },
        _ => new[]
        {
            ("TWR", "Tower"), ("GND", "Ground"), ("APP", "Approach"), ("DEP", "Departure"),
            ("ATIS", "Information"), ("CTR", "Control"), ("CLR", "Clearance"), ("DEL", "Delivery"),
            ("UNIC", "Unicom"), ("FSS", "Radio"), ("APRON", "Apron"), ("RDO", "Radio"),
            ("CTAF", "Traffic"),
        },
    };
}
