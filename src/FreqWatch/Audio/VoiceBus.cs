using FreqWatch.Settings;

namespace FreqWatch.Audio;

/// <summary>
/// Canal VOIX unique et sérialisé. <see cref="RadioAudioPipeline"/> est exclusive (chaque
/// lecture coupe la précédente) : tout ce qui parle — ATC, copilote, trafic ambiant — passe
/// donc par ici pour ne jamais se couper mutuellement.
///
/// <see cref="SpeakAsync"/> prend un délai d'attente du canal :
///  • <c>TimeSpan.Zero</c> : on abandonne si occupé (annonces non critiques, ambiance) ;
///  • une durée : on met en file (réponses ATC, transferts).
/// </summary>
public sealed class VoiceBus : IDisposable
{
    private readonly RadioAudioPipeline _pipeline;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VoiceBus(RadioAudioPipeline pipeline, SettingsService settings)
    {
        _pipeline = pipeline;
        _settings = settings;
    }

    /// <summary>Vrai si une voix est en cours de diffusion.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Joue une voix. Renvoie false si le canal était occupé (et n'a pas été attendu).</summary>
    public async Task<bool> SpeakAsync(TtsAudio audio, RadioProfile profile, TimeSpan wait, CancellationToken ct = default)
    {
        if (audio.IsEmpty) return false;

        bool acquired;
        try { acquired = await _gate.WaitAsync(wait, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        if (!acquired) return false;

        try
        {
            await _pipeline.PlayAsync(audio, _settings.Current.OutputDeviceNumber, profile, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Coupe la voix en cours (la pipeline appartient à la composition, pas au bus).</summary>
    public void Stop() => _pipeline.Stop();

    public void Dispose() => _gate.Dispose();
}
