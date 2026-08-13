using WilcoATC.Atc;
using WilcoATC.Audio;

namespace WilcoATC.Settings;

public enum TtsEngineKind { Sherpa, Windows, Google }

/// <summary>Choix utilisateur des règles de vol : déduction automatique, ou choix imposé.</summary>
public enum FlightRulesMode { Auto, ForceVfr, ForceIfr }

/// <summary>
/// Jeu d'annonces du copilote. <see cref="Auto"/> le déduit du gabarit de l'appareil (un
/// piston léger vole à vue), les deux autres l'imposent — un pilote de ligne s'entraînant
/// sur un Cessna veut ses callouts habituels, et l'inverse se rencontre tout autant.
/// </summary>
public enum CopilotRulesMode { Auto, ForceVfr, ForceIfr }


/// <summary>Réglages persistants (sérialisés en JSON dans %APPDATA%\WilcoATC).</summary>
public sealed class AppSettings
{
    // --- Langue de l'INTERFACE ---
    // Code de langue de l'app (ex. "en", "de", ou un code externe téléchargé). Ne concerne
    // QUE l'habillage : l'ATC, le copilote et le trafic ambiant parlent toujours anglais
    // (phraséologie OACI). Anglais par défaut.
    public string AppLanguage { get; set; } = "en";

    // --- Premier lancement ---
    // Faux tant que l'assistant n'a pas été mené à son terme. C'est la SEULE condition
    // d'affichage : un fichier de réglages absent donne donc bien un premier lancement,
    // et l'utilisateur peut relancer l'assistant depuis les réglages en le remettant à faux.
    public bool OnboardingCompleted { get; set; }

    // Assistant de PREMIÈRE CONFIGURATION (langue, téléchargement des voix + reconnaissance,
    // push-to-talk clavier/manette). Ne s'affiche qu'au tout premier lancement, une seule fois.
    public bool SetupCompleted { get; set; }

    // --- ATC ---
    public bool AtcEnabled { get; set; } = true;
    public bool AtcAutoContact { get; set; } = true;

    // --- ATIS ---
    // Quand ON : se caler sur une fréquence ATIS déclenche la lecture EN BOUCLE du bulletin
    // du terrain (météo du simulateur), jusqu'à ce qu'on quitte la fréquence.
    public bool AtisEnabled { get; set; } = true;
    // Silence entre deux passages du bulletin (secondes).
    public int AtisRepeatGapSeconds { get; set; } = 8;

    // --- Langue du contrôleur ---
    // RETIRÉE POUR L'INSTANT : l'ATC parle et comprend l'anglais, un point c'est tout (voir
    // LanguageResolver.Effective). Le réglage reviendra avec le multilingue ; l'exposer
    // aujourd'hui donnerait un choix sans effet.

    // --- Règles de vol (VFR / IFR) ---
    // En AUTO, les règles sont déduites du plan de vol chargé puis, à défaut, du gabarit de
    // l'appareil (un piston léger vole en VFR). Les deux autres valeurs forcent le choix :
    // l'utilisateur a toujours le dernier mot sur une déduction.
    public FlightRulesMode FlightRules { get; set; } = FlightRulesMode.Auto;

    // --- Source des fréquences ---
    // Le SIMULATEUR fait toujours foi quand il répond pour un terrain. Ce réglage décide de
    // ce qui se passe quand il ne répond PAS (terrain absent de la navdata, hors ligne,
    // réponse pas encore arrivée) :
    //
    //  • true  : on complète avec les fréquences RÉELLES (OurAirports). Meilleure couverture,
    //            mais ces données sont incomplètes et parfois divergentes — l'ATC peut citer
    //            une fréquence introuvable dans le simulateur ;
    //  • false : on ne cite QUE ce que le simulateur publie. Tout ce que dit l'ATC est
    //            affichable et syntonisable dans le jeu, au prix de terrains sans fréquence.
    //
    // Les corrections IMPORTÉES par l'utilisateur restent prioritaires dans les deux cas :
    // ce sont des données validées, pas un repli.
    public bool UseRealWorldFrequencies { get; set; } = true;

    // --- Réactivité de l'ATC ---
    // Pause avant chaque transmission, en millisecondes : le « temps de réflexion » du
    // contrôleur. Elle se déroule EN PARALLÈLE de la synthèse vocale (voir SpeakRawAsync),
    // donc la réponse coûte le plus long des deux, pas leur somme. 0 = aussi vite que la
    // voix est prête.
    public int AtcResponseDelayMs { get; set; } = 200;

