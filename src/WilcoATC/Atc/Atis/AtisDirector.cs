using System.Diagnostics;
using WilcoATC.Atc.Planning;
using WilcoATC.Audio;
using WilcoATC.Common;
using WilcoATC.Formatting;
using WilcoATC.Settings;
using WilcoATC.Sim;
using WilcoATC.Stations;

namespace WilcoATC.Atc.Atis;

/// <summary>
/// Station ATIS du terrain. Se caler sur une fréquence ATIS déclenche la lecture EN BOUCLE
/// du bulletin (météo du simulateur ramenée au sol, cf. <see cref="AtisSurface"/>) ; la
/// quitter l'arrête net, au milieu d'un mot s'il le faut — c'est ce que fait une radio.
///
/// Ce n'est PAS un contrôleur : l'ATIS ne répond à rien, ne salue personne et n'attend aucun
/// collationnement. Il vit donc à côté de l'<see cref="AtcController"/>, qui de son côté
/// s'abstient de dire bonjour sur une fréquence ATIS.
///
/// Le bulletin est FIGÉ à sa publication (conditions et heure d'observation comprises) et
/// rejoué à l'identique — l'audio n'est synthétisé qu'une fois, comme une bande qui tourne.
/// Un bulletin suivant (lettre suivante) n'est publié que si la météo a réellement changé,
/// et jamais plus d'une fois par <see cref="MinPublishInterval"/>.
/// </summary>
public sealed class AtisDirector : IDisposable
{
    private readonly ISimConnectService _sim;
    private readonly IStationResolver _stations;
    private readonly FlightPlanStore _plans;
    private readonly ITtsEngine _tts;
    private readonly VoiceBus _voice;
    private readonly VoicePicker _picker;
    private readonly SettingsService _settings;

    /// <summary>Anti-rebond : on ne branche l'ATIS qu'une fois le canal stable.</summary>
    private static readonly TimeSpan TuneSettle = TimeSpan.FromSeconds(2);

