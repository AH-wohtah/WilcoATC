namespace FreqWatch.Audio;

/// <summary>
/// Moteur de synthèse vocale : transforme du texte en audio PCM.
/// Implémentations : voix Windows (System.Speech, par défaut) et Piper (optionnel).
/// </summary>
public interface ITtsEngine
{
    /// <summary>Synthétise <paramref name="text"/> en un buffer PCM mono.</summary>
    Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default);

    /// <summary>Voix disponibles pour ce moteur (pour les réglages).</summary>
    IReadOnlyList<string> GetVoices();
}
