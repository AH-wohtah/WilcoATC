using WilcoATC.Atc.Context;
using WilcoATC.Atc.Understanding;

namespace WilcoATC.Atc.Brain;

/// <summary>Décision de l'AtcBrain : accordé/refusé + texte de réponse + raison (debug).</summary>
public sealed record AtcDecision(
    bool Approved,
    PilotIntent Intent,
    string ResponseText,
    string DebugReason,
    FlightPhase? AdvanceTo);
