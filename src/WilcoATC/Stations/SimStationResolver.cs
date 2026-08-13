using System.Collections.Concurrent;
using WilcoATC.Common;
using WilcoATC.Sim;

namespace WilcoATC.Stations;

/// <summary>
/// Décorateur : le SIMULATEUR (SimConnect Facilities Data API) est la source de fréquences qui
/// FAIT FOI — la liste affichée correspond exactement à ce que le joueur voit dans le simu
/// (navdata/scenery installés compris). OurAirports (+ overlay) ne sert que de REPLI quand le
/// simu ne renvoie rien pour un terrain (hors ligne, ou terrain absent de la navdata). Toute la
/// géographie (aéroport proche, position) reste au CSV.
///
/// La donnée sim arrive de façon ASYNCHRONE (requête → réponse) alors que l'interface est
/// synchrone : on la demande à la volée (une fois par ICAO), on répond au CSV en attendant,
/// puis on lève <see cref="FrequenciesUpdated"/> à l'arrivée pour que l'UI se rafraîchisse.
/// Toute la GÉOGRAPHIE (aéroport proche, position, « operational airport ») reste au CSV : la
/// facility data est ponctuelle (par ICAO) et n'indexe pas le monde.
/// </summary>
public sealed class SimStationResolver : IStationResolver
{
    // Tolérances alignées sur OurAirportsStationResolver (canal 8.33 kHz + rayon de pertinence).
    private const double FreqToleranceMhz = 0.006;
    private const double MaxDistanceMeters = 150_000;

    private readonly OurAirportsStationResolver _csv;
    private readonly ISimConnectService _sim;

    /// <summary>
    /// L'utilisateur autorise-t-il le repli sur les fréquences RÉELLES quand le simulateur
    /// ne publie rien ? Faux = on ne cite que ce qui existe dans le jeu (voir
    /// <see cref="Settings.AppSettings.UseRealWorldFrequencies"/>).
    /// </summary>
    private readonly Func<bool> _allowRealWorld;

    // ICAO -> fréquences live (converties + classées). Écrit sur le thread de pompe, lu sur
    // l'UI/ATC : ConcurrentDictionary.
    private readonly ConcurrentDictionary<string, IReadOnlyList<AirportFrequency>> _simFreqs =
        new(StringComparer.OrdinalIgnoreCase);

    // ICAO déjà demandés (évite de spammer RequestFacilityData). Ré-armé à la déconnexion.
    private readonly ConcurrentDictionary<string, byte> _requested =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? FrequenciesUpdated;

    public SimStationResolver(OurAirportsStationResolver csv, ISimConnectService sim,
                              Func<bool>? allowRealWorld = null)
    {
        _csv = csv;
        _sim = sim;
        _allowRealWorld = allowRealWorld ?? (() => true);
        _sim.AirportFrequenciesReceived += OnSimFrequencies;
        _sim.StateChanged += OnSimStateChanged;
    }

    // ---------------------------------------------------------- réception des fréquences live

    private void OnSimStateChanged(ConnectionState state, string? detail)
    {
        // Déconnexion : on ré-arme les demandes (le pilote peut relancer un autre simu/scenery).
        // Les fréquences déjà obtenues restent en cache — elles décrivent des aéroports, pas
        // l'état de vol.
        if (state != ConnectionState.Connected) _requested.Clear();
    }

    private void OnSimFrequencies(AirportFacilityFrequencies data)
    {
        if (string.IsNullOrWhiteSpace(data.Icao)) return;

        var list = ControllerTaxonomy.BuildList(
            data.Frequencies.Select(s => (s.Mhz, (string?)s.Name, (int?)s.Type)));

        // On mémorise même une liste vide (« le sim a répondu, rien à demander de plus ») : les
        // getters retombent alors sur le CSV, mais on ne re-demande pas en boucle.
        _simFreqs[data.Icao] = list;
        FrequenciesUpdated?.Invoke(data.Icao);
    }

    private void EnsureRequested(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return;
        if (_sim.State != ConnectionState.Connected) return;
        string code = icao!.Trim().ToUpperInvariant();
        if (_requested.TryAdd(code, 1))
            _sim.RequestAirportFrequencies(code);
    }

    private bool TrySimFreqs(string? icao, out IReadOnlyList<AirportFrequency> freqs)
    {
        freqs = Array.Empty<AirportFrequency>();
        if (string.IsNullOrWhiteSpace(icao)) return false;
        return _simFreqs.TryGetValue(icao!.Trim(), out freqs!) && freqs.Count > 0;
    }

    // ---------------------------------------------------------- fréquences (sim > CSV)

