namespace FreqWatch.Atc.Understanding;

/// <summary>Ensemble FERMÉ des intentions pilote reconnues.</summary>
public enum PilotIntent
{
    RequestClearance,
    RequestPushback,
    RequestTaxi,
    ReadyForDeparture,
    CheckIn,          // « bonjour » / prise de contact sur une fréquence
    ReportApproach,   // report de niveau / en approche
    Readback,
    Unknown,
}

/// <summary>Résultat de la reconnaissance : intention + texte brut + slots/diagnostic.</summary>
public sealed record RecognizedIntent(PilotIntent Intent, string RawText, string Source)
{
    /// <summary>Destination éventuelle extraite du « … to/pour X » (fallback sans SimBrief).</summary>
    public string? DestinationHint { get; init; }

    /// <summary>Diagnostic (mot-clé trouvé, ou « aucun mot-clé (langue=FR) ») pour le debug UI.</summary>
    public string? Reason { get; init; }
}
