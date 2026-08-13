namespace WilcoATC.Audio;

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

    /// <summary>
    /// Ce moteur peut-il RÉELLEMENT parler en l'état ?
    ///
    /// Faux quand le moteur choisi attend un modèle qui n'est pas installé. La couche
    /// appelante s'en sert pour se TAIRE et le dire, au lieu de laisser une voix de secours
    /// prendre la place à l'insu de l'utilisateur — qui la croirait alors sienne.
    ///
    /// Vrai par défaut : un moteur sans modèle à télécharger est toujours prêt.
    /// </summary>
    bool IsReady => true;

    /// <summary>
    /// Charge le modèle d'une voix À L'AVANCE, sans rien synthétiser.
    ///
    /// POURQUOI : charger un modèle Piper coûte environ une demi-seconde, payée par la
    /// PREMIÈRE phrase de cette voix — donc en plein échange, juste après que le pilote a
    /// parlé, là où c'est le plus visible. Préchargé au changement de fréquence (pendant
    /// que personne n'attend), ce coût disparaît de la réponse.
    ///
    /// Sans effet par défaut : les moteurs sans modèle à charger n'ont rien à faire.
    /// </summary>
    void Preload(TtsVoice voice) { }
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
