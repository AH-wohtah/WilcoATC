using FreqWatch.Atc.Brain;
using FreqWatch.Atc.Context;
using FreqWatch.Atc.Understanding;
using FreqWatch.Common;

namespace FreqWatch.Atc;

/// <summary>Contrôleur ATC inerte — pour l'aperçu du concepteur XAML uniquement.</summary>
public sealed class NullAtcController : IAtcController
{
    public void Start() { }
    public void TriggerManualTest() { }
    public void HandlePilotText(string text) { }
    public void SetControllerOverride(ControllerType? controller) { }
    public void SetPhaseOverride(FlightPhase? phase) { }
    public void SetHasBeenAirborneOverride(bool? value) { }
    public void SetTakeoffClearedOverride(bool cleared) { }
    public bool Enabled { get; set; }
    public bool TestMode { get; set; }
    public event Action<string>? TransmissionText { add { } remove { } }
    public event Action<string>? StatusChanged { add { } remove { } }
    public event Action<string>? PilotTranscript { add { } remove { } }
    public event Action<RecognizedIntent>? IntentRecognized { add { } remove { } }
    public event Action<AtcDecision>? DecisionMade { add { } remove { } }
    public event Action<FlightPhaseDebug>? PhaseChanged { add { } remove { } }
    public event Action<bool>? ExpectingReadbackChanged { add { } remove { } }
    public void Dispose() { }
}
