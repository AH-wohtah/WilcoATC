namespace WilcoATC.Atc.Understanding;

/// <summary>
/// Mots-clés qui identifient une intention pilote, par langue.
///
/// Le contrôleur COMPREND toutes les langues gérées, quelle que soit celle dans laquelle il
/// répond : la reconnaissance vocale (Parakeet v3, multilingue) rend le texte dans la langue
/// réellement parlée, et <see cref="GrammarIntentRecognizer"/> essaie ensuite CHAQUE table.
/// Celle qui gagne donne à la fois l'intention ET la langue du pilote.
///
/// RÈGLES D'ÉCRITURE, apprises de la table anglaise :
///  • tout en minuscules, sans ponctuation ni accent — le texte comparé est normalisé
///    (voir <see cref="AtcTextNormalizer"/>), les accents sont retirés des deux côtés ;
///  • pas de mot-clé de moins de 4 lettres qui soit un mot courant : la correspondance est
///    floue (une faute tolérée dès 4 lettres), un mot trop court déclencherait sur tout ;
///  • c'est le mot-clé le PLUS LONG qui gagne : on peut donc lister « pret au depart » ET
///    « depart » sans que le second vole la mise au premier.
/// </summary>
public static class IntentKeywords
{
    /// <summary>Table (intention, mots-clés) pour une langue. Anglais par défaut.</summary>
    public static (PilotIntent Intent, string[] Keywords)[] For(AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => French,
        AtcLanguage.German => German,
        AtcLanguage.Spanish => Spanish,
        AtcLanguage.Italian => Italian,
        _ => English,
    };

    // ------------------------------------------------------------------ anglais (OACI)

    public static readonly (PilotIntent Intent, string[] Keywords)[] English =
    {
        // L'URGENCE PASSE AVANT TOUT — l'ordre de cette table décide, la première
        // correspondance l'emportant. « Mayday, engine failure, request immediate landing »
        // contient « request » et « landing » : testée après, la détresse serait traitée comme
        // une banale demande, avec les refus et les collationnements qui vont avec.
        //
        // L'ANNULATION avant la déclaration, pour la même raison : « cancel mayday » contient
        // « mayday ».
        (PilotIntent.CancelEmergency, new[]
        {
            "cancel mayday", "cancel the mayday", "cancel pan pan", "cancel emergency",
            "cancel the emergency", "emergency cancelled", "situation resolved", "no longer an emergency",
        }),
        (PilotIntent.DeclareMayday, new[]
        {
            "mayday", "declaring an emergency", "declare an emergency", "we have an emergency",
            "engine failure", "engine fire", "on fire", "loss of control", "declaring emergency",
        }),
        (PilotIntent.DeclarePanPan, new[]
        {
            "pan pan", "panpan", "urgency", "medical emergency", "passenger ill",
            "minimum fuel", "low on fuel", "fuel emergency", "instrument failure",
        }),
        (PilotIntent.ReadyForDeparture, new[]
        {
            "ready for departure", "ready for takeoff", "ready to depart", "ready departure",
            "holding short ready", "ready for the runway", "lined up and ready",
        }),
        (PilotIntent.RequestPushback, new[]
        {
            "request pushback", "pushback", "ready for pushback", "push",
        }),
        (PilotIntent.RequestTaxi, new[]
        {
            "request taxi", "taxi", "ready to taxi", "taxi to the holding point", "taxi to runway",
        }),
        (PilotIntent.RequestClearance, new[]
        {
            "request clearance", "clearance", "ifr clearance", "ifr", "startup", "delivery",
            "cleared to", "request start", "ready to copy clearance",
        }),
        // Changement d'altitude EN VOL. Placé AVANT le report d'approche : à longueur de
        // mot-clé égale, c'est le premier du tableau qui gagne, et « flight level » doit
        // pencher vers une demande de niveau plutôt que vers une jointure de circuit.
        (PilotIntent.RequestAltitude, new[]
        {
            "request climb", "request descent", "request higher", "request lower",
            "request altitude", "request level change", "request flight level",
            "climb to", "descend to", "climb flight level", "descend flight level",
            "flight level",
        }),
        // LA FINALE AVANT L'APPROCHE : « established on final » contient « established », qui
        // vaut approche. Testée en second, la finale ne serait jamais reconnue — c'est l'ordre
        // qui décide, la première correspondance l'emportant.
        (PilotIntent.ReportFinal, new[]
        {
            "final", "on final", "short final", "final approach", "turning final",
            "established on the ils", "gear down", "runway in sight",
        }),
        (PilotIntent.ReportApproach, new[]
        {
            "inbound", "on approach", "established", "with information", "field in sight",
        }),
        (PilotIntent.CheckIn, new[]
        {
            "good day", "good morning", "good afternoon", "good evening", "hello",
            "with you", "check in", "on frequency",
        }),
        (PilotIntent.Readback, new[]
        {
            "roger", "wilco", "copy", "readback", "affirm", "understood", "acknowledged",
        }),
    };

