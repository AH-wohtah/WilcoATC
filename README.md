# WilcoATC

A **Windows desktop application (WPF, .NET 8)** that connects to **Microsoft Flight
Simulator** (2024, compatible with 2020) through **SimConnect** and gives you a **talking
air traffic controller** over the top of the game: it hears you on push-to-talk,
understands standard phraseology, answers in a radio voice, and calls you on its own
initiative throughout the flight.

Everything it needs to do that runs **on your machine and for free** — speech recognition,
speech synthesis and the controller's logic are all local. Cloud voices and VATSIM lookups
exist, but they are options you switch on, never requirements.

---

## Contents

- [What it does](#what-it-does)
- [Requirements](#requirements)
- [The SimConnect DLLs](#the-simconnect-dlls)
- [Build and run](#build-and-run)
- [Architecture](#architecture)
- [Radio frequencies: why hertz](#radio-frequencies-why-hertz)
- [Change detection (the CHANGED flag)](#change-detection-the-changed-flag)
- [The voice loop](#the-voice-loop)
  - [Radio effect: one intensity slider](#radio-effect-one-intensity-slider)
  - [Radio sound effects: real samples](#radio-sound-effects-real-samples)
  - [Speech recognition (ASR)](#speech-recognition-asr)
  - [Airline callsigns, both ways](#airline-callsigns-both-ways)
  - [First-run wizard](#first-run-wizard)
- [Understanding what the pilot says](#understanding-what-the-pilot-says)
- [SimBrief flight plan and realistic callsigns](#simbrief-flight-plan-and-realistic-callsigns)
- [Proactive ATC and controller handoffs](#proactive-atc-and-controller-handoffs)
- [Ambient life: chatter, traffic, cabin](#ambient-life-chatter-traffic-cabin)
- [GSX integration (pushback)](#gsx-integration-pushback)
- [Robustness and reconnection](#robustness-and-reconnection)
- [Where your data lives](#where-your-data-lives)
- [Troubleshooting](#troubleshooting)

---

## What it does

- **Talking controller.** Press your push-to-talk key, say a request in standard
  phraseology, and the controller answers in a radio voice — clearance, pushback, taxi,
  takeoff, altitude changes, approach, landing.
- **It talks first, too.** Frequency handoffs, approach and landing calls, a reminder if
  you take off without a clearance: the controller drives the flight, you are not expected
  to ask for everything.
- **Read-backs are checked.** Runway, squawk, frequency and altitude have to be read back;
  a bare "roger" does not count, and the controller repeats the item you missed.
- **Radio panel.** COM1 and COM2, **active** frequency (large, amber) and **standby**
  (smaller, cyan), with a **TX** lamp on whichever radio is transmitting.
- **Aircraft and flight panels.** Current aircraft, registration, nearest airport, IAS, GS,
  V/S, MSL and AGL altitude, heading, squawk, ground/air state, latitude and longitude.
- **Timestamped live log**, newest line on top: `14:32:07 — COM1 ACTIVE → 118.700`.
- **Ambient life** (all optional): background radio chatter, a copilot calling out phases,
  cabin sound packs, and traffic injection for empty airfields.

## Requirements

- **Windows 10/11 x64**.
- **.NET 8 SDK** (LTS) — https://dotnet.microsoft.com/download/dotnet/8.0
  (or **Visual Studio 2022** 17.8+ with the *.NET Desktop* workload).
- **Microsoft Flight Simulator 2020 or 2024** installed, for a real test.
- The **SimConnect DLLs** (managed + native) — see below.
- **NuGet packages** (free, restored on first build): `NAudio` and `NAudio.Vorbis` (audio),
  `System.Speech` (fallback voice), `org.k2fsa.sherpa.onnx` (native neural TTS and ASR),
  `SharpCompress` (voice-model extraction).

## The SimConnect DLLs

Two DLLs are needed, and both are **already bundled** in [`libs/`](libs/), so the solution
builds as it stands:

| File | Role | How it is used |
|---|---|---|
| `Microsoft.FlightSimulator.SimConnect.dll` | **Managed** .NET wrapper | Referenced by the project (`<Reference>` + `<HintPath>`) |
| `SimConnect.dll` | **Native x64** library | Copied **next to the executable** (otherwise `DllNotFoundException`) |

**Getting them yourself** (official source: the MSFS SDK) — see
[`libs/README.txt`](libs/README.txt). In short: enable the SDK in MSFS
(Options → General → Developers), install the Core SDK, then take both files from
`<MSFS SDK>\SimConnect SDK\lib\`.

The `.csproj` wires both automatically:

```xml
<Reference Include="Microsoft.FlightSimulator.SimConnect">
  <HintPath>..\..\libs\Microsoft.FlightSimulator.SimConnect.dll</HintPath>
  <Private>true</Private>
</Reference>
<None Include="..\..\libs\SimConnect.dll">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

> The project **targets x64** (`<Platforms>x64</Platforms>`) because native SimConnect is
> 64-bit. Building AnyCPU or x86 raises `BadImageFormatException`.

## Build and run

**Command line:**

```powershell
cd WilcoATC
dotnet build -c Release
dotnet run --project src\WilcoATC -c Release
```

<<<<<<< HEAD
**Visual Studio:**

1. Open `WilcoATC.sln`.
2. Check that the active platform is **x64**.
=======
**Dans Visual Studio :**
1. Ouvre `WilcoATC.sln`.
2. Vérifie que la plateforme active est **x64**.
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed
3. F5.

> ⚠️ **The simulator has to be running** for data to appear. With no sim, the app shows
> "Waiting for the simulator…" (pulsing amber lamp) and **retries in a loop**; as soon as
> MSFS starts and a flight is loaded, it connects on its own.

## Architecture

A clean split between the **SimConnect layer** and the **UI**: the UI never references a
SimConnect type, it only depends on the `ISimConnectService` interface and a few DTOs.

```
WilcoATC/
├─ WilcoATC.sln
<<<<<<< HEAD
├─ libs/                     SimConnect DLLs (managed + native)
├─ data/                     OurAirports CSVs, runways, seed title catalogue
└─ src/WilcoATC/
   ├─ App.xaml(.cs)          Composition root: builds every service and wires them
   ├─ MainWindow.xaml(.cs)   Cockpit UI (no SimConnect type in sight)
   ├─ OnboardingView / SetupView / RequirementsGate / VoiceDownloadWindow
=======
├─ libs/                     DLL SimConnect (managée + native)
├─ data/                     (stretch) CSV OurAirports, optionnels
└─ src/WilcoATC/
   ├─ App.xaml(.cs)          Composition root : crée le service + le VM, les câble
   ├─ MainWindow.xaml(.cs)   UI cockpit (aucun type SimConnect)
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed
   │
   ├─ Sim/                   ── SIMCONNECT LAYER (isolated, no WPF) ──
   │  ├─ ISimConnectService.cs    The contract the UI sees (the isolation seam)
   │  ├─ SimConnectService.cs     Connection, pump thread, reconnection, change detection
   │  ├─ NullSimConnectService.cs Inert service (designer preview, test benches)
   │  ├─ SimData.cs / SimIds.cs / SimEvents.cs   SimVar structs, enums, published DTOs
   │  └─ SimTitleCatalog.cs / SimTitleCollector.cs   Container titles seen in this install
   │
   ├─ Audio/                 ── VOICE PIPELINE (NAudio, no SimConnect) ──
   │  ├─ ITtsEngine / TtsAudio / VoiceBus       Synthesis and shared audio channel
   │  ├─ SherpaOnnxTtsEngine.cs   Default engine: Piper/VITS, native, offline
   │  ├─ WindowsTtsEngine.cs      SAPI fallback
   │  ├─ GoogleCloudTtsEngine.cs  Optional cloud voices (BYOK)
   │  ├─ RadioDsp.cs / RadioProfile.cs / RadioSampleRepository.cs   Radio effect
   │  └─ VoiceRepository / VoiceDownloader / VoicePicker
   │
   ├─ Atc/                   ── THE CONTROLLER ──
   │  ├─ AtcController.cs        The loop: hear → understand → decide → speak
   │  ├─ Brain/                  Rule table (atc-rules.json) and decisions
   │  ├─ Understanding/          Speech-to-text, normalisation, intent recognition
   │  ├─ Context/                Flight phase, flight rules, current controller
   │  ├─ Planning/               SimBrief import, flight plan, callsigns
   │  ├─ Atis/ Enroute/ GroundServices/ Intercept/ Vatsim/
   │  ├─ ControllerSequencer.cs  Which position you should be talking to
   │  ├─ ReadbackChecker.cs      What has to be read back, and what was missed
   │  └─ Localization/AtcPhrases.cs   Fixed phraseology, per language
   │
<<<<<<< HEAD
   ├─ Traffic/               Reading real traffic, and injecting some where there is none
   ├─ Immersion/             Chatter, copilot callouts, cabin sound packs
   ├─ Stations/              OurAirports resolver, runways, live sim frequencies
   ├─ Settings/ Localization/ Diagnostics/ Input/ Formatting/ Common/
   ├─ ViewModels/ Converters/ Themes/
   └─ Data/                  Embedded datasets (airline telephony)
=======
   ├─ Settings/              AppSettings + SettingsService (%APPDATA%\WilcoATC)
   ├─ Formatting/            FrequencyFormatter, TransponderFormatter, AircraftFormatter
   ├─ Common/Geo.cs          distance haversine partagée
   ├─ ViewModels/           MainViewModel, SettingsViewModel, RelayCommand, …
   ├─ Converters/           BoolToVisibility, LogKindToBrush
   ├─ Stations/             (stretch, isolé) résolveur OurAirports + mini-lecteur CSV
   ├─ SettingsWindow.xaml(.cs)  fenêtre de réglages
   └─ Themes/Cockpit.xaml   Palette et styles « avionique »
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed
```

**Subscribed data structures:**

```
RadioData      { Com1/2 Active/Standby (Hz), Com1/2 Transmit }   ← SIM_FRAME + CHANGED
ContextData    { Lat, Lon, AltMSL, AltAGL, HeadingTrue, IAS,     ← SECOND (1 Hz)
                 GroundSpeed, VerticalSpeed, OnGround, Squawk }
AircraftIdData { Title, AtcType, AtcModel, AtcId (strings) }      ← SECOND + CHANGED
```

`RadioData` and `ContextData` are `FLOAT64`; `AircraftIdData` uses **string** variables
(`STRING256/64/32`, marshalled with `ByValTStr` + `CharSet.Ansi`) and is only sent on
connection and whenever the aircraft changes (the `CHANGED` flag).

**Nearest airport** — through `SubscribeToFacilities(AIRPORT)`: the sim pushes the airports
in its cache (ICAO + coordinates) and we keep the closest one. **No external file is
required**; when the OurAirports CSVs are present, the ICAO code is enriched with the full
name (`LFPG · Paris Charles de Gaulle (3.2 km)`).

**Threading** — everything that touches SimConnect lives on a **dedicated thread**
("SimConnect-Pump") in event-based mode: SimConnect signals a `WaitHandle`, we call
`ReceiveMessage()`. Results are published as .NET events, and `MainViewModel` marshals them
onto the UI thread through the `Dispatcher`. **The UI never freezes.**

## Radio frequencies: why hertz

Frequencies are requested in **`Hz` (FLOAT64)**, not as `Frequency BCD16`.

- `Frequency BCD16` returns a BCD-encoded integer: tedious to decode, and **unable to
  represent 8.33 kHz spacing properly**.
- `Hz` returns an **exact integer number of hertz** (`118700000`): no BCD decoding, and
  enough precision for 8.33 kHz.

<<<<<<< HEAD
Display: `MHz = Hz ÷ 1 000 000`, rounded and formatted to **three decimals**, which covers
**25 kHz** (`118.700`, `121.500`) **and 8.33 kHz** (`118.305`) cleanly. The full reasoning
is in [`Formatting/FrequencyFormatter.cs`](src/WilcoATC/Formatting/FrequencyFormatter.cs).

The squawk (`TRANSPONDER CODE:1`, unit `BCO16`) is decoded nibble by nibble in
=======
Affichage : `MHz = Hz ÷ 1 000 000`, arrondi et formaté à **3 décimales** → couvre
proprement le **25 kHz** (`118.700`, `121.500`) **et le 8.33 kHz** (`118.305`).
Voir le commentaire détaillé dans [`Formatting/FrequencyFormatter.cs`](src/WilcoATC/Formatting/FrequencyFormatter.cs).

Le squawk (`TRANSPONDER CODE:1`, unité `BCO16`) est décodé quartet par quartet dans
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed
[`Formatting/TransponderFormatter.cs`](src/WilcoATC/Formatting/TransponderFormatter.cs).

## Change detection (the CHANGED flag)

The radio request uses `SIMCONNECT_PERIOD.SIM_FRAME` **with the
`SIMCONNECT_DATA_REQUEST_FLAG.CHANGED` flag**: the sim only sends an update **when a radio
value actually changes**. That is the frequency-change detection itself — instant (to the
frame) and with no wasted traffic.

Position does **not** use `CHANGED` (it changes constantly): it is requested at **1 Hz**
(`SECOND`). Keeping the two definitions separate stops aircraft movement from triggering
radio updates.

The service compares every field against the last known value to produce atomic log lines
(`COM1 ACTIVE → 118.700`, `COM2 TRANSMIT ON`, …).

## The voice loop

> ⚠️ **The voice plays over the game**, on an output device — nothing is injected into the
> MSFS sound engine (there is no clean way to do it), exactly like BeyondATC or
> SayIntentions.

<<<<<<< HEAD
End to end: `push-to-talk → speech recognition → intent → decision → text → TTS → radio
filter → sound`. Three decoupled interfaces, each replaceable:
=======
**Boucle de bout en bout** : `données de vol → transmission ATC (texte) → TTS → filtre
radio → son joué`. Trois interfaces découplées (dossiers [`Atc/`](src/WilcoATC/Atc/)
et [`Audio/`](src/WilcoATC/Audio/)), faciles à remplacer :
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed

| Interface | Role | Default (free, offline) | Option |
|---|---|---|---|
| `ISpeechToText` | Hear the pilot | **sherpa-onnx** (Parakeet, or Whisper) | — |
| `IAtcLineGenerator` / `AtcBrain` | Decide what to say | **Deterministic rule table** | — |
| `ITtsEngine` | Text → PCM | **sherpa-onnx** (Piper/VITS, native C#) | Google Cloud TTS (BYOK) · Windows SAPI (fallback) |

<<<<<<< HEAD
> **There is no LLM in this application any more.** It used to sit in front of both intent
> recognition and phrasing, and the app waited for its answer: several seconds of latency
> per transmission, for a result the deterministic path produces in about a millisecond.
> Old `Llm`/`Ollama` keys left in a `settings.json` are ignored on load and disappear on the
> next save.
=======
**Effet radio** ([`RadioDsp`](src/WilcoATC/Audio/RadioDsp.cs)) : passe-bande ~300–3000 Hz
(BiQuad), souffle de fond, **clic de squelch** à l'ouverture/fermeture, légère
saturation. Chaque étage est activable dans les réglages.
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed

**Triggers:**

<<<<<<< HEAD
- **Push-to-talk** — a key or a joystick button, captured globally (it works while MSFS has
  focus).
- **Automatic** — tuning a **known station** triggers the initial contact, once per station.
- **Manual** — the **▶ Test ATC** button, **F1** with the window focused, or the global
  **Ctrl+Alt+A** shortcut.
- A small **random delay** precedes each answer, so the controller does not reply like a
  machine.

### Voices: sherpa-onnx by default (native Piper, fully offline)

The default engine is **sherpa-onnx**
([`SherpaOnnxTtsEngine`](src/WilcoATC/Audio/SherpaOnnxTtsEngine.cs)): Piper/VITS running
**natively inside the .NET process** through the `org.k2fsa.sherpa.onnx` NuGet package.
**No Python, no `piper.exe`, no API key.** PCM is generated in memory and fed straight into
the radio pipeline.
=======
### Choisir le périphérique, la voix, activer le LLM
Bouton **« ⚙ Réglages »** (persistés dans `%APPDATA%\WilcoATC\settings.json`) :
- **Périphérique de sortie** : casque par défaut, ou un **câble virtuel** (VB-CABLE)
  pour une voie séparée du son du jeu.
- **Moteur/voix TTS** : **sherpa-onnx** (défaut, natif) ; Google (BYOK) ; Windows (secours).
- **LLM** : `Off` (templates), `Ollama` (local), `Cloud` (BYOK). Rien de configuré →
  **templates** ; le LLM n'est **jamais** obligatoire.
- **Effet radio** : bandes/souffle/squelch/saturation + volume.
- **Langue ATC** : English (phraséologie OACI) ou Français.

### Voix neuronale par défaut : sherpa-onnx (Piper natif, 100 % offline)
Le moteur par défaut est **sherpa-onnx** ([`SherpaOnnxTtsEngine`](src/WilcoATC/Audio/SherpaOnnxTtsEngine.cs)) :
Piper/VITS exécuté **nativement dans le process .NET** (package NuGet `org.k2fsa.sherpa.onnx`).
**Aucun Python, aucun `piper.exe`, aucune clé API.** Le PCM est généré en mémoire puis
passe dans le pipeline radio.

- **D'où vient la voix ?** Au **premier lancement sans voix installée**, l'app télécharge
  automatiquement (barre de progression) la voix par défaut
  **`vits-piper-en_US-ryan-medium`** depuis
  `https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-ryan-medium.tar.bz2`
  et l'extrait (pur managé, via SharpCompress + `System.Formats.Tar`).
- **Où sont stockées les voix ?** Dans **`%LOCALAPPDATA%\WilcoATC\voices\`** — un dossier
  par voix, contenant `*.onnx` + `tokens.txt` + `espeak-ng-data/` (format sherpa-onnx).
- **Ajouter une voix (dont des voix françaises)** : Réglages → **« Ajouter une voix »**
  propose un catalogue téléchargeable en un clic, incluant des voix **françaises** Piper
  (`fr_FR-siwis`, `fr_FR-tom`, `fr_FR-upmc`, `fr_FR-gilles`). La voix installée est
  sélectionnée automatiquement. Pour une expérience 100 % française, combinez-la avec
  Réglages → **Langue ATC = Français**.
- **Voix personnalisée** : décompressez n'importe quel modèle TTS sherpa-onnx
  (https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models) dans
  `%LOCALAPPDATA%\WilcoATC\voices\`, puis Réglages → **Dossier des voix** pour rafraîchir.
  Vitesse d'élocution réglable ; speaker id géré pour les modèles multi-locuteurs.
- **Modèle manquant / erreur** → repli automatique sur la **voix Windows** (SAPI), et le
  bouton « Télécharger la voix par défaut » (Réglages) relance l'installation.
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed

- **Where the voice comes from** — the [first-run wizard](#first-run-wizard) offers
  **`vits-piper-en_US-libritts-high`**, a **multi-speaker** model at the *high* tier, so
  several timbres for about 110 MB. Extraction is pure managed code (SharpCompress +
  `System.Formats.Tar`).
- **Where voices are stored** — `%LOCALAPPDATA%\WilcoATC\voices\`, one folder per voice
  holding `*.onnx` + `tokens.txt` + `espeak-ng-data/`.
- **Quality** — only the **medium** and **high** tiers are offered. The *low* tier was
  dropped: those models are sampled at 16 kHz instead of 22.05 kHz, which produces exactly
  the tinny timbre we are trying to avoid.
- **One voice per controller** — `VoicePicker` gives each station a **stable** timbre, and
  the **position type** takes part in the draw, so Ground and Tower at the same airport can
  never land on the same voice. Each position also carries a slight rate bias
  (Ground 0.96 · Center 0.98 · Approach 1.02 · Tower 1.06).
- **Custom voice** — unpack any sherpa-onnx TTS model
  (https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models) into
  `%LOCALAPPDATA%\WilcoATC\voices\`, then Settings → **Voices folder** to refresh. Speaking
  rate is adjustable and speaker ids are handled for multi-speaker models.
- **Missing model or error** → automatic fallback to the **Windows voice** (SAPI), and the
  *Download the default voice* button reinstalls it.

<<<<<<< HEAD
> The native DLLs (`sherpa-onnx-c-api.dll`, `onnxruntime.dll`) come from the NuGet package
> and are copied next to the executable (the project targets **win-x64**).
=======
### Voix Google (Google Cloud TTS, optionnel — BYOK)
Voix neuronales de très bonne qualité (WaveNet / Neural2 / Studio). **Palier gratuit
mensuel** Google, puis payant : c'est une option **BYOK**, jamais requise.
1. Console Google Cloud → activez l'API **Cloud Text-to-Speech** → créez une **clé API**.
2. Mettez la clé dans une **variable d'environnement** (défaut `WilcoATC_GOOGLE_KEY`) :
   `setx WilcoATC_GOOGLE_KEY "votre_clé"` (rouvrez l'app ensuite). La clé n'est
   **jamais** stockée dans l'application.
3. Réglages → moteur **Google**, choisissez une voix (ex. `en-US-Neural2-D`,
   `fr-FR-Neural2-B` ; la liste est éditable pour saisir n'importe quel nom de voix Google).
4. Le code de langue est déduit du nom de la voix. En cas d'absence de clé ou d'erreur
   → **repli automatique sur la voix Windows**.
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed

### Radio effect: one intensity slider

<<<<<<< HEAD
The DSP chain ([`RadioDsp`](src/WilcoATC/Audio/RadioDsp.cs)) applies a band-pass filter,
saturation, background hiss and squelch clicks. Those four cannot be judged separately by
ear, so they are driven by **a single intensity setting** (0 to 1, default **0.5**) through
[`RadioProfile.FromIntensity`](src/WilcoATC/Audio/RadioProfile.cs).

| Intensity | Band-pass | Hiss | Saturation |
=======
### LLM optionnel (Ollama local par défaut, ou BYOK cloud)
- **Ollama** : installez https://ollama.com, `ollama pull llama3.2`, puis Réglages →
  LLM **Ollama** (URL `http://localhost:11434`, modèle `llama3.2`).
- **Cloud (BYOK)** : Réglages → LLM **Cloud**, renseignez URL/modèle OpenAI-compatible
  et **le nom de la variable d'environnement** contenant votre clé (défaut
  `WilcoATC_LLM_KEY`). La clé n'est jamais stockée dans l'app.
- En cas d'échec (LLM injoignable, timeout, pas de clé) → **repli templates**.

### 100 % gratuit et hors-ligne
Templates + voix Windows + DSP local ne nécessitent **aucune clé ni connexion**. Le test
manuel fonctionne dès le lancement, même sans simu (avec un indicatif générique).

## Comprendre les requêtes du pilote (v1, au sol)

Le pilote formule une requête (**voix bientôt, ou texte dès maintenant**), l'app en
déduit l'intention, la **valide selon l'état courant**, puis répond (clairance ou refus)
via la voix radio. Boucle : `texte/STT → intention → validation contexte → réponse`.

Quatre briques découplées ([`Atc/Understanding`](src/WilcoATC/Atc/Understanding/),
[`Atc/Context`](src/WilcoATC/Atc/Context/), [`Atc/Brain`](src/WilcoATC/Atc/Brain/)) :

| Brique | Rôle | Défaut (gratuit, offline) | Option |
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed
|---|---|---|---|
| 0.0 | 150 – 6000 Hz | 0.004 | 1.2 |
| **0.5** *(default)* | **275 – 4300 Hz** | **0.017** | **1.9** |
| 1.0 | 400 – 2600 Hz | 0.030 | 2.6 |

<<<<<<< HEAD
> The old setting was fixed at 300–3000 Hz, the equivalent of intensity **0.9** — the voice
> was needlessly degraded by default. The current default lets the timbre through.

### Radio sound effects: real samples
=======
**Grammaire bilingue** ([`GrammarIntentRecognizer`](src/WilcoATC/Atc/Understanding/GrammarIntentRecognizer.cs)) :
pilotée par la **langue effective** (réglage « Langue des transmissions », `Auto` suit la voix).
Chaque intention a ses mots-clés **français ET anglais** — ex. `REQUEST_PUSHBACK` = « repoussage /
repousser / push », `REQUEST_TAXI` = « roulage », `READY_FOR_DEPARTURE` = « prêt(s) au départ »,
`CHECK_IN` = « bonjour », `REPORT_APPROACH` = « niveau de vol / en approche ». Le texte est
**normalisé** (minuscules, accents supprimés, ponctuation ignorée) → tolère casse, hésitations et
callsign. En `UNKNOWN`, la console affiche la **raison** (« aucun mot-clé reconnu (langue=FR) »)
pour distinguer un échec ASR d'un échec de grammaire.

**Collationnement (readback) vs nouvelle requête** : après une clairance/approbation, l'ATC
passe en état **« en attente de collationnement »**. Tant qu'il est actif, un message qui
reprend les termes de l'ATC, contient un accusé (« approuvé / correct / roger / wilco / reçu »)
ou n'est que le callsign est classé **READBACK** (jamais revalidé comme requête, donc jamais
refusé pour cause de phase) → réponse « collationnement correct » ([`ReadbackDetector`](src/WilcoATC/Atc/ReadbackDetector.cs)).
Une requête **déjà accordée** obtient « c'est déjà approuvé » au lieu d'un refus de phase.
Le panneau debug affiche l'état *collationnement attendu : OUI/non* et, par message, s'il est
classé REQUEST ou READBACK.
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed

Mic clicks and the squelch tail are **synthesised by default** — and it shows. A metal
contact inside a plastic housing has resonances three filters cannot imitate; a breath even
less so.

Drop your own `.wav` files into **`%LOCALAPPDATA%\WilcoATC\radio\`**
([`RadioSampleRepository`](src/WilcoATC/Audio/RadioSampleRepository.cs)). **The filename
prefix selects the category**, the rest is free:

<<<<<<< HEAD
| Prefix | When it plays | Without a file |
=======
**Table de règles** éditable : `%LOCALAPPDATA%\WilcoATC\atc-rules.json` (créée au 1er
lancement depuis la ressource embarquée). Chaque règle : intention → phases autorisées +
contrôleurs autorisés + `requireOnGround` + phrase d'accord ; les refus sont contextuels
(en vol / mauvaise fréquence + redirection / mauvaise phase).

**Console pilote** (bas de la fenêtre) : champ de saisie, override contrôleur, et
l'affichage de débogage **transcription → intention → décision** (vert = accordé, rouge =
refusé). Les requêtes et décisions sont aussi journalisées.

**Ajouter une intention** : ajoutez une entrée dans `atc-rules.json` (conditions +
phrase `approved`) — la validation et la réponse sont pilotées par ce fichier. *(Un
tout nouveau type d'intention demande aussi un mot-clé de grammaire, ou l'activation du
LLM ; l'exemple pushback fonctionne intégralement en templates.)*

**Vérifié** : les 5 scénarios (pushback accordé au parking/Ground, refusé en vol, refusé
sur Tour + redirection, ready-for-departure, taxi) passent à l'exécution.

## Plan de vol SimBrief + indicatif réaliste

**Indicatif parlé** ([`CallsignFormatter`](src/WilcoATC/Atc/Planning/CallsignFormatter.cs), réutilisé
partout — clairance ET contact initial) :
- vol de ligne → **télophonie compagnie + numéro** (ex. `UAE 231` → « Emirates 231 »), via le
  dataset **OpenFlights `airlines.dat`** embarqué (ICAO → callsign) ;
- aviation générale → **immatriculation en alphabet phonétique OACI** (`G-FBIG` → « Golf Foxtrot
  Bravo India Golf »). Plus jamais l'immat brute pour un vol de compagnie.

**Import SimBrief** (Réglages → *Plan de vol*) : saisissez votre **username SimBrief** (ou Pilot ID),
puis **« Importer depuis SimBrief »**. Appel de l'API **gratuite et sans clé**
`https://www.simbrief.com/api/xml.fetcher.php?username={username}&json=1`. Un mauvais username →
message d'erreur clair, sans crash. On peut aussi **charger un fichier OFP XML** exporté.

Le [`FlightPlan`](src/WilcoATC/Atc/Planning/FlightPlan.cs) extrait : origine/destination (ICAO+nom),
alternate, route, **altitude de croisière** (`general.initial_altitude`), compagnie + numéro
(`general.icao_airline`/`flight_number`), callsign (`atc.callsign`), avion (`aircraft.icaocode`).
Un encart **« Plan de vol chargé »** (callsign, départ → destination, croisière, avion) confirme l'import.

**Clairance** (structure CRAFT) utilisant en priorité SimBrief :
> **« Emirates 231, cleared to Abu Dhabi as filed, climb and maintain 5000 feet, squawk 4271. »**

- `{callsign}` ← plan (télophonie) ou immat phonétique ;
- `{destination}` ← nom SimBrief nettoyé (« Abu Dhabi Intl » → « Abu Dhabi »), sinon slot « to X »
  de la requête vocale (résolu via OurAirports), sinon *« say again your destination »* ;
- `{initial_altitude}` ← altitude de départ standard **5000 ft** (réglable) — SimBrief ne fournit que
  la croisière, affichée dans l'encart ;
- `{squawk}` ← code octal valide généré.
- **Sans SimBrief** : repli propre (destination vocale + altitude par défaut).

## ATC proactif + langue (voix → texte)

**Langue automatique** : le réglage *Langue ATC = **Auto*** (défaut) fait **parler l'ATC dans
la langue de la voix TTS sélectionnée** — une voix `fr_FR` déclenche des transmissions en
**français**, une voix `en_US` en **anglais** ([`LanguageResolver`](src/WilcoATC/Atc/LanguageResolver.cs)).
On peut aussi forcer English ou Français. Les réponses (clairance, refus) et les
transmissions proactives existent en EN et FR dans [`atc-rules.json`](src/WilcoATC/Atc/Brain/atc-rules.json).

**ATC proactif** ([`FlightDirector`](src/WilcoATC/Atc/FlightDirector.cs)) : au-delà des
réponses aux requêtes, l'ATC **initie** des transmissions aux transitions de phase de vol —
l'ATC te parle tout au long du vol :

| Transition | L'ATC dit… |
|---|---|
| **Décollage SANS autorisation** | *« vous avez décollé sans autorisation, contactez la tour »* (la tour te rappelle) |
| **Décollage autorisé → en l'air** | *« contact radar, contactez le départ sur {fréquence} »* (transfert de fréquence) |
| Entrée en **approche** | *« descendez et maintenez 3000 pieds, prévoyez l'ILS piste… »* |
| **Atterrissage** | *« piste…, autorisé à l'atterrissage »* |
| **Roulage arrivée** | *« bienvenue, rejoignez le parking, contactez le sol »* |

L'autorisation de décollage est mémorisée quand tu obtiens le « cleared for takeoff »
(`READY_FOR_DEPARTURE` accordé) ; sans elle, le décollage déclenche le rappel de la tour.
Chaque événement est joué **une fois par vol** (réarmé au retour au parking).

## SID de clairance & transferts de fréquence

**SID dans la clairance** : le vrai **SID** est extrait du `navlog` SimBrief (fixes où
`is_sid_star=="1"`, nom dans `via_airway`) et prononcé par [`SidFormatter`](src/WilcoATC/Formatting/SidFormatter.cs)
(`SOSAL2Y` → « SOSAL 2 Yankee »). La clairance devient *« …autorisé à destination de Genève
**via le départ SOSAL 2 Yankee**, montez initialement 5000 pieds, transpondeur … »* (EN :
*« …via the SOSAL 2 Yankee departure… »*). **Sans SID / sans plan** → repli « selon le plan
déposé » / « as filed ».

**Transferts de fréquence en vol** ([`ControllerSequencer`](src/WilcoATC/Atc/ControllerSequencer.cs)) :
l'ATC enchaîne les positions et te dit quand changer de fréquence, avec la fréquence
**prononcée chiffre par chiffre** ([`FrequencyFormatter.Speak`](src/WilcoATC/Formatting/FrequencyFormatter.cs),
`128.500` → « un deux huit décimal cinq »). Déclencheurs (constantes ajustables) :

| Transfert | Seuil |
|---|---|
| Tour → Départ | AGL > `2500 ft` en montée |
| Départ → Centre | MSL > `FL100` |
| Centre → Approche | en descente & distance arrivée < `40 NM` |
| Approche → Tour arrivée | AGL < `2000 ft` & < `15 NM` |
| Tour arrivée → Sol arrivée | posé, < `30 kt` |

**Fréquences (honnêteté sur les données)** :
- **Terminales** (Sol/Tour/Départ/Approche) : **OurAirports** (`airport-frequencies.csv`) pour
  le terrain concerné — fiables.
- **Centre** : pas de dataset gratuit fiable → si **VATSIM activé** (Réglages) et en ligne,
  on récupère la vraie fréquence du contrôleur `*_CTR` de la région ([`VatsimClient`](src/WilcoATC/Atc/Vatsim/VatsimClient.cs)) ;
  sinon une valeur **approximative configurable** (log « fréquence Centre approximative »).

Après le transfert, quand tu **te cales sur la nouvelle fréquence**, la détection de
changement COM déclenche le **check-in** sur la nouvelle station (contact initial).

## Intégration GSX (pushback)

Quand l'ATC **accorde le pushback**, l'app peut déclencher le pushback de **GSX Pro**
(FSDreamTeam) — **sans module WASM**.

**Mécanisme** ([`GsxGroundServices`](src/WilcoATC/Atc/GroundServices/GsxGroundServices.cs)) :
le SimConnect standard ne peut pas écrire les LVARs de GSX, mais GSX a une option
**auto-pushback** documentée : *frein de parking serré + phare anticollision (beacon)
allumé → GSX demande le pushback*. L'app **allume donc le beacon** via SimConnect
(`BEACON_LIGHTS_SET`) quand la clairance pushback est accordée ; GSX prend le relais
(direction demandée dans son menu, ou automatique).

**Activation** : Réglages → ATC → **« Déclencher GSX au pushback »** (désactivé par défaut).
Prérequis côté GSX : activer son **auto-pushback**. Sans GSX / sans l'option, l'effet se
limite à allumer le beacon (inoffensif). Architecture isolée derrière `IGroundServices`
(on pourra ajouter plus tard un pilotage LVAR direct via un pont WASM MobiFlight/FSUIPC).

## Robustesse / reconnexion

- **Simu absent au démarrage** → état « En attente », **retry automatique** toutes les 2 s.
- **Simu fermé en cours** (`OnRecvQuit`) → teardown propre → retour en attente →
  **reconnexion automatique** au redémarrage du simu.
- **Erreur SimConnect** (`COMException`, `OnRecvException`) → log + reconnexion, **jamais
  de crash**.
- **DLL SimConnect manquante/incompatible** → état « Dépendance manquante » (voyant
  rouge) + message clair, au lieu d'un crash.

## Stretch : résolution de station (OurAirports)

Bonus **totalement isolé** (dossier [`Stations/`](src/WilcoATC/Stations/)). Si tu
places `airports.csv` + `airport-frequencies.csv` dans [`data/`](data/) (voir
[`data/README.txt`](data/README.txt)), l'app tente d'associer la fréquence active à
l'aéroport le plus proche partageant cette fréquence et affiche son nom
(ex. `Paris CDG · TWR`). Sans les fichiers, le résolveur se désactive silencieusement.

## Dépannage

| Symptôme | Cause probable | Solution |
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed
|---|---|---|
| `keyup*.wav` | the pilot presses the PTT, at the start | synthesised |
| `breath*.wav` | their breath, just before speaking | **nothing** |
| `tail*.wav` | the squelch tail, when the carrier drops | synthesised |
| `bed*.wav` | their cockpit background, under the voice | **nothing** |

```
keyup.wav   keyup-2.wav   keyup_yaesu.wav      -> 3 variants, drawn at random
breath.wav  breath-long.wav
tail.wav
bed-cessna.wav
```

Four things that matter:

- **Several variants per category**, drawn at random on each transmission. The same sound
  on a loop gives the game away as much as a bad one.
- **Neither breath nor cockpit background is synthesised.** With no file they simply do not
  play — a fake breath is heard instantly, and its absence is better.
- **The cockpit background cuts dead** when the PTT is released, before the squelch tail.
  That abrupt stop carries much of the credibility.
- **Automatic resampling** — a 44.1 kHz file is brought down to the stream rate. Without
  that it would come out an octave low and twice as long.

Anything unusable is tolerated: missing folder, corrupt file, exotic format → the sample is
skipped (with a log line) and the transmission carries on.

Settings ▸ Audio offers **Open folder**, **Reload** (after dropping files in, no restart)
the pack status, and a level slider for the cockpit background.

> The files are not shipped: their licences are not ours to give. Free sound banks, ham
> radio archives or your own recordings all work.

### Speech recognition (ASR)

Default model: **Parakeet TDT 0.6B v2** (NVIDIA NeMo, int8, ~460 MB), free and offline,
chosen **by measurement** on a corpus of synthesised ATC phraseology:

| Model | Word errors | Time per phrase |
|---|---:|---:|
| **parakeet-tdt-0.6b-v2** *(default)* | **8.8 %** | 0.15 s |
| whisper-base.en | 14.4 % | 0.18 s |
| whisper-tiny | 14.7 % | 0.13 s |

- **Where?** `%LOCALAPPDATA%\WilcoATC\asr\`. Deleting the folder is enough to roll back.
- **Whisper models are still supported**: the best *installed* model wins
  ([`SpeechModelRepository.Resolve`](src/WilcoATC/Audio/SpeechModelRepository.cs)).
- **No hotwords.** Vocabulary biasing was tried and made things **worse** (10.5 % against
  8.8 %): the model's vocabulary is sub-word, whole words do not encode into it. Aviation
  accuracy therefore comes from a correction pass **after** transcription
  ([`AtcTextNormalizer`](src/WilcoATC/Atc/Understanding/AtcTextNormalizer.cs)), which leaves
  decoding alone.
- **Push-to-talk** keeps capturing for **250 ms after the key is released** (people almost
  always let go on the last syllable), and the minimum length is **0.25 s** — it used to be
  0.5 s, which silently threw away "wilco", "roger" and other short answers.

### Airline callsigns, both ways

The OpenFlights `airlines.dat` dataset is embedded and used in **both directions**:

- **Speaking** — [`CallsignFormatter`](src/WilcoATC/Atc/Planning/CallsignFormatter.cs) says
  "Speedbird 123" rather than spelling out a registration. With no known airline it falls
  back to the registration in the ICAO phonetic alphabet.
- **Hearing** — [`SpokenCallsignResolver`](src/WilcoATC/Atc/Understanding/SpokenCallsignResolver.cs)
  finds the airline the pilot said, including when recognition split it in two ("speed bird
  123") or dropped a letter.
- **Guard rail** — a telephony name is only accepted when it is **followed by a number**.
  The dataset contains callsigns that are also ordinary words ("Cactus", "Eagle"); without
  that rule they would fire on plain phraseology.
- About thirty **corrections** are layered on top of the dataset, wherever the radio does
  not use the commercial name (BAW → *Speedbird*, DLH → *Lufthansa*, BEL → *Beeline*…).

### First-run wizard

On the very first start (setting `OnboardingCompleted` missing or false), a **six-screen**
wizard opens over the main window:

1. **Welcome** — what the application does, and the reminder that nothing leaves the machine.
2. **Models** — downloading the ASR model (~460 MB) and a voice (~110 MB), with progress and
   recovery from network drops.
3. **Audio** — output device (with a voice test) and microphone (with an **end-to-end
   test**: you speak, the transcription appears).
4. **Flight** — push-to-talk key, the flight you are about to fly, and your SimBrief
   username (optional).
5. **Tour** — the four areas of the main window.
6. **Done** — a recap of what was configured.

> **It informs, it does not block.** Every step is skippable and no *Next* button is ever
> disabled: turning down the ~570 MB has to leave a usable application — typed input instead
> of the microphone, the Windows voice instead of Piper.

**Running it again**: Settings → **Startup** tab → *Run again*. The wizard opens on the
**next launch** of WilcoATC (reopening it immediately would stack two modal windows writing
to the same settings). Closing it with the ✕ does **not** mark it complete: it comes back.

### Google voices (Google Cloud TTS, optional — BYOK)

Very good neural voices (WaveNet / Neural2 / Studio). Google's **monthly free tier**, then
paid: it is a **BYOK** option and never required.

1. Google Cloud console → enable the **Cloud Text-to-Speech** API → create an **API key**.
2. Put the key in an **environment variable** (default `WILCOATC_GOOGLE_KEY`):
   `setx WILCOATC_GOOGLE_KEY "your_key"`, then restart the app. The key is **never** stored
   by the application. *(The former name `FREQWATCH_GOOGLE_KEY` is still read as a fallback.)*
3. Settings → engine **Google**, then pick a voice (`en-US-Neural2-D`, `en-GB-Neural2-B`…;
   the list is editable, so any Google voice name works).
4. The language code is derived from the voice name. Missing key or any error → **automatic
   fallback to the Windows voice**.

### Free and offline, all the way

The rule table, the local ASR, the native TTS and the local DSP need **no key and no
connection**. The manual test works from the first launch, even with no simulator running.

## Understanding what the pilot says

The pilot speaks, the app derives an **intent**, validates it against the **current state**,
then answers — clearance or refusal — in the radio voice.
Loop: `speech → text → intent → context check → answer`.

Four decoupled pieces ([`Atc/Understanding`](src/WilcoATC/Atc/Understanding/),
[`Atc/Context`](src/WilcoATC/Atc/Context/), [`Atc/Brain`](src/WilcoATC/Atc/Brain/)):

| Piece | Role |
|---|---|
| `ISpeechToText` | Hear the pilot — sherpa-onnx, offline |
| `IIntentRecognizer` | Understand — keyword grammar, one keyword set per language |
| `FlightContextProvider` | Current state — flight phase and controller, derived from SimVars |
| `AtcBrain` | Validate and answer — the JSON rule table |

**Grammar** ([`GrammarIntentRecognizer`](src/WilcoATC/Atc/Understanding/GrammarIntentRecognizer.cs))
is driven by the **effective language**. Every intent has its keywords per language —
`REQUEST_PUSHBACK` = "pushback / push", `REQUEST_TAXI` = "taxi", `READY_FOR_DEPARTURE` =
"ready for departure", `CHECK_IN` = "good day", `REPORT_APPROACH` = "on approach". Text is
**normalised** first (lower case, accents folded, punctuation dropped, spelled numbers
turned into digits), so case, hesitations and the callsign do not matter. On `UNKNOWN` the
log states the **reason**, which separates a recognition failure from a grammar failure.

**Phase never refuses anything.** The flight phase is *estimated* from SimVars, so it is a
guess — and when the guess was wrong the controller used to answer "unable at this time" to
a perfectly valid request, leaving the pilot with no way out. Phases still **trigger**
things (copilot callouts, approach calls, coherent ambient traffic); they never **forbid**.

**Read-back versus new request** — after a clearance the controller waits for a read-back.
While it waits, a message that repeats its words, contains an acknowledgement or is just the
callsign is classified **READBACK**
([`ReadbackDetector`](src/WilcoATC/Atc/ReadbackDetector.cs)) — never re-validated as a
request, so never refused. What has to be read back (runway, squawk, frequency, altitude) is
extracted from what the controller just said
([`ReadbackChecker`](src/WilcoATC/Atc/ReadbackChecker.cs)); an incomplete read-back gets the
**missing item named** back at you, not the whole clearance repeated. Stay silent and the
controller prompts twice, then repeats the instruction, then gives up — and, if you enabled
it, concludes there is a radio failure.

**Flight phase** comes from `SIM ON GROUND`, ground speed, AGL altitude, vertical speed and
**`BRAKE PARKING INDICATOR`**: `PARKED → PUSHBACK → TAXI_OUT → TAKEOFF → AIRBORNE →
APPROACH → LANDING → TAXI_IN`. The **controller** is the type of the station resolved from
the active COM frequency (Ground / Tower / Clearance / Approach / Center).

**Editable rule table**: `%LOCALAPPDATA%\WilcoATC\atc-rules.json`, written on first launch
from the embedded resource and refreshed whenever the shipped version is newer. Each rule
maps an intent to allowed controllers, `requireOnGround`, and the phrasing of the approval —
in every supported language. Adding an intent means adding an entry there (plus a grammar
keyword if the intent itself is new).

**Controller languages**: English (the ICAO standard and the universal fallback), French,
German, Spanish and Italian. The country of the airport picks the language; the moment the
pilot speaks another one, the controller follows them. Adding a language needs three things
and nothing else: a value in `AtcLanguage`, its phrases in `AtcPhrases` and the `i18n`
blocks of `atc-rules.json`, and its pilot keywords in `IntentKeywords`.

**Interface languages**: English, Français, Deutsch, Español, Italiano, Português,
Nederlands — independent of the controller's language.

## SimBrief flight plan and realistic callsigns

**Spoken callsign** ([`CallsignFormatter`](src/WilcoATC/Atc/Planning/CallsignFormatter.cs),
reused everywhere — clearance and initial contact alike):

- airline flight → **telephony name + number** (`UAE 231` → "Emirates 231"), through the
  embedded **OpenFlights `airlines.dat`** dataset (ICAO → callsign);
- general aviation → **registration in the ICAO phonetic alphabet** (`G-FBIG` → "Golf
  Foxtrot Bravo India Golf"). Never a raw registration for an airline flight again.

**SimBrief import** (Settings → *Flight plan*): enter your **SimBrief username** (or Pilot
ID) and press **Import from SimBrief**. It calls the **free, key-less** API
`https://www.simbrief.com/api/xml.fetcher.php?username={username}&json=1`. A wrong username
produces a clear error, never a crash. An exported **OFP XML file** can be loaded instead.

[`FlightPlan`](src/WilcoATC/Atc/Planning/FlightPlan.cs) extracts origin and destination
(ICAO + name), alternate, route, **cruise altitude**, airline and flight number, callsign,
aircraft type, and the **SID** (see below). A *Flight plan loaded* panel confirms the import.

**Clearance** (CRAFT order), preferring SimBrief data:

> **"Emirates 231, cleared to Abu Dhabi as filed, climb and maintain 5000 feet, squawk 4271."**

- `{callsign}` ← the plan (telephony) or the phonetic registration;
- `{destination}` ← the cleaned SimBrief name ("Abu Dhabi Intl" → "Abu Dhabi"), otherwise the
  destination heard in the request (resolved through OurAirports), otherwise *"say again your
  destination"*;
- `{initial_altitude}` ← the standard initial climb, **5000 ft** by default and adjustable —
  SimBrief only gives the cruise level;
- `{squawk}` ← a generated, valid octal code.
- **Without SimBrief**: a clean fallback (spoken destination + default altitude).

**Startup and clearance in one call** — the standard European exchange, where the pilot asks
`request startup, destination …`. Delivery grants both at once, quotes the **SID** and the
**initial level**, and then, once the read-back is correct, **hands you to Ground**:

> — *"Brussels Delivery, good day, Beeline 3633, Airbus A320, gate 154, request startup, destination Charles de Gaulle, information Delta."*
> — **"Beeline 3633, good day, startup approved, CIV 2 Delta departure, level 70, squawk 3400."**
> — *"Startup approved, CIV 2 Delta departure, level 70, squawk 3400."*
> — **"Beeline 3633, readback correct, report ready for pushback on Brussels Ground on 121.875."**
> — *"Brussels Ground on 121.875, Beeline 3633, thank you."* → the controller **says nothing
> more**: they are done with this flight.

- `{sid}` ← the SID from the flight plan, spoken (`CIV2D` → "CIV 2 Delta"). **With no SID
  loaded**, it falls back to the "cleared to … as filed" clearance;
- `{initial_level}` ← the same setting as `{initial_altitude}`, announced as a level
  (5000 ft → "level 50");
- `{ground_station}` / `{ground_freq}` ← the airport's **actually published** Ground position
  and frequency. No Ground at that airport → a plain *"readback correct"*, with no handoff to
  a controller that does not exist.

The **SID** itself is extracted from the SimBrief `navlog` (fixes where `is_sid_star=="1"`,
name in `via_airway`) and spoken by
[`SidFormatter`](src/WilcoATC/Formatting/SidFormatter.cs): `SOSAL2Y` → "SOSAL 2 Yankee".

## Proactive ATC and controller handoffs

Beyond answering requests, the controller **starts** transmissions
([`FlightDirector`](src/WilcoATC/Atc/FlightDirector.cs)):

| Event | The controller says… |
|---|---|
| **Takeoff WITHOUT a clearance** | *"you departed without clearance, contact the tower"* |
| Entering **approach** | *"descend and maintain 3000 feet, expect the ILS runway…"* |
| **Landing** | *"runway…, cleared to land"* |
| **Taxi in** | *"welcome, taxi to the stand, contact ground"* |

The takeoff clearance is remembered when you are granted one; without it, taking off
triggers the tower's call. Each event plays **once per flight**, re-armed when you park.

**Frequency handoffs** ([`ControllerSequencer`](src/WilcoATC/Atc/ControllerSequencer.cs)):
the controller walks the positions and tells you when to change frequency, the frequency
being **spoken as digits**
([`FrequencyFormatter.Speak`](src/WilcoATC/Formatting/FrequencyFormatter.cs)). Thresholds are
adjustable constants:

| Handoff | Threshold |
|---|---|
| **Ground → Tower** (holding point) | on the ground, taxiing out, within `500 m` of a **runway threshold** and below `40 kt` |
| Tower → Departure | AGL > `2500 ft`, climbing |
| Departure → Center | MSL > `FL100` |
| Center → Approach | descending and less than `40 NM` from arrival |
| Approach → Arrival tower | AGL < `2000 ft` and less than `15 NM` |
| Arrival tower → Arrival ground | on the ground, below `30 kt` |

**Ground → Tower** is the only handoff on the ground. With no taxiway data available, it
fires on the **distance to the nearest runway threshold**
([`RunwayRepository.NearestThreshold`](src/WilcoATC/Stations/RunwayRepository.cs)), once per
flight. It stays **silent** if the airport publishes no Tower, if you are already on its
frequency, or if you are taxiing too fast to be arriving at a holding point. Like the handoff
to Ground after the clearance, it is a **goodbye**: you read the frequency back, and the
controller neither answers nor chases you.

**Frequencies — where they come from, honestly:**

- **Terminal positions** (Ground / Tower / Departure / Approach): **OurAirports**
  (`airport-frequencies.csv`) for that airport, plus the sim's own **live** frequencies when
  it publishes them — reliable.
- **Center**: no reliable free dataset. With **VATSIM enabled** in the settings and online,
  the real `*_CTR` frequency for the region is fetched
  ([`VatsimClient`](src/WilcoATC/Atc/Vatsim/VatsimClient.cs)); otherwise an **approximate,
  configurable** value is used, and the log says so.

Nothing is ever announced without a number: if the next frequency is unknown, the controller
says *"remain this frequency"* instead of naming a position you could not tune.

After a handoff, **tuning the new frequency** triggers the check-in with the new station
through the COM change detection.

## Ambient life: chatter, traffic, cabin

All of this is optional and off by default; none of it is needed for the ATC loop.

- **Radio chatter** ([`ChatterDirector`](src/WilcoATC/Immersion/ChatterDirector.cs)) — other
  crews on your frequency, with their own voices and phraseology matching the position you
  are on. It never uses your callsign.
- **Real traffic** ([`TrafficAtcDirector`](src/WilcoATC/Traffic/TrafficAtcDirector.cs)) — the
  controller talks to the aircraft your traffic add-on (FSLTL, AIG…) already provides. It
  **reads** that traffic, it does not create it.
- **Traffic injection** ([`TrafficInjector`](src/WilcoATC/Traffic/TrafficInjector.cs)) —
  spawns arrivals, departures and parked aircraft where an airfield is empty. They receive a
  flight plan and **the simulator flies them**; we never write their position. Your own
  aircraft type is deliberately excluded from the pool, so injection never produces a
  look-alike of your aeroplane.
- **Copilot callouts** and **cabin sound packs**
  ([`ImmersionController`](src/WilcoATC/Immersion/ImmersionController.cs)) — phase callouts
  and cabin announcements, sharing the same audio channel, where ATC always has priority.
- **Interception** ([`InterceptDirector`](src/WilcoATC/Atc/Intercept/InterceptDirector.cs)) —
  after a presumed radio failure, a fighter can be spawned to escort you. This is **the only
  feature that writes into the simulator's world**, and it is off by default. Your own
  aircraft type is excluded here too.

## GSX integration (pushback)

When the controller **approves pushback**, the app can start **GSX Pro** (FSDreamTeam)
pushback — **without a WASM module**.

**How** ([`GsxGroundServices`](src/WilcoATC/Atc/GroundServices/GsxGroundServices.cs)):
standard SimConnect cannot write GSX LVARs, but GSX has a documented **auto-pushback**
option — *parking brake set + beacon on → GSX requests pushback*. So the app **turns the
beacon on** through SimConnect (`BEACON_LIGHTS_SET`) when pushback is cleared, and GSX takes
over (direction from its menu, or automatic).

**Enabling it**: Settings → ATC → **Trigger GSX on pushback** (off by default). On the GSX
side, its own auto-pushback has to be enabled. Without GSX, or without that option, the
effect is limited to switching the beacon on, which is harmless. The whole thing sits behind
`IGroundServices`, so a direct LVAR path through a MobiFlight/FSUIPC WASM bridge can be added
later.

## Robustness and reconnection

- **No simulator at startup** → "Waiting" state, **automatic retry** every 2 s.
- **Simulator closed mid-session** (`OnRecvQuit`) → clean teardown → back to waiting →
  **automatic reconnection** when the sim restarts.
- **SimConnect error** (`COMException`, `OnRecvException`) → logged, then reconnection.
  **Never a crash.**
- **Missing or incompatible SimConnect DLL** → a "Missing dependency" state (red lamp) with a
  clear message, instead of a crash.
- **A log file per launch**, in `%LOCALAPPDATA%\WilcoATC\logs\` (the last 10 are kept). It is
  opened in a module initializer, *before* the entry point, so even a failure while loading
  assemblies is captured, and nothing is buffered — a process that dies abruptly still leaves
  its last lines.

## Where your data lives

| Path | What is in it |
|---|---|
| `%APPDATA%\WilcoATC\settings.json` | Settings |
| `%APPDATA%\WilcoATC\sim-titles.json` | Container titles seen in your simulator |
| `%LOCALAPPDATA%\WilcoATC\voices\` | TTS voices (one folder per voice) |
| `%LOCALAPPDATA%\WilcoATC\asr\` | Speech-recognition model |
| `%LOCALAPPDATA%\WilcoATC\atc-rules.json` | Editable rule table and phraseology |
| `%LOCALAPPDATA%\WilcoATC\radio\` | Your own radio samples |
| `%LOCALAPPDATA%\WilcoATC\cabin\` | Cabin sound packs |
| `%LOCALAPPDATA%\WilcoATC\logs\` | Diagnostic logs, last 10 launches |

> The application used to be called FreqWatch and stored all of this under a folder of that
> name. On first launch after the rename, those folders are **moved** to the new name
> ([`LegacyDataMigration`](src/WilcoATC/Diagnostics/LegacyDataMigration.cs)), so nothing —
> settings, voices, models — has to be downloaded or configured again.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Red "Missing dependency" lamp | Native `SimConnect.dll` not next to the exe | Check `libs/SimConnect.dll` and that the build is x64 |
| `BadImageFormatException` | Built as x86 or AnyCPU | Build **x64** |
| Stuck on "Waiting" while MSFS runs | No flight loaded, or SimConnect disabled | Load a flight; check the sim's `SimConnect.xml` |
| Frequencies never move | The aircraft's radio is 25 kHz only | Normal; test by changing frequency in the cockpit |
| The controller does not hear you | ASR model missing, or the wrong input device | Settings ▸ Audio: run the end-to-end microphone test |
| The controller keeps asking for a read-back | The missing item was not repeated | It names what is missing — read back the runway, squawk, frequency or altitude |

---

<<<<<<< HEAD
*WilcoATC — a talking controller for Microsoft Flight Simulator. No paid dependency.*
=======
*WilcoATC — moniteur COM SimConnect. Aucune dépendance payante.*
>>>>>>> e7fe021db87ca81b351c8289fd91318a552665ed