    public IReadOnlyList<AirportFrequency> ListFrequencies(string icao)
    {
        // CORRECTIONS IMPORTÉES par l'utilisateur : elles font autorité, MÊME sur le simulateur
        // (c'est tout l'intérêt d'une correction communautaire validée).
        if (_csv.IsUserOverride(icao)) return _csv.ListFrequencies(icao);

        EnsureRequested(icao);
        // Le SIMULATEUR FAIT FOI : dès qu'il a renvoyé des fréquences pour ce terrain, on n'affiche
        // QUE les siennes — la liste correspond alors exactement à ce que le joueur voit dans le
        // simu. OurAirports (+ overlay) n'est qu'un REPLI, et l'utilisateur peut le couper.
        if (TrySimFreqs(icao, out var sim)) return sim;

        return _allowRealWorld() ? _csv.ListFrequencies(icao) : Array.Empty<AirportFrequency>();
    }

    public double? FindFrequencyHz(string icao, ControllerType controller)
    {
        if (_csv.IsUserOverride(icao)) return _csv.FindFrequencyHz(icao, controller);

        EnsureRequested(icao);
        if (TrySimFreqs(icao, out var sim))
        {
            // Le sim fait foi pour ce terrain : on ne complète PAS avec OurAirports (sinon on
            // citerait une fréquence que le joueur ne voit pas dans le simu).
            foreach (var f in sim)
                if (f.Type == controller) return f.Mhz * 1_000_000.0;
            return null;
        }
        return _allowRealWorld() ? _csv.FindFrequencyHz(icao, controller) : null;
    }

    /// <summary>Importe/fusionne un CSV de fréquences dans l'overlay utilisateur (délégué au CSV).</summary>
    public int ImportOverlay(string csvPath) => _csv.ImportOverlay(csvPath);

    // ---------------------------------------------------------- fréquence tunée -> station

    public string? Resolve(double activeHz, double lat, double lon)
    {
        if (ResolveSimHit(activeHz, lat, lon) is { } h)
        {
            string label = ControllerTaxonomy.ShortLabel(h.Freq.Type);
            return string.IsNullOrEmpty(label) ? h.Name : $"{h.Name} · {label}";
        }
        // Sans repli autorisé, une fréquence inconnue du simulateur reste anonyme : l'ATC ne
        // saluera donc pas sur un canal que le jeu ne publie pas.
        return _allowRealWorld() ? _csv.Resolve(activeHz, lat, lon) : null;
    }

    public StationInfo? ResolveStation(double activeHz, double lat, double lon)
    {
        if (ResolveSimHit(activeHz, lat, lon) is { } h) return new StationInfo(h.Name, h.Freq.Type);
        return _allowRealWorld() ? _csv.ResolveStation(activeHz, lat, lon) : null;
    }

    // Parmi les aéroports dont on a les fréquences LIVE, le plus proche qui publie un canal
    // correspondant à la fréquence active. Sinon null (repli CSV).
    private (string Name, AirportFrequency Freq)? ResolveSimHit(double activeHz, double lat, double lon)
    {
        if (activeHz < 1_000_000 || _simFreqs.IsEmpty) return null;
        double mhz = activeHz / 1_000_000.0;

        (string Name, AirportFrequency Freq)? best = null;
        double bestDist = double.MaxValue;

        foreach (var kv in _simFreqs)
        {
            if (_csv.AirportPosition(kv.Key) is not { } p) continue;
            double d = Geo.DistanceMeters(lat, lon, p.Lat, p.Lon);
            if (d > MaxDistanceMeters || d >= bestDist) continue;

            foreach (var f in kv.Value)
            {
                if (Math.Abs(f.Mhz - mhz) > FreqToleranceMhz) continue;
                string? name = _csv.LookupAirportName(kv.Key);
                if (string.IsNullOrWhiteSpace(name)) break;
                best = (name!, f);
                bestDist = d;
                break;
            }
        }
        return best;
    }

    // ---------------------------------------------------------- géographie / noms : délégués au CSV

    public string? LookupAirportName(string icao) => _csv.LookupAirportName(icao);

    /// <summary>
    /// Recherche géographique dans les données RÉELLES (elle ne sert qu'au Centre, que le
    /// simulateur ne publie jamais par terrain). Coupée quand l'utilisateur exige les seules
    /// fréquences du jeu. Les secteurs en-route, eux, restent actifs : ils sont installés
    /// séparément et volontairement, précisément pour combler ce manque.
    /// </summary>
    public double? FindNearestFrequencyHz(ControllerType controller, double lat, double lon, double maxKm)
        => _allowRealWorld() ? _csv.FindNearestFrequencyHz(controller, lat, lon, maxKm) : null;

    public string? NearestControlledAirportIcao(double lat, double lon, bool includeSmallFields = false)
        => _csv.NearestControlledAirportIcao(lat, lon, includeSmallFields);

    public (double Lat, double Lon)? AirportPosition(string icao) => _csv.AirportPosition(icao);

    public string? OperationalAirport(string? nearestIcao, double lat, double lon)
        => _csv.OperationalAirport(nearestIcao, lat, lon);
}