    // ------------------------------------------------------------------ français

    private static readonly (PilotIntent Intent, string[] Keywords)[] French =
    {
        // Urgence en tête, annulation avant déclaration : voir la table anglaise.
        (PilotIntent.CancelEmergency, new[]
        {
            "annule mayday", "annulez mayday", "annule le mayday", "annule pan pan",
            "annule l urgence", "fin d urgence", "situation retablie", "plus d urgence",
        }),
        (PilotIntent.DeclareMayday, new[]
        {
            "mayday", "je declare une urgence", "situation d urgence", "panne moteur",
            "feu moteur", "au feu", "perte de controle", "detresse",
        }),
        (PilotIntent.DeclarePanPan, new[]
        {
            "pan pan", "panpan", "passager malade", "urgence medicale", "carburant minimum",
            "panne d instrument", "probleme technique",
        }),
        (PilotIntent.ReadyForDeparture, new[]
        {
            "pret au depart", "prete au depart", "pret pour le decollage", "pret decollage",
            "prets au depart", "au point d attente pret", "aligne et pret", "pour le decollage",
        }),
        (PilotIntent.RequestPushback, new[]
        {
            "demande repoussage", "repoussage", "pret pour le repoussage", "demande le repoussage",
            "push back", "pushback",
        }),
        (PilotIntent.RequestTaxi, new[]
        {
            "demande roulage", "roulage", "demande le roulage", "pret a rouler",
            "pour rouler", "roulage point d attente",
        }),
        (PilotIntent.RequestClearance, new[]
        {
            "demande la clairance", "demande clairance", "clairance", "autorisation de depart",
            "demande la mise en route", "mise en route", "pret a copier", "clairance ifr",
        }),
        (PilotIntent.RequestAltitude, new[]
        {
            "demande la montee", "demande montee", "demande la descente", "demande descente",
            "demande niveau superieur", "demande niveau inferieur", "demande changement de niveau",
            "demande le niveau", "monter au niveau", "descendre au niveau", "niveau de vol",
        }),
        (PilotIntent.ReportFinal, new[]
        {
            "en finale", "en courte finale", "finale", "piste en vue", "train sorti",
        }),
        (PilotIntent.ReportApproach, new[]
        {
            "en approche", "etabli", "verticale terrain", "terrain en vue",
            "avec information", "en rapprochement",
        }),
        (PilotIntent.CheckIn, new[]
        {
            "bonjour", "bonsoir", "avec vous", "sur la frequence", "je vous ecoute",
        }),
        (PilotIntent.Readback, new[]
        {
            "bien recu", "recu", "affirme", "compris", "wilco", "collationne",
        }),
    };

    // ------------------------------------------------------------------ allemand

    private static readonly (PilotIntent Intent, string[] Keywords)[] German =
    {
        (PilotIntent.CancelEmergency, new[]
        {
            "mayday annullieren", "notfall aufgehoben", "notlage beendet", "kein notfall mehr",
        }),
        (PilotIntent.DeclareMayday, new[]
        {
            "mayday", "ich erklare den notfall", "notfall", "triebwerksausfall", "feuer an bord",
            "kontrollverlust",
        }),
        (PilotIntent.DeclarePanPan, new[]
        {
            "pan pan", "panpan", "dringlichkeit", "medizinischer notfall", "kranker passagier",
            "minimum fuel", "instrumentenausfall",
        }),
        (PilotIntent.ReadyForDeparture, new[]
        {
            "bereit zum abflug", "startbereit", "bereit zum start", "abflugbereit",
            "am rollhalt bereit", "aufgereiht und bereit",
        }),
        (PilotIntent.RequestPushback, new[]
        {
            "erbitte pushback", "pushback", "bereit zum pushback", "erbitte zuruckstossen",
            "zuruckstossen",
        }),
        (PilotIntent.RequestTaxi, new[]
        {
            "erbitte rollen", "rollen", "bereit zum rollen", "erbitte rollfreigabe",
            "rollfreigabe", "zum rollhalt",
        }),
        (PilotIntent.RequestClearance, new[]
        {
            "erbitte freigabe", "freigabe", "streckenfreigabe", "ifr freigabe",
            "erbitte anlassfreigabe", "anlassfreigabe", "bereit zum mitschreiben",
        }),
        (PilotIntent.RequestAltitude, new[]
        {
            "erbitte steigflug", "erbitte sinkflug", "erbitte hoher", "erbitte tiefer",
            "erbitte flugflache", "steigen auf", "sinken auf", "flugflache",
        }),
        (PilotIntent.ReportFinal, new[]
        {
            "im endteil", "im endanflug", "endanflug", "piste in sicht", "fahrwerk ausgefahren",
        }),
        (PilotIntent.ReportApproach, new[]
        {
            "im anflug", "etabliert", "platz in sicht", "mit information",
        }),
        (PilotIntent.CheckIn, new[]
        {
            "guten tag", "guten morgen", "guten abend", "gruss gott", "bei ihnen",
            "auf der frequenz",
        }),
        (PilotIntent.Readback, new[]
        {
            "verstanden", "roger", "wilco", "bestatige", "affirm",
        }),
    };

