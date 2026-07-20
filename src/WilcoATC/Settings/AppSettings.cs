using FreqWatch.Atc;
using FreqWatch.Audio;

namespace FreqWatch.Settings;

public enum TtsEngineKind { Sherpa, Windows, Google }
public enum LlmMode { Off, Ollama, Cloud }

/// <summary>Réglages persistants (sérialisés en JSON dans %APPDATA%\FreqWatch).</summary>
public sealed class AppSettings
{
    // --- Langue (interface + ATC) ---
    // Code de langue de l'app (ex. "en", "fr", ou un code externe téléchargé). Pilote À LA FOIS
    // l'interface ET la langue parlée/reconnue de l'ATC. Anglais par défaut.
    public string AppLanguage { get; set; } = "en";

    // --- ATC ---
    public bool AtcEnabled { get; set; } = true;
    public bool AtcAutoContact { get; set; } = true;
    public AtcLanguage AtcLanguage { get; set; } = AtcLanguage.Auto; // (hérité) désormais dérivé d'AppLanguage

    // --- Mode Test (débogage) ---
    // Quand ON : le cerveau ACCEPTE toute requête quelle que soit la phase / le contrôleur
    // (court-circuite la validation, sans la supprimer), et l'avertissement « décollé sans
    // autorisation » est supprimé. Par défaut DÉSACTIVÉ (comportement strict normal).
    public bool TestMode { get; set; }

    // --- Intégration GSX (pushback) ---
    public bool GsxIntegrationEnabled { get; set; }

    // --- Transferts de fréquence / Centre ---
    // VATSIM : si en ligne, on cherche la vraie fréquence de Centre (sinon approximatif).
    public bool VatsimEnabled { get; set; }
    // Fréquence de Centre APPROXIMATIVE (MHz) utilisée hors-ligne, faute de dataset fiable.
    public double CenterFrequencyMhz { get; set; } = 132.0;
    // Nom parlé du Centre (ex. « Geneva » -> « Geneva Center »). Vide = « Center » générique.
    public string CenterName { get; set; } = "";

    // --- Plan de vol / SimBrief ---
    public string SimBriefUsername { get; set; } = "";
    // Altitude initiale de la clairance de départ (SimBrief ne fournit que la croisière).
    public int DefaultInitialClimbFeet { get; set; } = 5000;

    // --- Audio de sortie ---
    public int OutputDeviceNumber { get; set; } = -1; // -1 = périphérique par défaut

    // --- Reconnaissance vocale (STT) ---
    public int InputDeviceNumber { get; set; } = -1; // -1 = micro par défaut
    // Dossier des modèles ASR (null = %LOCALAPPDATA%\FreqWatch\asr).
    public string? SttModelsDir { get; set; }

    // --- TTS ---
    // Moteur par défaut : sherpa-onnx (Piper natif, offline, sans clé).
    public TtsEngineKind TtsEngine { get; set; } = TtsEngineKind.Sherpa;
    public string? WindowsVoice { get; set; }

    // sherpa-onnx : dossier des voix (null = %LOCALAPPDATA%\FreqWatch\voices),
    // nom de la voix sélectionnée (null = voix par défaut), vitesse et locuteur.
    public string? SherpaVoicesDir { get; set; }
    public string? SherpaVoiceName { get; set; }
    public double SherpaSpeed { get; set; } = 1.0;
    public int SherpaSpeakerId { get; set; }

    // Google Cloud TTS (BYOK) : la clé est lue dans cette variable d'environnement.
    public string GoogleApiKeyEnvVar { get; set; } = "FREQWATCH_GOOGLE_KEY";
    public string GoogleVoiceName { get; set; } = "en-US-Neural2-D";

    // --- LLM (optionnel, désactivé par défaut) ---
    public LlmMode Llm { get; set; } = LlmMode.Off;
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.2";
    public string CloudBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string CloudModel { get; set; } = "gpt-4o-mini";
    public string CloudApiKeyEnvVar { get; set; } = "FREQWATCH_LLM_KEY";

    // --- Effet radio ---
    public bool RadioBandPass { get; set; } = true;
    public bool RadioHiss { get; set; } = true;
    public bool RadioSquelch { get; set; } = true;
    public bool RadioSaturation { get; set; } = true;
    public double RadioVolume { get; set; } = 0.9;

    public RadioProfile ToRadioProfile() => new()
    {
        BandPass = RadioBandPass,
        Hiss = RadioHiss,
        Squelch = RadioSquelch,
        Saturation = RadioSaturation,
        Volume = RadioVolume,
    };
}
