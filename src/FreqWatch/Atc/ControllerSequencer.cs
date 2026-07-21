namespace FreqWatch.Atc;

/// <summary>Position de contrôle le long du vol.</summary>
public enum ControllerPosition
{
    Ground, Tower, Departure, Center, Approach, ArrivalTower, ArrivalGround,
}

/// <summary>État de vol nécessaire pour décider des transferts (dérivé de SimConnect).</summary>
public readonly record struct FlightState(
    bool OnGround, double AglFeet, double MslFeet, double VerticalSpeedFpm,
    double GroundSpeedKnots, double DistToArrNm);

/// <summary>
/// Machine à états PURE (testable) de l'enchaînement des contrôleurs :
/// Tour → Départ → Centre → Approche → Tour arrivée → Sol arrivée.
/// <see cref="Update"/> renvoie la NOUVELLE position quand un transfert doit être annoncé.
/// Seuils = constantes claires et ajustables.
/// </summary>
public sealed class ControllerSequencer
{
    public const double TowerToDepartureAglFeet = 2500;   // après décollage, en montée
    public const double DepartureToCenterAltFeet = 10000; // ~ FL100
    public const double CenterToApproachDistanceNm = 40;  // en descente près de l'arrivée
    public const double ApproachToArrivalNm = 15;         // établi sur l'approche finale
    public const double ApproachToTowerAglFeet = 2000;
    public const double ArrivalSlowKnots = 30;            // posé et ralenti
    private const double DescendFpm = -300;

    // On démarre avec la Tour (départ) : les transferts en route commencent une fois en l'air.
    public ControllerPosition Current { get; private set; } = ControllerPosition.Tower;

    /// <summary>
    /// Quand aucune fréquence Centre fiable n'existe, on SAUTE cette étape : le pilote reste
    /// avec le Départ jusqu'à l'Approche. Mieux vaut ne pas transférer du tout que d'annoncer
    /// « contact center » sans pouvoir dire sur quelle fréquence.
    /// </summary>
    public bool SkipCenter { get; set; }

    public void Reset()
    {
        Current = ControllerPosition.Tower;
        SkipCenter = false;
    }

    /// <summary>Reprend la séquence à une position donnée (démarrage en vol).</summary>
    public void StartAt(ControllerPosition position) => Current = position;

    /// <summary>
    /// Position de contrôle qui CORRESPOND à un état de vol donné. Sert quand l'application
    /// est lancée alors que le vol est déjà en cours : sans ça la séquence repartait de la
    /// Tour de départ et attendait un décollage qui n'arriverait jamais — donc plus aucun
    /// transfert de tout le vol.
    /// </summary>
    public static ControllerPosition PositionFor(FlightState s)
    {
        if (s.OnGround) return ControllerPosition.Tower;

        bool descending = s.VerticalSpeedFpm < DescendFpm;

        if (descending && s.DistToArrNm < ApproachToArrivalNm && s.AglFeet < ApproachToTowerAglFeet)
            return ControllerPosition.ArrivalTower;
        if (descending && s.DistToArrNm < CenterToApproachDistanceNm)
            return ControllerPosition.Approach;
        if (s.MslFeet > DepartureToCenterAltFeet)
            return ControllerPosition.Center;
        if (s.AglFeet > TowerToDepartureAglFeet)
            return ControllerPosition.Departure;

        return ControllerPosition.Tower;
    }

    /// <summary>Renvoie la nouvelle position si un transfert a lieu, sinon null.</summary>
    public ControllerPosition? Update(FlightState s)
    {
        var next = NextFor(Current, s, SkipCenter);
        if (next is { } n && n != Current) { Current = n; return n; }
        return null;
    }

    private static ControllerPosition? NextFor(ControllerPosition cur, FlightState s, bool skipCenter)
    {
        bool descendingNearArrival =
            s.VerticalSpeedFpm < DescendFpm && s.DistToArrNm < CenterToApproachDistanceNm;

        return cur switch
        {
            ControllerPosition.Tower when !s.OnGround && s.AglFeet > TowerToDepartureAglFeet
                => ControllerPosition.Departure,

            // Sans Centre exploitable, le Départ passe directement la main à l'Approche :
            // la chaîne reste vivante jusqu'à l'arrivée (c'est ce test EN PREMIER qui évite
            // de rebasculer indéfiniment vers un Centre qu'on ne peut pas annoncer).
            ControllerPosition.Departure when skipCenter && descendingNearArrival
                => ControllerPosition.Approach,
            ControllerPosition.Departure when !skipCenter && s.MslFeet > DepartureToCenterAltFeet
                => ControllerPosition.Center,

            ControllerPosition.Center when descendingNearArrival
                => ControllerPosition.Approach,
            ControllerPosition.Approach when s.AglFeet < ApproachToTowerAglFeet && s.DistToArrNm < ApproachToArrivalNm
                => ControllerPosition.ArrivalTower,
            ControllerPosition.ArrivalTower when s.OnGround && s.GroundSpeedKnots < ArrivalSlowKnots
                => ControllerPosition.ArrivalGround,
            _ => null,
        };
    }
}
