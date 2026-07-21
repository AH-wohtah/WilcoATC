using FreqWatch.Atc;
using FreqWatch.Audio;

namespace FreqWatch.Settings;

public enum TtsEngineKind { Sherpa, Windows, Google }
public enum LlmMode { Off, Ollama, Cloud }

/// <summary>Réglages persistants (sérialisés en JSON dans %APPDATA%\FreqWatch).</summary>
public sealed class AppSettings
{
    // --- Langue de l'INTERFACE ---
    // Code de langue de l'app (ex. "en", "de", ou un code externe téléchargé). Ne concerne
    // QUE l'habillage : l'ATC, le copilote et le trafic ambiant parlent toujours anglais
    // (phraséologie OACI). Anglais par défaut.
    public string AppLanguage { get; set; } = "en";

    // --- ATC ---
    public bool AtcEnabled { get; set; } = true;
    public bool AtcAutoContact { get; set; } = true;

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

    // --- Immersion : copilote virtuel (annonces / callouts) ---
    public bool CopilotEnabled { get; set; }
    // Vitesses de référence pour les annonces de décollage (kt). Réglables : SimConnect
    // n'expose pas de V1/VR/V2 fiables selon les avions.
    public int CopilotV1Knots { get; set; } = 135;
    public int CopilotVrKnots { get; set; } = 140;
    public int CopilotV2Knots { get; set; } = 148;
    // Annoncer aussi les checklists aux transitions de phase.
    public bool CopilotChecklists { get; set; } = true;
    // Voix dédiée au copilote (null = voix par défaut des réglages TTS).
    public string? CopilotVoiceName { get; set; }

    // --- Push-to-talk (reconnaissance vocale) ---
    // Touche GLOBALE maintenue pour parler (fonctionne même quand MSFS a le focus).
    // 0 = désactivé. Code de touche virtuelle Windows (VK).
    public int PttVirtualKey { get; set; }
    public string PttKeyName { get; set; } = "";

    // --- Immersion : trafic radio ambiant (« des gens qui parlent ») ---
    public bool ChatterEnabled { get; set; }
    public int ChatterMinGapSeconds { get; set; } = 25;
    public int ChatterMaxGapSeconds { get; set; } = 70;

    // --- Immersion : packs de sons de cabine ---
    public bool CabinEnabled { get; set; }
    public string? CabinPackName { get; set; }          // null = premier pack trouvé
    public double CabinVolume { get; set; } = 0.6;
    public string? CabinPacksDir { get; set; }          // null = %LOCALAPPDATA%\FreqWatch\cabin

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