    // --- Collationnement exigé ---
    // Quand ON : après une clairance, le contrôleur RÉCLAME le collationnement si le pilote
    // se tait (deux relances, puis il renonce), et refuse un collationnement incomplet — un
    // « roger » ne vaut pas relecture d'une piste, d'un squawk ou d'une fréquence.
    public bool RequireReadback { get; set; } = true;

    // Après TROIS appels sans réponse, le contrôleur conclut à une panne radio : il demande
    // le squawk 7600, invite à poursuivre selon le plan de vol, et cesse de réclamer. Rien
    // d'autre ne se produit — ni défense aérienne, ni interception.
    public bool ReadbackRadioFailureCall { get; set; } = true;

    // --- Interception (chasseur dans le simulateur) ---
    // Après l'annonce de panne radio, un appareil de chasse est CRÉÉ dans le simulateur et
    // vient escorter le joueur. C'est la seule fonction de l'application qui écrit dans le
    // monde du simu ; elle est donc désactivée par défaut.
    public bool InterceptorEnabled { get; set; }

    // Titre de conteneur de l'appareil (celui de l'aircraft.cfg). Vide = on essaie les
    // titres connus du F/A-18E, l'un après l'autre (voir InterceptDirector.DefaultTitles).
    public string InterceptorTitle { get; set; } = "";

    // Durée de l'escorte avant que le chasseur ne s'en aille (secondes).
    public int InterceptorSeconds { get; set; } = 90;

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

    // NB : le vol saisi à la main dans l'assistant N'EST PAS persisté. C'est une donnée de
    // SESSION (comme un plan SimBrief) : elle disparaît à la fermeture, et l'assistant repart
    // vide au lancement suivant.

    // --- Immersion : copilote virtuel (annonces / callouts) ---
    public bool CopilotEnabled { get; set; }
    // Vitesses de référence pour les annonces de décollage (kt). Réglables : SimConnect
    // n'expose pas de V1/VR/V2 fiables selon les avions.
    public int CopilotV1Knots { get; set; } = 135;
    public int CopilotVrKnots { get; set; } = 140;
    public int CopilotV2Knots { get; set; } = 148;
    // Annoncer aussi les checklists aux transitions de phase.
    public bool CopilotChecklists { get; set; } = true;

    // Jeu d'annonces : VFR (aviation légère) ou IFR (ligne). AUTO le déduit du gabarit —
    // c'est ce que faisait l'application sans le dire ; on rend simplement la main.
    //
    //  • VFR : « airspeed alive », rotation, hauteurs annoncées sans le mot « minimums »,
    //          ni V1/V2, ni spoilers, ni inverseurs — rien de tout cela n'existe à bord ;
    //  • IFR : les callouts de ligne au complet (80 nœuds, V1, V2, spoilers, reverse…).
    public CopilotRulesMode CopilotRules { get; set; } = CopilotRulesMode.Auto;
    // Voix dédiée au copilote (null = voix par défaut des réglages TTS).
    public string? CopilotVoiceName { get; set; }

    // --- Push-to-talk (reconnaissance vocale) ---
    // Touche GLOBALE maintenue pour parler (fonctionne même quand MSFS a le focus).
    // 0 = désactivé. Code de touche virtuelle Windows (VK).
    public int PttVirtualKey { get; set; }
    public string PttKeyName { get; set; } = "";

    // Variante JOYSTICK/HOTAS : bouton global maintenu pour parler. Utilisable EN PLUS de la
    // touche clavier (l'un ou l'autre déclenche). Device = index périphérique, Button = n°
    // (1-based). Button < 1 = désactivé.
    public int PttJoystickDevice { get; set; } = -1;
    public int PttJoystickButton { get; set; }
    public string PttJoystickName { get; set; } = "";

    // --- Immersion : trafic radio ambiant (« des gens qui parlent ») ---
    public bool ChatterEnabled { get; set; }
    public int ChatterMinGapSeconds { get; set; } = 25;
    public int ChatterMaxGapSeconds { get; set; } = 70;

    // --- Immersion : le contrôle parle AU TRAFIC RÉEL du simulateur ---
    //
    // Différent du trafic d'ambiance ci-dessus, qui invente des échanges. Ici les indicatifs,
    // les pistes et les instants sont ceux d'appareils réellement présents : quand la tour
    // autorise un appareil à atterrir, il est bel et bien en finale sur cette piste, et on le
    // voit se poser. Ne fonctionne qu'avec du trafic dans le simulateur (le sien ou celui d'un
    // injecteur), et ne parle que sur une fréquence de tour.
    public bool TrafficAtcEnabled { get; set; }

