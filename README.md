# FreqWatch

Application desktop **Windows / WPF (.NET 8)** qui se connecte à **Microsoft Flight
Simulator** (2024, compatible 2020) via **SimConnect**, détecte les changements de
**fréquence radio COM** et les affiche en direct sur une interface « panneau radio »
sombre.

> Première brique d'un projet plus large (ATC connecté au simu). Ici, le périmètre
> est volontairement limité : **se connecter au simu → lire les fréquences COM →
> détecter les changements → les afficher joliment.** Rien d'autre.

---

## Sommaire

- [Aperçu fonctionnel](#aperçu-fonctionnel)
- [Prérequis](#prérequis)
- [Les DLL SimConnect](#les-dll-simconnect)
- [Compiler et lancer](#compiler-et-lancer)
- [Architecture](#architecture)
- [Choix technique : unité des fréquences](#choix-technique--unité-des-fréquences)
- [Détection de changement (le flag CHANGED)](#détection-de-changement-le-flag-changed)
- [ATC vocal (voix par-dessus le jeu)](#atc-vocal-voix-par-dessus-le-jeu)
- [Robustesse / reconnexion](#robustesse--reconnexion)
- [Stretch : résolution de station (OurAirports)](#stretch--résolution-de-station-ourairports)
- [Dépannage](#dépannage)

---

## Aperçu fonctionnel

- **COM1 & COM2** : fréquence **active** (grande, ambre) et **standby** (plus petite,
  cyan), avec un **voyant TX** orange sur la radio qui émet (`COM TRANSMIT`).
- **Voyant de connexion** : 🟢 Connecté / 🟠 En attente (pulsé) / 🔴 Dépendance manquante.
- **Journal temps réel horodaté** : à chaque changement, une ligne apparaît **en haut**,
  ex. `14:32:07 — COM1 ACTIVE → 118.700`.
- **Panneau AVION** : appareil courant (`TITLE`), immatriculation (`ATC ID`),
  type/modèle, et **aéroport le plus proche** (issu du cache SimConnect).
- **Panneau VOL** : vitesse indiquée (IAS), vitesse sol (GS), vitesse verticale (V/S),
  altitude MSL et sol (AGL), cap, squawk, état sol/vol, latitude/longitude.
- **ATC vocal** : une transmission ATC contextuelle (avec votre indicatif) est
  **jouée à voix haute** sur le périphérique choisi, avec effet radio (voir plus bas).

## Prérequis

- **Windows 10/11 x64**.
- **.NET 8 SDK** (LTS) — https://dotnet.microsoft.com/download/dotnet/8.0
  (ou **Visual Studio 2022** 17.8+ avec la charge de travail « .NET Desktop »).
- **Microsoft Flight Simulator 2020 ou 2024** installé (pour un test réel).
- Les **DLL SimConnect** (managée + native) — voir ci-dessous.
- **NuGet** (gratuits, restaurés au premier build) : `NAudio` (audio), `System.Speech`
  (voix de secours), **`org.k2fsa.sherpa.onnx`** (TTS neuronale native) et `SharpCompress`
  (extraction des voix). Ollama et Google TTS sont optionnels.

## Les DLL SimConnect

Deux DLL sont nécessaires, **déjà embarquées** dans [`libs/`](libs/) (donc la
solution compile telle quelle) :

| Fichier | Rôle | Utilisation |
|---|---|---|
| `Microsoft.FlightSimulator.SimConnect.dll` | Wrapper **managé** .NET | Référencé par le projet (`<Reference>` + `<HintPath>`) |
| `SimConnect.dll` | Bibliothèque **native x64** | Copiée **à côté de l'exécutable** (sinon `DllNotFoundException`) |

**Où les récupérer soi-même** (source officielle = SDK MSFS) : voir
[`libs/README.txt`](libs/README.txt). En résumé : dans MSFS, active le SDK
(Options → General → Developers), installe le Core SDK, puis récupère les deux
fichiers dans `<MSFS SDK>\SimConnect SDK\lib\`.

Le `.csproj` gère les deux références automatiquement :
```xml
<Reference Include="Microsoft.FlightSimulator.SimConnect">
  <HintPath>..\..\libs\Microsoft.FlightSimulator.SimConnect.dll</HintPath>
  <Private>true</Private>
</Reference>
<None Include="..\..\libs\SimConnect.dll">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

> Le projet **cible x64** (`<Platforms>x64</Platforms>`) car le SimConnect natif est
> 64 bits. Compiler en AnyCPU/x86 provoque une `BadImageFormatException`.

## Compiler et lancer

**En ligne de commande :**
```powershell
cd FreqWatch
dotnet build -c Release
dotnet run --project src\FreqWatch -c Release
```

**Dans Visual Studio :**
1. Ouvre `FreqWatch.sln`.
2. Vérifie que la plateforme active est **x64**.
3. F5.

> ⚠️ **Le simulateur doit tourner** pour voir des données. Sans simu lancé, l'app
> affiche « En attente du simulateur… » (voyant ambre pulsé) et **réessaie en boucle** ;
> dès que MSFS démarre (et qu'un vol est chargé), elle se connecte automatiquement.

## Architecture

Séparation nette **couche SimConnect** ↔ **UI** : l'UI ne référence **jamais** de type
SimConnect ; elle ne dépend que de l'interface `ISimConnectService` et de quelques DTO.

```
FreqWatch/
├─ FreqWatch.sln
├─ libs/                     DLL SimConnect (managée + native)
├─ data/                     (stretch) CSV OurAirports, optionnels
└─ src/FreqWatch/
   ├─ App.xaml(.cs)          Composition root : crée le service + le VM, les câble
   ├─ MainWindow.xaml(.cs)   UI cockpit (aucun type SimConnect)
   │
   ├─ Sim/                   ── COUCHE SIMCONNECT (isolée, sans WPF) ──
   │  ├─ ISimConnectService.cs   Contrat vu par l'UI (le « joint » d'isolation)
   │  ├─ SimConnectService.cs    Connexion, thread de pompage, reconnexion, détection
   │  ├─ NullSimConnectService.cs Service inerte (aperçu concepteur)
   │  ├─ SimData.cs              Structs RadioData / PositionData (layout SimVar)
   │  ├─ SimIds.cs               Enums DEFINITION / REQUEST
   │  ├─ SimEvents.cs            DTO exposés (RadioSnapshot, RadioChange, PositionSnapshot)
   │  └─ ConnectionState.cs
   │
   ├─ Audio/                 ── PIPELINE VOCAL (NAudio, sans SimConnect) ──
   │  ├─ ITtsEngine.cs / TtsAudio.cs
   │  ├─ WindowsTtsEngine.cs     TTS par défaut (System.Speech)
   │  ├─ PiperTtsEngine.cs       TTS Piper (optionnel) + TtsEngineSelector
   │  ├─ RadioDsp.cs             bandpass + souffle + squelch + saturation
   │  ├─ RadioAudioPipeline.cs   effet + lecture sur le périphérique choisi
   │  └─ AudioDeviceService.cs   énumération des sorties (VB-CABLE inclus)
   │
   ├─ Atc/                   ── CERVEAU ATC (branché sur la couche Sim) ──
   │  ├─ IAtcLineGenerator.cs    QUOI dire
   │  ├─ TemplateAtcLineGenerator.cs   templates EN/FR (défaut)
   │  ├─ Llm/ + LlmAtcLineGenerator.cs Ollama / cloud BYOK (optionnel, repli templates)
   │  ├─ FlightSnapshot.cs       vue consolidée des données de vol
   │  └─ AtcController.cs        déclencheurs auto/manuel -> génère -> parle
   │
   ├─ Settings/              AppSettings + SettingsService (%APPDATA%\FreqWatch)
   ├─ Formatting/            FrequencyFormatter, TransponderFormatter, AircraftFormatter
   ├─ Common/Geo.cs          distance haversine partagée
   ├─ ViewModels/           MainViewModel, SettingsViewModel, RelayCommand, …
   ├─ Converters/           BoolToVisibility, LogKindToBrush
   ├─ Stations/             (stretch, isolé) résolveur OurAirports + mini-lecteur CSV
   ├─ SettingsWindow.xaml(.cs)  fenêtre de réglages
   └─ Themes/Cockpit.xaml   Palette et styles « avionique »
```

**Structures de données abonnées** :

```
RadioData      { Com1/2 Active/Standby (Hz), Com1/2 Transmit }   ← SIM_FRAME + CHANGED
ContextData    { Lat, Lon, AltMSL, AltAGL, HeadingTrue, IAS,     ← SECOND (1 Hz)
                 GroundSpeed, VerticalSpeed, OnGround, Squawk }
AircraftIdData { Title, AtcType, AtcModel, AtcId (strings) }      ← SECOND + CHANGED
```

`RadioData`/`ContextData` sont en `FLOAT64` ; `AircraftIdData` utilise des variables
**chaîne** (`STRING256/64/32`, marshalées via `ByValTStr` + `CharSet.Ansi`) et
n'est renvoyée qu'à la connexion puis à chaque changement d'appareil (flag `CHANGED`).

**Aéroport le plus proche** — via `SubscribeToFacilities(AIRPORT)` : le simu pousse
les aéroports de son cache (ICAO + coordonnées) ; on garde le plus proche de l'avion.
**Aucun fichier externe requis** ; si l'OurAirports CSV est présent, l'ICAO est enrichi
de son nom complet (ex. `LFPG · Paris Charles de Gaulle (3.2 km)`).

**Flux de threads** : tout ce qui touche SimConnect vit sur un **thread dédié**
(« SimConnect-Pump ») en mode *event-based* (un `WaitHandle` est signalé par
SimConnect, puis on appelle `ReceiveMessage()`). Les résultats sont publiés via des
événements .NET ; le `MainViewModel` fait le **marshalling vers le thread UI** via le
`Dispatcher`. → **L'UI ne gèle jamais.**

## Choix technique : unité des fréquences

Les fréquences sont demandées en **`Hz` (FLOAT64)**, et **non** en `Frequency BCD16`.

- `Frequency BCD16` renvoie un entier codé BCD : décodage fastidieux et **incapable
  de représenter proprement l'espacement 8.33 kHz**.
- `Hz` renvoie un **nombre entier de hertz exact** (ex. `118700000`) : **aucun décodage
  BCD**, précision suffisante pour le 8.33 kHz.

Affichage : `MHz = Hz ÷ 1 000 000`, arrondi et formaté à **3 décimales** → couvre
proprement le **25 kHz** (`118.700`, `121.500`) **et le 8.33 kHz** (`118.305`).
Voir le commentaire détaillé dans [`Formatting/FrequencyFormatter.cs`](src/FreqWatch/Formatting/FrequencyFormatter.cs).

Le squawk (`TRANSPONDER CODE:1`, unité `BCO16`) est décodé quartet par quartet dans
[`Formatting/TransponderFormatter.cs`](src/FreqWatch/Formatting/TransponderFormatter.cs).

## Détection de changement (le flag CHANGED)

La requête radio utilise `SIMCONNECT_PERIOD.SIM_FRAME` **avec le flag
`SIMCONNECT_DATA_REQUEST_FLAG.CHANGED`** : le simu n'envoie une mise à jour **que
lorsqu'une valeur radio change**. C'est précisément le mécanisme de « détection de
changement de fréquence » — instantané (à la frame près) et sans flux inutile.

La position, elle, **n'utilise pas** `CHANGED` (elle varie en permanence) : elle est
demandée à **1 Hz** (`SECOND`) pour un simple encart contextuel. Séparer les deux
définitions évite qu'un déplacement de l'avion ne déclenche des envois radio.

Le service compare chaque champ au dernier connu pour produire des lignes de journal
atomiques (`COM1 ACTIVE → 118.700`, `COM2 ÉMISSION ON`, …).

## ATC vocal (voix par-dessus le jeu)

> ⚠️ **La voix se joue PAR-DESSUS le jeu**, sur un périphérique de sortie — on
> n'injecte rien dans le moteur son de MSFS (impossible proprement), exactement
> comme BeyondATC / SayIntentions.

**Boucle de bout en bout** : `données de vol → transmission ATC (texte) → TTS → filtre
radio → son joué`. Trois interfaces découplées (dossiers [`Atc/`](src/FreqWatch/Atc/)
et [`Audio/`](src/FreqWatch/Audio/)), faciles à remplacer :

| Interface | Rôle | Défaut (gratuit, hors-ligne) | Option |
|---|---|---|---|
| `IAtcLineGenerator` | QUOI dire | **Templates** déterministes (EN/FR) | LLM : Ollama local **ou** cloud BYOK |
| `ITtsEngine` | Texte → PCM | **sherpa-onnx** (Piper/VITS **natif C#**) | Google Cloud TTS (BYOK) · voix Windows (secours) |
| `RadioAudioPipeline` | Effet radio + lecture | **NAudio** (toujours) | — |

**Effet radio** ([`RadioDsp`](src/FreqWatch/Audio/RadioDsp.cs)) : passe-bande ~300–3000 Hz
(BiQuad), souffle de fond, **clic de squelch** à l'ouverture/fermeture, légère
saturation. Chaque étage est activable dans les réglages.

**Déclencheurs** :
- **Auto** — en se calant sur la fréquence d'une **station connue** (résolue via
  OurAirports) → contact initial, une seule fois par station. *(nécessite les CSV
  OurAirports, cf. plus bas ; sinon utilisez le test manuel).*
- **Manuel** — bouton **« ▶ Test ATC »**, touche **F1** (fenêtre au premier plan) ou
  raccourci **global Ctrl+Alt+A** (marche même quand MSFS a le focus).
- Un petit **délai aléatoire** précède chaque réponse (anti-effet « robot instantané »).

### Choisir le périphérique, la voix, activer le LLM
Bouton **« ⚙ Réglages »** (persistés dans `%APPDATA%\FreqWatch\settings.json`) :
- **Périphérique de sortie** : casque par défaut, ou un **câble virtuel** (VB-CABLE)
  pour une voie séparée du son du jeu.
- **Moteur/voix TTS** : **sherpa-onnx** (défaut, natif) ; Google (BYOK) ; Windows (secours).
- **LLM** : `Off` (templates), `Ollama` (local), `Cloud` (BYOK). Rien de configuré →
  **templates** ; le LLM n'est **jamais** obligatoire.
- **Effet radio** : bandes/souffle/squelch/saturation + volume.
- **Langue ATC** : English (phraséologie OACI) ou Français.

### Voix neuronale par défaut : sherpa-onnx (Piper natif, 100 % offline)
Le moteur par défaut est **sherpa-onnx** ([`SherpaOnnxTtsEngine`](src/FreqWatch/Audio/SherpaOnnxTtsEngine.cs)) :
Piper/VITS exécuté **nativement dans le process .NET** (package NuGet `org.k2fsa.sherpa.onnx`).
**Aucun Python, aucun `piper.exe`, aucune clé API.** Le PCM est généré en mémoire puis
passe dans le pipeline radio.

- **D'où vient la voix ?** Au **premier lancement sans voix installée**, l'app télécharge
  automatiquement (barre de progression) la voix par défaut
  **`vits-piper-en_US-ryan-medium`** depuis
  `https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-ryan-medium.tar.bz2`
  et l'extrait (pur managé, via SharpCompress + `System.Formats.Tar`).
- **Où sont stockées les voix ?** Dans **`%LOCALAPPDATA%\FreqWatch\voices\`** — un dossier
  par voix, contenant `*.onnx` + `tokens.txt` + `espeak-ng-data/` (format sherpa-onnx).
- **Ajouter une voix (dont des voix françaises)** : Réglages → **« Ajouter une voix »**
  propose un catalogue téléchargeable en un clic, incluant des voix **françaises** Piper
  (`fr_FR-siwis`, `fr_FR-tom`, `fr_FR-upmc`, `fr_FR-gilles`). La voix installée est
  sélectionnée automatiquement. Pour une expérience 100 % française, combinez-la avec
  Réglages → **Langue ATC = Français**.
- **Voix personnalisée** : décompressez n'importe quel modèle TTS sherpa-onnx
  (https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models) dans
  `%LOCALAPPDATA%\FreqWatch\voices\`, puis Réglages → **Dossier des voix** pour rafraîchir.
  Vitesse d'élocution réglable ; speaker id géré pour les modèles multi-locuteurs.
- **Modèle manquant / erreur** → repli automatique sur la **voix Windows** (SAPI), et le
  bouton « Télécharger la voix par défaut » (Réglages) relance l'installation.

> Les DLL natives (`sherpa-onnx-c-api.dll`, `onnxruntime.dll`) sont fournies par le NuGet
> et copiées à côté de l'exécutable (projet ciblé **win-x64**).

### Voix Google (Google Cloud TTS, optionnel — BYOK)
Voix neuronales de très bonne qualité (WaveNet / Neural2 / Studio). **Palier gratuit
mensuel** Google, puis payant : c'est une option **BYOK**, jamais requise.
1. Console Google Cloud → activez l'API **Cloud Text-to-Speech** → créez une **clé API**.
2. Mettez la clé dans une **variable d'environnement** (défaut `FREQWATCH_GOOGLE_KEY`) :
   `setx FREQWATCH_GOOGLE_KEY "votre_clé"` (rouvrez l'app ensuite). La clé n'est
   **jamais** stockée dans l'application.
3. Réglages → moteur **Google**, choisissez une voix (ex. `en-US-Neural2-D`,
   `fr-FR-Neural2-B` ; la liste est éditable pour saisir n'importe quel nom de voix Google).
4. Le code de langue est déduit du nom de la voix. En cas d'absence de clé ou d'erreur
   → **repli automatique sur la voix Windows**.


### LLM optionnel (Ollama local par défaut, ou BYOK cloud)
- **Ollama** : installez https://ollama.com, `ollama pull llama3.2`, puis Réglages →
  LLM **Ollama** (URL `http://localhost:11434`, modèle `llama3.2`).
- **Cloud (BYOK)** : Réglages → LLM **Cloud**, renseignez URL/modèle OpenAI-compatible
  et **le nom de la variable d'environnement** contenant votre clé (défaut
  `FREQWATCH_LLM_KEY`). La clé n'est jamais stockée dans l'app.
- En cas d'échec (LLM injoignable, timeout, pas de clé) → **repli templates**.

### 100 % gratuit et hors-ligne
Templates + voix Windows + DSP local ne nécessitent **aucune clé ni connexion**. Le test
manuel fonctionne dès le lancement, même sans simu (avec un indicatif générique).

## Comprendre les requêtes du pilote (v1, au sol)

Le pilote formule une requête (**voix bientôt, ou texte dès maintenant**), l'app en
déduit l'intention, la **valide selon l'état courant**, puis répond (clairance ou refus)
via la voix radio. Boucle : `texte/STT → intention → validation contexte → réponse`.

Quatre briques découplées ([`Atc/Understanding`](src/FreqWatch/Atc/Understanding/),
[`Atc/Context`](src/FreqWatch/Atc/Context/), [`Atc/Brain`](src/FreqWatch/Atc/Brain/)) :

| Brique | Rôle | Défaut (gratuit, offline) | Option |
|---|---|---|---|
| `ISpeechToText` | entendre le pilote | *(à venir : ASR sherpa-onnx whisper, voir plus bas)* — **saisie texte** en attendant | — |
| `IIntentRecognizer` | comprendre | **grammaire / mots-clés BILINGUE (FR/EN)** | LLM (Ollama / BYOK), repli grammaire |
| `FlightContextProvider` | état courant | phase de vol + contrôleur, dérivés des SimVars | override manuel du contrôleur (test) |
| `AtcBrain` | valider + répondre | **table de règles JSON (EN + FR)** | — |

**Grammaire bilingue** ([`GrammarIntentRecognizer`](src/FreqWatch/Atc/Understanding/GrammarIntentRecognizer.cs)) :
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
refusé pour cause de phase) → réponse « collationnement correct » ([`ReadbackDetector`](src/FreqWatch/Atc/ReadbackDetector.cs)).
Une requête **déjà accordée** obtient « c'est déjà approuvé » au lieu d'un refus de phase.
Le panneau debug affiche l'état *collationnement attendu : OUI/non* et, par message, s'il est
classé REQUEST ou READBACK.

**ASR (voix) — modèle par langue** : quand le STT sera branché, il chargera un modèle **whisper
multilingue** sherpa-onnx (ex. `sherpa-onnx-whisper-base`) et fixera la langue de reconnaissance
(`fr` / `en`) d'après la langue effective — ou, pour l'anglais seul, `sherpa-onnx-whisper-tiny.en`.
En attendant, tout se teste en **mode texte** (le champ de saisie de la console pilote).

**Phase de vol** dérivée de `SIM ON GROUND`, vitesse sol, altitude AGL, vitesse verticale
et **`BRAKE PARKING INDICATOR`** : `PARKED → PUSHBACK → TAXI_OUT → TAKEOFF → AIRBORNE →
APPROACH → LANDING → TAXI_IN`. **Contrôleur** = type de la station résolue depuis la
fréquence COM active (Ground / Tower / Clearance / Approach / Center).

**Table de règles** éditable : `%LOCALAPPDATA%\FreqWatch\atc-rules.json` (créée au 1er
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

**Indicatif parlé** ([`CallsignFormatter`](src/FreqWatch/Atc/Planning/CallsignFormatter.cs), réutilisé
partout — clairance ET contact initial) :
- vol de ligne → **télophonie compagnie + numéro** (ex. `UAE 231` → « Emirates 231 »), via le
  dataset **OpenFlights `airlines.dat`** embarqué (ICAO → callsign) ;
- aviation générale → **immatriculation en alphabet phonétique OACI** (`G-FBIG` → « Golf Foxtrot
  Bravo India Golf »). Plus jamais l'immat brute pour un vol de compagnie.

**Import SimBrief** (Réglages → *Plan de vol*) : saisissez votre **username SimBrief** (ou Pilot ID),
puis **« Importer depuis SimBrief »**. Appel de l'API **gratuite et sans clé**
`https://www.simbrief.com/api/xml.fetcher.php?username={username}&json=1`. Un mauvais username →
message d'erreur clair, sans crash. On peut aussi **charger un fichier OFP XML** exporté.

Le [`FlightPlan`](src/FreqWatch/Atc/Planning/FlightPlan.cs) extrait : origine/destination (ICAO+nom),
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
**français**, une voix `en_US` en **anglais** ([`LanguageResolver`](src/FreqWatch/Atc/LanguageResolver.cs)).
On peut aussi forcer English ou Français. Les réponses (clairance, refus) et les
transmissions proactives existent en EN et FR dans [`atc-rules.json`](src/FreqWatch/Atc/Brain/atc-rules.json).

**ATC proactif** ([`FlightDirector`](src/FreqWatch/Atc/FlightDirector.cs)) : au-delà des
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
`is_sid_star=="1"`, nom dans `via_airway`) et prononcé par [`SidFormatter`](src/FreqWatch/Formatting/SidFormatter.cs)
(`SOSAL2Y` → « SOSAL 2 Yankee »). La clairance devient *« …autorisé à destination de Genève
**via le départ SOSAL 2 Yankee**, montez initialement 5000 pieds, transpondeur … »* (EN :
*« …via the SOSAL 2 Yankee departure… »*). **Sans SID / sans plan** → repli « selon le plan
déposé » / « as filed ».

**Transferts de fréquence en vol** ([`ControllerSequencer`](src/FreqWatch/Atc/ControllerSequencer.cs)) :
l'ATC enchaîne les positions et te dit quand changer de fréquence, avec la fréquence
**prononcée chiffre par chiffre** ([`FrequencyFormatter.Speak`](src/FreqWatch/Formatting/FrequencyFormatter.cs),
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
  on récupère la vraie fréquence du contrôleur `*_CTR` de la région ([`VatsimClient`](src/FreqWatch/Atc/Vatsim/VatsimClient.cs)) ;
  sinon une valeur **approximative configurable** (log « fréquence Centre approximative »).

Après le transfert, quand tu **te cales sur la nouvelle fréquence**, la détection de
changement COM déclenche le **check-in** sur la nouvelle station (contact initial).

## Intégration GSX (pushback)

Quand l'ATC **accorde le pushback**, l'app peut déclencher le pushback de **GSX Pro**
(FSDreamTeam) — **sans module WASM**.

**Mécanisme** ([`GsxGroundServices`](src/FreqWatch/Atc/GroundServices/GsxGroundServices.cs)) :
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

Bonus **totalement isolé** (dossier [`Stations/`](src/FreqWatch/Stations/)). Si tu
places `airports.csv` + `airport-frequencies.csv` dans [`data/`](data/) (voir
[`data/README.txt`](data/README.txt)), l'app tente d'associer la fréquence active à
l'aéroport le plus proche partageant cette fréquence et affiche son nom
(ex. `Paris CDG · TWR`). Sans les fichiers, le résolveur se désactive silencieusement.

## Dépannage

| Symptôme | Cause probable | Solution |
|---|---|---|
| Voyant rouge « Dépendance manquante » | `SimConnect.dll` natif absent à côté de l'exe | Vérifie `libs/SimConnect.dll` et le build x64 |
| `BadImageFormatException` | Build en x86/AnyCPU | Compile en **x64** |
| Reste « En attente » alors que MSFS tourne | Aucun vol chargé, ou SimConnect désactivé | Charge un vol ; vérifie `SimConnect.xml` du simu |
| Fréquences figées | Radio en mode 25 kHz seulement dans l'avion | Normal ; teste en changeant la fréquence dans le cockpit |

---

*FreqWatch — moniteur COM SimConnect. Aucune dépendance payante.*
