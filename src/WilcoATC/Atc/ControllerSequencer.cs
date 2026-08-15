using WilcoATC.Atc.Context;

namespace WilcoATC.Atc;

/// <summary>Position de contrôle le long du vol.</summary>
public enum ControllerPosition
{
    Ground, Tower, Departure, Center, Approach, ArrivalTower, ArrivalGround,

    /// <summary>
    /// VFR : zone de départ quittée, pas encore en vue de l'arrivée. Il n'y a alors PAS de
    /// contrôleur attitré — c'est l'équivalent d'un « frequency change approved ». On garde
    /// malgré tout une position nommée pour que la machine à états reste explicite.
    /// </summary>
    VfrEnroute,
}

/// <summary>État de vol nécessaire pour décider des transferts (dérivé de SimConnect).</summary>
public readonly record struct FlightState(
    bool OnGround, double AglFeet, double MslFeet, double VerticalSpeedFpm,
    double GroundSpeedKnots, double DistToArrNm);

/// <summary>
/// Machine à états PURE (testable) de l'enchaînement des contrôleurs. DEUX enchaînements,
/// selon les règles de vol — c'est la même machine, pas la même carte :
///
///  • IFR : Tour → Départ → Centre → Approche → Tour arrivée → Sol arrivée ;
///  • VFR : Tour → (zone quittée) → Tour arrivée → Sol arrivée. Ni Centre ni Approche :
///    un vol à vue ne se fait pas guider aux instruments, il quitte la zone et rappelle à
///    l'arrivée pour s'intégrer au circuit.
///
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

    // --- Seuils propres au VFR ---
    /// <summary>Hauteur au-dessus de laquelle on considère la zone de départ quittée.</summary>
    public const double VfrLeaveZoneAglFeet = 2000;

    /// <summary>
    /// Distance à l'arrivée en deçà de laquelle on rappelle la tour pour s'intégrer.
    /// Sert AUSSI de garde pour le tour de piste : tant qu'on tourne autour de son propre
    /// terrain on reste sous ce seuil, donc on ne quitte jamais la fréquence — c'est
    /// exactement ce qu'on veut, un circuit ne se fait pas en changeant de contrôleur.
    /// </summary>
    public const double VfrJoinDistanceNm = 10;

    /// <summary>Règles de vol courantes : elles choisissent l'enchaînement à suivre.</summary>
    public FlightRules Rules { get; set; } = FlightRules.Ifr;

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
    public static ControllerPosition PositionFor(FlightState s, FlightRules rules = FlightRules.Ifr)
    {
        if (s.OnGround) return ControllerPosition.Tower;

        // VFR : pas de Centre ni d'Approche à rejoindre. Soit on est en vue de l'arrivée
        // (on s'intègre), soit on est en route et personne ne nous suit.
        if (rules == FlightRules.Vfr)
            return s.DistToArrNm < VfrJoinDistanceNm
                ? ControllerPosition.ArrivalTower
                : ControllerPosition.VfrEnroute;

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
        var next = Rules == FlightRules.Vfr ? NextForVfr(Current, s) : NextFor(Current, s, SkipCenter);
        if (next is { } n && n != Current) { Current = n; return n; }
        return null;
    }

    /// <summary>
    /// Enchaînement VFR. Trois transitions seulement, et la garde de distance fait tout le
    /// travail : tant qu'on reste près de son terrain (tour de piste, essais), on ne quitte
    /// jamais la tour.
    /// </summary>
    private static ControllerPosition? NextForVfr(ControllerPosition cur, FlightState s) => cur switch
    {
        // Zone quittée : la tour libère la fréquence. On s'éloigne ET on est monté.
        ControllerPosition.Tower when !s.OnGround
                                   && s.AglFeet > VfrLeaveZoneAglFeet
                                   && s.DistToArrNm > VfrJoinDistanceNm
            => ControllerPosition.VfrEnroute,

        // En vue de l'arrivée : on rappelle la tour pour s'intégrer au circuit.
        ControllerPosition.VfrEnroute when s.DistToArrNm < VfrJoinDistanceNm
            => ControllerPosition.ArrivalTower,

        ControllerPosition.ArrivalTower when s.OnGround && s.GroundSpeedKnots < ArrivalSlowKnots
            => ControllerPosition.ArrivalGround,

        _ => null,
    };

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
            ControllerPosition.Departure when descendingNearArrival
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