    // --- Injection de trafic : faire NAÎTRE des appareils là où il n'y en a pas ---
    //
    // Les appareils créés reçoivent un plan de vol et sont pilotés PAR LE SIMULATEUR : ils
    // roulent, décollent et atterrissent pour de bon. Utile là où le trafic est rare — un
    // aérodrome tranquille reste tranquille, quel que soit l'injecteur installé.
    public bool TrafficInjectionEnabled { get; set; }

    /// <summary>Nombre d'appareils injectés visés. Au-delà d'une vingtaine, le simulateur souffre.</summary>
    public int TrafficInjectionCount { get; set; } = 8;

    // --- Immersion : packs de sons de cabine ---
    public bool CabinEnabled { get; set; }
    public string? CabinPackName { get; set; }          // null = premier pack trouvé
    public double CabinVolume { get; set; } = 0.6;
    public string? CabinPacksDir { get; set; }          // null = %LOCALAPPDATA%\WilcoATC\cabin

    // --- Audio de sortie ---
    public int OutputDeviceNumber { get; set; } = -1; // -1 = périphérique par défaut

    // --- Reconnaissance vocale (STT) ---
    public int InputDeviceNumber { get; set; } = -1; // -1 = micro par défaut
    // Dossier des modèles ASR (null = %LOCALAPPDATA%\WilcoATC\asr).
    public string? SttModelsDir { get; set; }

    // --- TTS ---
    // Moteur par défaut : sherpa-onnx (Piper natif, offline, sans clé).
    public TtsEngineKind TtsEngine { get; set; } = TtsEngineKind.Sherpa;
    public string? WindowsVoice { get; set; }

    // sherpa-onnx : dossier des voix (null = %LOCALAPPDATA%\WilcoATC\voices),
    // nom de la voix sélectionnée (null = voix par défaut), vitesse et locuteur.
    public string? SherpaVoicesDir { get; set; }
    public string? SherpaVoiceName { get; set; }
    public double SherpaSpeed { get; set; } = 1.0;
    public int SherpaSpeakerId { get; set; }

    // Google Cloud TTS (BYOK) : la clé est lue dans cette variable d'environnement. Un
    // settings.json antérieur au renommage y garde l'ancien nom : le moteur lit alors aussi
    // WILCOATC_GOOGLE_KEY en repli (voir GoogleCloudTtsEngine).
    public string GoogleApiKeyEnvVar { get; set; } = Audio.GoogleCloudTtsEngine.DefaultKeyEnvVar;
    public string GoogleVoiceName { get; set; } = "en-US-Neural2-D";

    // Le LLM (Ollama / cloud) a été SUPPRIMÉ, code compris. Il était interrogé à chaque
    // transmission — reconnaissance d'intention, génération de phrase, repli conversationnel —
    // et l'application attendait sa réponse : plusieurs secondes de latence pour un résultat
    // que la voie déterministe produit en une milliseconde. Les anciennes clés (« Llm »,
    // « OllamaUrl »…) qui traînent dans un settings.json sont simplement ignorées à la
    // lecture, puis disparaissent à la première sauvegarde.

    // --- Effet radio ---
    public bool RadioBandPass { get; set; } = true;
    public bool RadioSquelch { get; set; } = true;
    public bool RadioSaturation { get; set; } = true;
    public double RadioVolume { get; set; } = 0.9;

    // Intensité globale de l'effet radio (0 = presque propre, 1 = très marqué). Un seul
    // curseur : bande passante, souffle et saturation vont ensemble à l'oreille.
    public double RadioIntensity { get; set; } = 0.5;

    // Dossier des ÉCHANTILLONS radio réels (null = %LOCALAPPDATA%\WilcoATC\radio).
    // Fichiers keyup*.wav / breath*.wav / tail*.wav / bed*.wav ; voir RadioSampleRepository.
    public string? RadioSamplesDir { get; set; }

    // Niveau du fond sonore de la station émettrice (bed*.wav) sous la voix.
    public double RadioBedVolume { get; set; } = 0.35;

    public RadioProfile ToRadioProfile()
    {
        var p = RadioProfile.FromIntensity(RadioIntensity);
        p.BandPass = RadioBandPass;
        p.Squelch = RadioSquelch;
        p.Saturation = RadioSaturation;
        p.Volume = RadioVolume;
        p.BedVolume = RadioBedVolume;
        return p;
    }
}
