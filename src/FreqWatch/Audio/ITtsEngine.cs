namespace FreqWatch.Audio;

/// <summary>
/// Moteur de synthèse vocale : transforme du texte en audio PCM.
/// Implémentations : voix Windows (System.Speech, par défaut) et Piper (optionnel).
/// </summary>
public interface ITtsEngine
{
    /// <summary>Synthétise <paramref name="text"/> en un buffer PCM mono (voix des réglages).</summary>
    Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Synthétise avec une voix IMPOSÉE (copilote, contrôleur d'une fréquence donnée,
    /// équipage d'ambiance…). Les moteurs qui ne savent pas changer de voix retombent
    /// simplement sur la voix par défaut.
    /// </summary>
    Task<TtsAudio> SynthesizeAsync(string text, TtsVoice voice, CancellationToken ct = default)
        => SynthesizeAsync(text, ct);

    /// <summary>Voix disponibles pour ce moteur (pour les réglages).</summary>
    IReadOnlyList<string> GetVoices();
}

/// <summary>
/// Voix demandée pour une synthèse. <c>Name</c> null = voix par défaut des réglages.
/// <c>SpeakerId</c> n'a d'effet que sur les modèles multi-locuteurs (borné automatiquement).
/// <c>SpeedScale</c> module légèrement le débit : c'est ce qui différencie deux « personnes »
/// quand une seule voix est installée.
/// </summary>
public readonly record struct TtsVoice(string? Name = null, int SpeakerId = 0, float SpeedScale = 1f)
{
    public static readonly TtsVoice Default = new();
}
