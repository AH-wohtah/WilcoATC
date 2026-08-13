using WilcoATC.Sim;

namespace WilcoATC.Traffic;

/// <summary>
/// Un appareil du trafic, tel qu'on le connaît à un instant donné : son identité et son état
/// de vol réunis. Les deux arrivent par des relevés séparés — voir <see cref="TrafficPicture"/>.
/// </summary>
public sealed record TrafficAircraft(NearbyAircraft Identity, NearbyAircraftState State)
{
    public uint ObjectId => Identity.ObjectId;

    /// <summary>
    /// Indicatif PRONONÇABLE. « Air France » + « 1234 » donne « Air France 1234 » ; sans
    /// compagnie, on retombe sur l'immatriculation, qui est toujours renseignée.
    ///
    /// Les paquets de trafic ne remplissent pas toujours ces champs : quand rien n'est
    /// exploitable, on renvoie une chaîne vide, et l'appelant s'abstient de parler plutôt
    /// que d'inventer un indicatif — un contrôleur qui s'adresse à un avion inexistant est
    /// pire qu'un contrôleur silencieux.
    /// </summary>
    public string Callsign
    {
        get
        {
            string airline = Identity.Airline.Trim();
            string number = Identity.FlightNumber.Trim();
            if (airline.Length > 0 && number.Length > 0) return $"{airline} {number}";
            if (airline.Length > 0) return airline;
            return Identity.TailNumber.Trim();
        }
    }
}

/// <summary>
/// IMAGE VIVANTE DU TRAFIC autour du joueur : qui est là, où, et dans quel état.
///
/// Elle n'écrit RIEN dans le simulateur. C'est tout l'intérêt de l'approche : le trafic est
/// déjà produit par le simulateur et ses extensions (FSLTL et consorts), avec de vraies
/// compagnies et de vraies livrées. Chercher à le fabriquer nous-même reviendrait à refaire
/// moins bien ce qui existe — et nous ramènerait aux appareils pilotés de l'extérieur, dont
/// le déplacement saccadé est un défaut connu et sans remède propre.
///
/// On se contente donc de REGARDER, pour pouvoir en PARLER.
/// </summary>
public sealed class TrafficPicture
{
    /// <summary>
    /// Au-delà, un appareil est considéré comme disparu. Généreux à dessein : un relevé peut
    /// manquer un appareil sans qu'il ait quitté le monde, et oublier trop vite ferait
    /// resurgir sans cesse des appareils « nouveaux » qui n'ont jamais bougé.
    /// </summary>
    private static readonly TimeSpan Forget = TimeSpan.FromSeconds(30);

    private readonly Dictionary<uint, NearbyAircraft> _identities = new();
    private readonly Dictionary<uint, (NearbyAircraftState State, DateTime At)> _states = new();
    private readonly object _gate = new();

    /// <summary>Horloge injectable — le banc d'essai ne dépend pas de l'heure réelle.</summary>
    private readonly Func<DateTime> _now;

    public TrafficPicture(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>
    /// L'identité est mémorisée SANS DATE : elle ne périme pas. Un appareil garde son
    /// indicatif tant qu'il existe, et une identité conservée en trop ne coûte qu'un peu de
    /// mémoire — alors qu'une identité oubliée trop tôt rendrait muet un appareil bien visible.
    /// </summary>
    public void Observe(NearbyAircraft identity)
    {
        lock (_gate) _identities[identity.ObjectId] = identity;
    }

    public void Observe(NearbyAircraftState state)
    {
        lock (_gate) _states[state.ObjectId] = (state, _now());
    }

    /// <summary>
    /// Le trafic connu : appareils dont on a À LA FOIS l'identité et un état récent. Un
    /// appareil dont on ignore l'un des deux n'est pas listé — on ne saurait ni comment
    /// l'appeler, ni où il se trouve.
    /// </summary>
    public IReadOnlyList<TrafficAircraft> Current()
    {
        var cutoff = _now() - Forget;
        lock (_gate)
        {
            return _states
                .Where(kv => kv.Value.At >= cutoff && _identities.ContainsKey(kv.Key))
                .Select(kv => new TrafficAircraft(_identities[kv.Key], kv.Value.State))
                .ToList();
        }
    }

    /// <summary>Oublie les appareils trop anciens. À appeler de loin en loin.</summary>
    public void Prune()
    {
        var cutoff = _now() - Forget;
        lock (_gate)
        {
            var gone = _states.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList();
            foreach (var id in gone) { _states.Remove(id); _identities.Remove(id); }
        }
    }

    public int Count { get { lock (_gate) return _states.Count; } }
}