    /// <summary>Délai avant la première diffusion : une station s'écoute, elle ne saute pas dessus.</summary>
    private static readonly TimeSpan FirstBroadcastDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>Intervalle minimal entre deux bulletins (un vrai ATIS se met à jour à l'heure).</summary>
    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromMinutes(10);

    // Derniers instantanés reçus. Écrits sur le thread de pompage SimConnect, lus aussi par
    // la boucle de diffusion : ce sont des records immuables, l'affectation de référence est
    // atomique et lire un instantané légèrement daté est sans conséquence ici.
    private RadioSnapshot? _radio;
    private ContextSnapshot? _context;
    private WeatherSnapshot? _weather;

    // Machine à états du canal — touchée UNIQUEMENT depuis les événements sim (sérialisés
    // sur le thread de pompage), donc sans verrou.
    private string? _pendingKey;
    private DateTime _pendingSince;
    private string? _settledKey;      // canal déjà tranché (branché ou écarté) : on ne le re-résout pas

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    // Bulletin en cours de diffusion (null = rien de publié pour cette station).
    private AtisReport? _published;
    private string _digest = "";
    private DateTime _publishedAt;

    // Bande sonore du bulletin : synthétisée une fois, rejouée à chaque passage.
    private string _spokenText = "";
    private TtsAudio _spokenAudio = TtsAudio.Empty;

    /// <summary>Nouveau bulletin publié (texte complet) — pour le journal.</summary>
    public event Action<string>? AtisPublished;

    public AtisDirector(ISimConnectService sim, IStationResolver stations, FlightPlanStore plans,
                        ITtsEngine tts, VoiceBus voice, VoicePicker picker, SettingsService settings)
    {
        _sim = sim;
        _stations = stations;
        _plans = plans;
        _tts = tts;
        _voice = voice;
        _picker = picker;
        _settings = settings;
    }

    public void Start()
    {
        _sim.RadioSnapshotReceived += OnRadio;
        _sim.ContextReceived += OnContext;
        _sim.WeatherReceived += OnWeather;
        _sim.StateChanged += OnState;
    }

    // ------------------------------------------------------------------ événements sim

    private void OnRadio(RadioSnapshot r) { _radio = r; Evaluate(); }
    private void OnContext(ContextSnapshot c) { _context = c; Evaluate(); }
    private void OnWeather(WeatherSnapshot w) => _weather = w;

    private void OnState(ConnectionState state, string? _)
    {
        if (state == ConnectionState.Connected) return;
        StopBroadcast();
        _radio = null;
        _context = null;
        _weather = null;
        _pendingKey = null;
        _settledKey = null;
    }

    /// <summary>
    /// Décide si l'ATIS doit tourner. Appelée à chaque instantané radio/contexte, donc écrite
    /// pour être BON MARCHÉ : la résolution station↔fréquence (qui balaye le jeu de données)
    /// n'a lieu qu'une fois par canal, quand il s'est stabilisé.
    /// </summary>
    private void Evaluate()
    {
        var r = _radio; var c = _context;

        // Hors cockpit, ATIS désactivé, radio non initialisée : silence. Et on ré-arme, pour
        // que revenir aux commandes sur la même fréquence rebranche bien le bulletin.
        if (!_settings.Current.AtisEnabled || r is null || c is null
            || !c.InFlightSession || r.Com1ActiveHz < 1_000_000)
        {
            StopBroadcast();
            _settledKey = null;
            _pendingKey = null;
            return;
        }

        string key = Math.Round(r.Com1ActiveHz / 1000.0).ToString();

        if (key != _pendingKey) { _pendingKey = key; _pendingSince = DateTime.UtcNow; return; }
        if (DateTime.UtcNow - _pendingSince < TuneSettle) return;
        if (key == _settledKey) return;   // canal déjà tranché

        _settledKey = key;
        StopBroadcast();                  // on quitte l'éventuel bulletin précédent

        var station = TunedAtisStation(r.Com1ActiveHz, c);
        if (station is null) return;

        Debug.WriteLine($"[WilcoATC/Atis] fréquence ATIS de {station.Key} -> diffusion en boucle.");
        StartBroadcast(station);
    }

    // ------------------------------------------------------------------ quelle station ?

    /// <param name="Icao">null quand on n'a pas su rattacher la fréquence à un terrain précis.</param>
    private sealed record AtisStation(string? Icao, string Name)
    {
        /// <summary>Identité stable de la station (choix de la voix, lettre du bulletin).</summary>
        public string Key => Icao ?? Name;
    }

    /// <summary>
    /// L'ATIS de quel terrain écoute-t-on ? Deux voies INDÉPENDANTES, l'une suffit :
    ///  • un terrain candidat (le plus proche, ou celui du plan de vol) publie CETTE
    ///    fréquence en ATIS — c'est la voie qui donne l'ICAO, dont on a besoin pour la piste
    ///    du plan et la formule d'altimètre ;
    ///  • à défaut, la fréquence se résout sur un canal de type ATIS : on n'a alors que le
    ///    nom du terrain, ce qui suffit à diffuser un bulletin.
    ///
    /// Deux voies plutôt qu'une parce que les données de fréquences sont incomplètes : une
    /// seule suffirait à rendre la fonction muette là où l'autre voit très bien la station.
    /// </summary>
    private AtisStation? TunedAtisStation(double hz, ContextSnapshot c)
    {
        string? icao = MatchingIcao(hz, c);
        if (icao is not null)
        {
            string? name = FlightPlan.CleanAirportName(_stations.LookupAirportName(icao));
            return new AtisStation(icao, string.IsNullOrWhiteSpace(name) ? icao : name!);
        }

        var station = _stations.ResolveStation(hz, c.Latitude, c.Longitude);
        if (station?.Controller != ControllerType.Atis || string.IsNullOrWhiteSpace(station.Name))
            return null;

        string? clean = FlightPlan.CleanAirportName(station.Name);
        return new AtisStation(null, string.IsNullOrWhiteSpace(clean) ? station.Name : clean!);
    }

    /// <summary>Premier terrain candidat dont la fréquence ATIS publiée est celle qu'on écoute.</summary>
    private string? MatchingIcao(double hz, ContextSnapshot c)
    {
        var plan = _plans.Current;
        string?[] candidates =
        {
            _stations.OperationalAirport(c.NearestAirportIcao, c.Latitude, c.Longitude),
            plan?.DestinationIcao,
            plan?.OriginIcao,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string icao = candidate!.Trim().ToUpperInvariant();
            double? atis = _stations.FindFrequencyHz(icao, ControllerType.Atis);
            if (atis is not null && FrequencyFormatter.SameChannel(atis.Value, hz)) return icao;
        }
        return null;
    }

    // ------------------------------------------------------------------ diffusion en boucle

    private void StartBroadcast(AtisStation station)
    {
        StopBroadcast();   // ceinture et bretelles : jamais deux boucles en parallèle

        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts = cts = new CancellationTokenSource();
        }

        // Nouvelle station : le bulletin de la précédente ne vaut plus rien.
        _published = null;
        _digest = "";
        _spokenText = "";
        _spokenAudio = TtsAudio.Empty;

        _ = LoopAsync(station, cts);
    }

