using WilcoATC.Settings;

namespace WilcoATC.Audio;

/// <summary>
/// Canal VOIX unique et sérialisé. <see cref="RadioAudioPipeline"/> est exclusive (chaque
/// lecture coupe la précédente) : tout ce qui parle — ATC, copilote, trafic ambiant — passe
/// donc par ici pour ne jamais se couper mutuellement.
///
/// <see cref="SpeakAsync"/> prend un délai d'attente du canal :
///  • <c>TimeSpan.Zero</c> : on abandonne si occupé (annonces non critiques, ambiance) ;
///  • une durée : on met en file (réponses ATC, transferts).
/// </summary>
/// <summary>
/// Qui a la priorité sur le canal. Un contrôleur qui répond à une question ne doit pas
/// attendre la fin d'un « positive rate » ou d'un échange d'ambiance : dans la réalité,
/// c'est le trafic de fond qui s'efface, jamais l'inverse.
/// </summary>
public enum VoicePriority
{
    Ambient = 0,   // trafic radio d'ambiance
    Copilot = 1,   // annonces du copilote
    Atc = 2,       // le contrôleur : rien ne passe devant
}

public sealed class VoiceBus : IDisposable
{
    private readonly RadioAudioPipeline _pipeline;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Priorité de ce qui joue en ce moment (lu sans verrou : simple indication).</summary>
    private volatile int _playing = (int)VoicePriority.Ambient;

    public VoiceBus(RadioAudioPipeline pipeline, SettingsService settings)
    {
        _pipeline = pipeline;
        _settings = settings;
    }

    /// <summary>Vrai si une voix est en cours de diffusion.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Joue une voix. Renvoie false si le canal était occupé (et n'a pas été attendu).</summary>
    public async Task<bool> SpeakAsync(TtsAudio audio, RadioProfile profile, TimeSpan wait,
                                       CancellationToken ct = default,
                                       VoicePriority priority = VoicePriority.Atc)
    {
        if (audio.IsEmpty) return false;

        // PRÉEMPTION : ce qui joue est moins important que ce qu'on veut dire -> on le coupe
        // au lieu d'attendre sa fin. Sans ça, une réponse du contrôleur pouvait rester en
        // file derrière plusieurs secondes de callout copilote ou de trafic d'ambiance — et
        // c'est exactement ce qui se ressent comme « l'ATC met cinq secondes à répondre ».
        if (_gate.CurrentCount == 0 && (int)priority > _playing) _pipeline.Stop();

        var waited = System.Diagnostics.Stopwatch.StartNew();
        bool acquired;
        try { acquired = await _gate.WaitAsync(wait, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        if (!acquired) return false;
        waited.Stop();

        // Une attente longue est la cause n°1 d'une réponse qui paraît lente, et la seule
        // qu'on ne puisse pas déduire des durées de synthèse : on la journalise.
        if (waited.ElapsedMilliseconds > 300)
            Diagnostics.FileLog.Write(
                $"[voix] canal occupé {waited.ElapsedMilliseconds} ms avant de parler ({priority}).");

        _playing = (int)priority;
        try
        {
            await _pipeline.PlayAsync(audio, _settings.Current.OutputDeviceNumber, profile, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _playing = (int)VoicePriority.Ambient;
            _gate.Release();
        }
    }

    /// <summary>Coupe la voix en cours (la pipeline appartient à la composition, pas au bus).</summary>
    public void Stop() => _pipeline.Stop();

    public void Dispose() => _gate.Dispose();
}
