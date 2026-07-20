using FreqWatch.Atc.Brain;
using FreqWatch.Atc.Context;
using FreqWatch.Atc.Understanding;
using FreqWatch.Common;

namespace FreqWatch.Atc;

/// <summary>Orchestrateur de la boucle vocale ATC (déclencheurs -> texte -> voix -> radio).</summary>
public interface IAtcController : IDisposable
{
    void Start();

    /// <summary>Force une transmission de test (bouton / touche), à tout moment.</summary>
    void TriggerManualTest();

    /// <summary>Traite une requête pilote (texte saisi ou transcription vocale).</summary>
    void HandlePilotText(string text);

    /// <summary>Force le type de contrôleur (null = auto depuis la fréquence) — utile pour tester.</summary>
    void SetControllerOverride(ControllerType? controller);

    /// <summary>DEBUG (Mode Test) : force la phase courante (null = auto depuis les capteurs).</summary>
    void SetPhaseOverride(FlightPhase? phase);

    /// <summary>DEBUG (Mode Test) : force le flag « déjà été en l'air » (null = auto).</summary>
    void SetHasBeenAirborneOverride(bool? value);

    /// <summary>DEBUG (Mode Test) : force le flag « autorisé au décollage » (CLEARED_FOR_TAKEOFF).</summary>
    void SetTakeoffClearedOverride(bool cleared);

    /// <summary>Active/désactive l'ATC (persisté).</summary>
    bool Enabled { get; set; }

    /// <summary>Mode Test : accepte toute requête (court-circuite la validation), persisté.</summary>
    bool TestMode { get; set; }

    /// <summary>Texte de la transmission émise (pour le journal / l'UI).</summary>
    event Action<string>? TransmissionText;

    /// <summary>Statut lisible ("Prêt", "Transmission…", "Erreur audio"…).</summary>
    event Action<string>? StatusChanged;

    /// <summary>Transcription/texte de la requête pilote (débogage).</summary>
    event Action<string>? PilotTranscript;

    /// <summary>Intention reconnue + diagnostic (débogage : intention, mot-clé / raison).</summary>
    event Action<RecognizedIntent>? IntentRecognized;

    /// <summary>Décision de l'AtcBrain (accordé/refusé + raison).</summary>
    event Action<AtcDecision>? DecisionMade;

    /// <summary>État de la machine à phases (phase courante + HasBeenAirborne) — debug UI.</summary>
    event Action<FlightPhaseDebug>? PhaseChanged;

    /// <summary>Passe à true quand l'ATC attend un collationnement, false quand il est reçu — debug UI.</summary>
    event Action<bool>? ExpectingReadbackChanged;
}

/// <summary>Instantané de debug de la machine à états de phase.</summary>
public sealed record FlightPhaseDebug(Context.FlightPhase Phase, bool HasBeenAirborne, bool OnGround);