    /// <summary>
    /// Arrête la diffusion. On ANNULE seulement : la source est libérée par la boucle
    /// elle-même en sortant. Annuler puis libérer ici laisserait la boucle, encore en train
    /// de se terminer, manipuler un jeton dont la source a disparu.
    /// </summary>
    private void StopBroadcast()
    {
        lock (_gate)
        {
            _cts?.Cancel();   // coupe aussi la diffusion en cours (le jeton va jusqu'au lecteur audio)
            _cts = null;
        }
    }

    private async Task LoopAsync(AtisStation station, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Delay(FirstBroadcastDelay, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var report = CurrentReport(station);
                if (report is null)
                {
                    // Météo pas encore reçue du simulateur : on repasse, plutôt que de
                    // diffuser un bulletin creux.
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    continue;
                }

                TtsAudio audio = await SpokenAsync(report, station, ct).ConfigureAwait(false);
                if (!audio.IsEmpty)
                {
                    // Canal voix PARTAGÉ : l'ATC passe devant. Si quelqu'un parle on abandonne
                    // ce passage — une station qui tourne en boucle ne perd rien à attendre.
                    await _voice.SpeakAsync(audio, AtisProfile(), TimeSpan.Zero, ct, VoicePriority.Ambient).ConfigureAwait(false);
                }

                await Task.Delay(RepeatGap(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* fréquence quittée : fin normale */ }
        catch (Exception ex) { Debug.WriteLine("[WilcoATC/Atis] " + ex); }
        finally { cts.Dispose(); }
    }

    private TimeSpan RepeatGap()
        => TimeSpan.FromSeconds(Math.Clamp(_settings.Current.AtisRepeatGapSeconds, 2, 120));

    /// <summary>
    /// Bulletin à diffuser. Il ne change QUE lors d'une publication : entre deux, on rejoue
    /// le même texte — donc le même audio — quelles que soient les rafales du simulateur.
    /// </summary>
    private AtisReport? CurrentReport(AtisStation station)
    {
        var w = _weather; var c = _context;
        if (w is null || c is null) return _published;

        var conditions = AtisSurface.Reduce(w, c.AltitudeAglFeet, c.Latitude);
        string? runway = RunwayInUse(station, conditions, c);
        string digest = conditions.Digest(runway);

        if (_published is null)
        {
            // Première écoute : on ne part pas systématiquement d'« information Alpha ». Un
            // ATIS tourne depuis des heures quand on arrive dessus ; la lettre de départ est
            // tirée du terrain (stable pour tout le vol, différente d'un terrain à l'autre).
            return Publish(station, SeedLetter(station.Key), conditions, runway, w.ZuluTime, digest);
        }

        if (digest != _digest && DateTime.UtcNow - _publishedAt >= MinPublishInterval)
            return Publish(station, NextLetter(_published.Letter), conditions, runway, w.ZuluTime, digest);

        return _published;
    }

    private AtisReport Publish(AtisStation station, char letter, AtisConditions conditions,
                               string? runway, TimeSpan zulu, string digest)
    {
        _published = new AtisReport(station.Name, station.Icao, letter, conditions, runway, zulu);
        _digest = digest;
        _publishedAt = DateTime.UtcNow;
        return _published;
    }

    /// <summary>
    /// Piste en service. Le plan de vol fait foi quand on est bien à l'aéroport concerné —
    /// à l'arrivée en vol, la piste de destination ; au sol, celle du départ. Sinon on retient
    /// la piste la mieux orientée face au vent, ce qui est le critère d'un vrai terrain.
    ///
    /// Vent CALME et pas de plan : on n'annonce aucune piste. La piste préférentielle d'un
    /// terrain par vent nul est une donnée locale qu'on n'a pas — et un ATIS qui envoie sur
    /// la mauvaise piste est plus nuisible qu'un ATIS qui n'en parle pas.
    /// </summary>
    private string? RunwayInUse(AtisStation station, AtisConditions conditions, ContextSnapshot c)
    {
        var plan = _plans.Current;
        if (station.Icao is not null && plan is not null)
        {
            string? planned = c.OnGround
                ? Runway(plan.OriginIcao, plan.OriginRunway, station.Icao)
                    ?? Runway(plan.DestinationIcao, plan.DestinationRunway, station.Icao)
                : Runway(plan.DestinationIcao, plan.DestinationRunway, station.Icao)
                    ?? Runway(plan.OriginIcao, plan.OriginRunway, station.Icao);
            if (planned is not null) return planned;
        }

        return conditions.IsCalm ? null : AtisSurface.RunwayFacing(conditions.WindDirectionDeg);
    }

    private static string? Runway(string? planIcao, string? planRunway, string stationIcao)
        => !string.IsNullOrWhiteSpace(planRunway)
           && string.Equals(planIcao?.Trim(), stationIcao, StringComparison.OrdinalIgnoreCase)
            ? planRunway!.Trim()
            : null;

    // ------------------------------------------------------------------ voix & audio

    /// <summary>
    /// Synthèse du bulletin, MISE EN CACHE : un ATIS est un enregistrement, pas un
    /// contrôleur qui redit son texte. Tant que le bulletin n'a pas changé, on rejoue la
    /// même bande — même intonation, et aucune synthèse à chaque passage.
    /// </summary>
    private async Task<TtsAudio> SpokenAsync(AtisReport report, AtisStation station, CancellationToken ct)
    {
        string text = AtisComposer.Compose(report);
        if (text == _spokenText && !_spokenAudio.IsEmpty) return _spokenAudio;

        var audio = await _tts.SynthesizeAsync(text, AtisVoice(station), ct).ConfigureAwait(false);
        _spokenText = text;
        _spokenAudio = audio;
        AtisPublished?.Invoke(text);
        return audio;
    }

    /// <summary>Voix propre à la station, stable pendant tout le vol (comme un contrôleur).</summary>
    private TtsVoice AtisVoice(AtisStation station)
        => _picker.For("ATIS " + station.Key, AtcLanguage.English, ControllerType.Atis);

    /// <summary>
    /// Effet radio complet, MAIS sans déclic d'alternat : une station ATIS émet en continu,
    /// personne n'y appuie sur un bouton. Un peu en retrait aussi — c'est un fond qu'on
    /// écoute, pas quelqu'un qui vous parle.
    /// </summary>
    private RadioProfile AtisProfile()
    {
        var p = _settings.Current.ToRadioProfile();
        p.Squelch = false;
        p.Volume = Math.Clamp(p.Volume * 0.85, 0, 1);
        return p;
    }

    // ------------------------------------------------------------------ lettre du bulletin

    private static char NextLetter(char letter) => letter >= 'Z' ? 'A' : (char)(letter + 1);

    /// <summary>
    /// Lettre de départ, tirée du terrain par hachage FNV-1a (et non <c>GetHashCode</c>, qui
    /// varie d'un lancement à l'autre) : stable pendant tout le vol, et deux terrains ne
    /// démarrent pas sur la même lettre.
    /// </summary>
    private static char SeedLetter(string key)
    {
        uint hash = 2166136261;
        foreach (char ch in key)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (char)('A' + hash % 26);
    }

    public void Dispose()
    {
        _sim.RadioSnapshotReceived -= OnRadio;
        _sim.ContextReceived -= OnContext;
        _sim.WeatherReceived -= OnWeather;
        _sim.StateChanged -= OnState;
        StopBroadcast();
    }
}