    // ------------------------------------------------------------------ espagnol

    private static readonly (PilotIntent Intent, string[] Keywords)[] Spanish =
    {
        (PilotIntent.CancelEmergency, new[]
        {
            "cancelar mayday", "cancelo mayday", "cancelar emergencia", "emergencia cancelada",
            "situacion resuelta",
        }),
        (PilotIntent.DeclareMayday, new[]
        {
            "mayday", "declaro emergencia", "declaramos emergencia", "fallo de motor",
            "fuego a bordo", "perdida de control",
        }),
        (PilotIntent.DeclarePanPan, new[]
        {
            "pan pan", "panpan", "urgencia", "emergencia medica", "pasajero enfermo",
            "combustible minimo", "fallo de instrumentos",
        }),
        (PilotIntent.ReadyForDeparture, new[]
        {
            "listo para despegue", "listo para salida", "preparado para despegue",
            "listos para despegue", "en punto de espera listo", "alineado y listo",
        }),
        (PilotIntent.RequestPushback, new[]
        {
            "solicito retroceso", "retroceso", "listo para retroceso", "solicito pushback",
            "pushback",
        }),
        (PilotIntent.RequestTaxi, new[]
        {
            "solicito rodaje", "rodaje", "listo para rodar", "solicito rodar",
            "rodaje al punto de espera",
        }),
        (PilotIntent.RequestClearance, new[]
        {
            "solicito autorizacion", "autorizacion", "autorizacion ifr", "solicito puesta en marcha",
            "puesta en marcha", "listo para copiar",
        }),
        (PilotIntent.RequestAltitude, new[]
        {
            "solicito ascenso", "solicito descenso", "solicito nivel superior",
            "solicito nivel inferior", "solicito cambio de nivel", "ascender a nivel",
            "descender a nivel", "nivel de vuelo",
        }),
        (PilotIntent.ReportFinal, new[]
        {
            "en final", "en corta final", "pista a la vista", "tren abajo",
        }),
        (PilotIntent.ReportApproach, new[]
        {
            "en aproximacion", "establecido", "campo a la vista", "con informacion",
        }),
        (PilotIntent.CheckIn, new[]
        {
            "buenos dias", "buenas tardes", "buenas noches", "con usted", "en frecuencia",
        }),
        (PilotIntent.Readback, new[]
        {
            "recibido", "copiado", "afirmo", "entendido", "wilco",
        }),
    };

    // ------------------------------------------------------------------ italien

    private static readonly (PilotIntent Intent, string[] Keywords)[] Italian =
    {
        (PilotIntent.CancelEmergency, new[]
        {
            "annulla mayday", "annullo mayday", "annulla emergenza", "emergenza annullata",
            "situazione risolta",
        }),
        (PilotIntent.DeclareMayday, new[]
        {
            "mayday", "dichiaro emergenza", "dichiariamo emergenza", "avaria motore",
            "incendio a bordo", "perdita di controllo",
        }),
        (PilotIntent.DeclarePanPan, new[]
        {
            "pan pan", "panpan", "urgenza", "emergenza medica", "passeggero malato",
            "carburante minimo", "avaria strumenti",
        }),
        (PilotIntent.ReadyForDeparture, new[]
        {
            "pronto al decollo", "pronti al decollo", "pronto per il decollo",
            "pronto alla partenza", "al punto attesa pronto", "allineato e pronto",
        }),
        (PilotIntent.RequestPushback, new[]
        {
            "richiedo pushback", "pushback", "pronto per il pushback", "richiedo spinta",
        }),
        (PilotIntent.RequestTaxi, new[]
        {
            "richiedo rullaggio", "rullaggio", "pronto al rullaggio", "richiedo di rullare",
            "rullaggio al punto attesa",
        }),
        (PilotIntent.RequestClearance, new[]
        {
            "richiedo autorizzazione", "autorizzazione", "autorizzazione ifr",
            "richiedo messa in moto", "messa in moto", "pronto a copiare",
        }),
        (PilotIntent.RequestAltitude, new[]
        {
            "richiedo salita", "richiedo discesa", "richiedo livello superiore",
            "richiedo livello inferiore", "richiedo cambio livello", "salire al livello",
            "scendere al livello", "livello di volo",
        }),
        (PilotIntent.ReportFinal, new[]
        {
            "in finale", "in corto finale", "pista in vista", "carrello estratto",
        }),
        (PilotIntent.ReportApproach, new[]
        {
            "in avvicinamento", "stabilizzato", "campo in vista", "con informazione",
        }),
        (PilotIntent.CheckIn, new[]
        {
            "buongiorno", "buonasera", "con lei", "sulla frequenza",
        }),
        (PilotIntent.Readback, new[]
        {
            "ricevuto", "copiato", "affermo", "capito", "wilco",
        }),
    };
}
