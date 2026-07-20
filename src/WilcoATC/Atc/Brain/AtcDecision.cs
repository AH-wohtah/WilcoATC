using FreqWatch.Atc.Context;
using FreqWatch.Atc.Understanding;

namespace FreqWatch.Atc.Brain;

/// <summary>Décision de l'AtcBrain : accordé/refusé + texte de réponse + raison (debug).</summary>
public sealed record AtcDecision(
    bool Approved,
    PilotIntent Intent,
    string ResponseText,
    string DebugReason,
    FlightPhase? AdvanceTo);
