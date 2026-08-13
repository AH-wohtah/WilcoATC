namespace WilcoATC.Atc.Understanding;

/// <summary>Ensemble FERMÉ des intentions pilote reconnues.</summary>
public enum PilotIntent
{
    RequestClearance,
    RequestPushback,
    RequestTaxi,
    ReadyForDeparture,
    RequestAltitude,  // demande de montée / descente / changement de niveau (EN VOL)
    CheckIn,          // « bonjour » / prise de contact sur une fréquence
    ReportApproach,   // report de niveau / en approche

    /// <summary>
    /// « En finale ». C'est le report qui DÉCLENCHE l'autorisation d'atterrissage.
    ///
    /// Il était confondu avec l'approche, qui ne vaut qu'un « reçu, poursuivez ». L'unique
    /// source d'autorisation était alors le passage en phase « atterrissage » — laquelle,
    /// dans l'estimateur, ne commence qu'AU SOL, pendant le roulage. On était donc autorisé
    /// à atterrir une fois posé : l'ordre exactement inverse de la réalité.
    /// </summary>
    ReportFinal,

    /// <summary>
    /// DÉTRESSE — « mayday ». Danger grave et imminent : panne moteur, feu, incapacité.
    /// L'appareil obtient la priorité absolue, et la fréquence lui est dégagée.
    /// </summary>
    DeclareMayday,

    /// <summary>
    /// URGENCE — « pan pan ». Situation sérieuse mais sans danger immédiat : passager malade,
    /// panne d'instrument, carburant limité. Priorité, sans le silence radio de la détresse.
    /// </summary>
    DeclarePanPan,

    /// <summary>Fin d'urgence : le pilote annule, le contrôle reprend son cours normal.</summary>
    CancelEmergency,

    Readback,
    Unknown,
}

/// <summary>Résultat de la reconnaissance : intention + texte brut + slots/diagnostic.</summary>
public sealed record RecognizedIntent(PilotIntent Intent, string RawText, string Source)
{
    /// <summary>Destination éventuelle extraite du « … to/pour X » (fallback sans SimBrief).</summary>
    public string? DestinationHint { get; init; }

    /// <summary>
    /// Indicatif de compagnie entendu dans la transmission (ex. « Speedbird 123 »), ou null.
    /// Utile au journal et au collationnement : le pilote s'annonce, on le reconnaît.
    /// </summary>
    public string? CallsignHint { get; init; }

    /// <summary>Diagnostic (mot-clé trouvé, ou « aucun mot-clé (langue=FR) ») pour le debug UI.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Langue dans laquelle le pilote a parlé, quand la reconnaissance a pu la déterminer
    /// (c'est la table de mots-clés qui a gagné). Null si aucune intention n'a été reconnue :
    /// on ne devine pas une langue sur du silence, et le contrôleur garde alors la sienne.
    /// </summary>
    public AtcLanguage? Language { get; init; }
}
