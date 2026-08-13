using WilcoATC.Settings;

namespace WilcoATC.Audio;

/// <summary>
/// Choisit le moteur TTS selon les réglages, à chaque synthèse.
/// Défaut = **sherpa-onnx** (Piper natif). Si le moteur choisi échoue (voix non
/// installée, erreur), on retombe automatiquement sur la voix Windows (SAPI) :
/// la boucle vocale ne casse jamais.
/// </summary>
public sealed class TtsEngineSelector : ITtsEngine, IDisposable
{
    private readonly SettingsService _settings;
    private readonly WindowsTtsEngine _windows;
    private readonly SherpaOnnxTtsEngine _sherpa;
    private readonly GoogleCloudTtsEngine _google;

    private readonly VoiceRepository _voices;

    /// <summary>
    /// Le moteur CHOISI peut-il réellement parler ?
    ///
    /// C'est la question que personne ne posait. Sans voix neuronale installée, la synthèse
    /// échouait et l'application basculait EN SILENCE sur celle de Windows — dont la voix par
    /// défaut suit la langue du système. Un utilisateur français entendait donc un contrôleur
    /// français sans avoir rien téléchargé, sans le moindre avertissement, et en croyant
    /// écouter la voix du logiciel.
    ///
    /// Windows reste un choix LÉGITIME quand il est explicite : ce qu'il ne doit plus être,
    /// c'est une substitution muette à un moteur qu'on a demandé et qui n'est pas là.
    /// </summary>
    public bool IsReady => _settings.Current.TtsEngine switch
    {
        TtsEngineKind.Sherpa => _voices.HasAnyVoice(),
        TtsEngineKind.Google => !string.IsNullOrWhiteSpace(_settings.Current.GoogleApiKeyEnvVar),
        _ => true,   // Windows : toujours disponible, c'est le propre du moteur du système
    };

    public TtsEngineSelector(SettingsService settings, VoiceRepository voices)
    {
        _settings = settings;
        _voices = voices;
        _windows = new WindowsTtsEngine(() => settings.Current.WindowsVoice);
        _sherpa = new SherpaOnnxTtsEngine(voices,
            () => (settings.Current.SherpaVoiceName, (float)settings.Current.SherpaSpeed, settings.Current.SherpaSpeakerId));
        _google = new GoogleCloudTtsEngine(() => (settings.Current.GoogleApiKeyEnvVar, settings.Current.GoogleVoiceName));
    }

    public IReadOnlyList<string> GetVoices() => _settings.Current.TtsEngine switch
    {
        TtsEngineKind.Sherpa => _sherpa.GetVoices(),
        TtsEngineKind.Google => _google.GetVoices(),
        _ => _windows.GetVoices(),
    };

    public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default)
        => SynthesizeAsync(text, TtsVoice.Default, ct);

    /// <summary>Précharge côté sherpa uniquement : c'est le seul moteur à charger un modèle.</summary>
    public void Preload(TtsVoice voice)
    {
        if (_settings.Current.TtsEngine == TtsEngineKind.Sherpa) _sherpa.Preload(voice);
    }

    public async Task<TtsAudio> SynthesizeAsync(string text, TtsVoice voice, CancellationToken ct = default)
    {
        ITtsEngine? preferred = _settings.Current.TtsEngine switch
        {
            TtsEngineKind.Sherpa => _sherpa,
            TtsEngineKind.Google => _google,
            _ => null, // Windows : moteur direct ci-dessous
        };

        // MOTEUR DEMANDÉ MAIS ABSENT : on ne parle pas. Se rabattre sur Windows donnerait une
        // voix du système — française sur un Windows français — que l'utilisateur prendrait
        // pour celle du logiciel. Mieux vaut un silence explicite, que la couche appelante
        // transforme en message « installez les voix ».
        if (preferred is not null && !IsReady)
        {
            Diagnostics.FileLog.Write(
                $"[voix] moteur « {_settings.Current.TtsEngine} » sélectionné mais aucun modèle installé : "
                + "transmission supprimée (pas de repli sur la voix de Windows).");
            return TtsAudio.Empty;
        }

        if (preferred is not null)
        {
            try
            {
                var audio = await preferred.SynthesizeAsync(text, voice, ct).ConfigureAwait(false);
                if (!audio.IsEmpty) return audio;
            }
            // Le repli subsiste pour un ÉCHEC PONCTUEL — modèle présent mais synthèse ratée.
            // Ce cas-là mérite une voix de secours ; l'absence totale de modèle, non.
            catch { /* repli Windows ci-dessous */ }
        }

        return await _windows.SynthesizeAsync(text, ct).ConfigureAwait(false);
    }

    public void Dispose() => _sherpa.Dispose();
}
