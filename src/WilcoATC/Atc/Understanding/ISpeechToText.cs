namespace WilcoATC.Atc.Understanding;

/// <summary>
/// Transcription vocale du pilote (push-to-talk). Implémentation par défaut à venir :
/// ASR offline via sherpa-onnx (modèle Whisper anglais). La saisie texte reste toujours
/// disponible en parallèle, donc le STT n'est jamais requis pour tester la boucle.
/// </summary>
public interface ISpeechToText
{
    /// <summary>Vrai si un moteur ASR est prêt (modèle chargé).</summary>
    bool IsAvailable { get; }

    /// <summary>Début de capture (appui push-to-talk).</summary>
    void StartListening();

    /// <summary>Fin de capture (relâchement) -> texte transcrit (vide si indisponible).</summary>
    Task<string> StopAndTranscribeAsync(CancellationToken ct = default);
}

/// <summary>STT inerte : indisponible (mode texte uniquement) tant que l'ASR n'est pas branché.</summary>
public sealed class NullSpeechToText : ISpeechToText
{
    public bool IsAvailable => false;
    public void StartListening() { }
    public Task<string> StopAndTranscribeAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
}
