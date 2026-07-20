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

    public void Reset() => Current = ControllerPosition.Tower;

    /// <summary>Renvoie la nouvelle position si un transfert a lieu, sinon null.</summary>
    public ControllerPosition? Update(FlightState s)
    {
        var next = NextFor(Current, s);
        if (next is { } n && n != Current) { Current = n; return n; }
        return null;
    }

    private static ControllerPosition? NextFor(ControllerPosition cur, FlightState s) => cur switch
    {
        ControllerPosition.Tower when !s.OnGround && s.AglFeet > TowerToDepartureAglFeet
            => ControllerPosition.Departure,
        ControllerPosition.Departure when s.MslFeet > DepartureToCenterAltFeet
            => ControllerPosition.Center,
        ControllerPosition.Center when s.VerticalSpeedFpm < DescendFpm && s.DistToArrNm < CenterToApproachDistanceNm
            => ControllerPosition.Approach,
        ControllerPosition.Approach when s.AglFeet < ApproachToTowerAglFeet && s.DistToArrNm < ApproachToArrivalNm
            => ControllerPosition.ArrivalTower,
        ControllerPosition.ArrivalTower when s.OnGround && s.GroundSpeedKnots < ArrivalSlowKnots
            => ControllerPosition.ArrivalGround,
        _ => null,
    };
}
