using FreqWatch.Sim;

namespace FreqWatch.Atc.Context;

/// <summary>
/// Machine à états de la phase de vol AVEC MÉMOIRE.
///
/// Les SimVars instantanés ne NOMMENT pas la phase : ils DÉCLENCHENT des transitions.
/// Point critique — <see cref="HasBeenAirborne"/> ne passe à true QUE si l'avion est
/// vraiment en vol, c'est-à-dire : pas au sol, EN ALTITUDE (AGL &gt; seuil) ET AVEC DE
/// LA VITESSE SOL. Cette triple condition + un anti-rebond écartent les parasites du
/// chargement (un « on-ground = false » ou un pic d'AGL alors que l'avion est immobile),
/// qui faisaient basculer à tort en « airborne » -> TAXI_IN -> pushback refusé.
/// </summary>
public sealed class FlightPhaseEstimator
{
    private const double AirborneAglFeet = 100;   // au-dessus -> potentiellement en vol
    private const double AirborneMinGs = 30;       // ... mais il faut aussi de la vitesse (écarte l'immobile)
    private const int AirborneConfirmFrames = 2;   // anti-rebond : N frames consécutifs
    private const double ApproachAglFeet = 3000;
    private const double TaxiSpeedKts = 3;
    private const double FastSpeedKts = 40;
    private const double DescendFpm = -300;

    private bool _initialized;
    private int _airborneStreak;
    private FlightPhase _phase = FlightPhase.Unknown;

    public bool HasBeenAirborne { get; private set; }
    public FlightPhase Phase => _phase;

    public void Reset()
    {
        _initialized = false;
        _airborneStreak = 0;
        _phase = FlightPhase.Unknown;
        HasBeenAirborne = false;
    }

    public FlightPhase Estimate(ContextSnapshot c)
    {
        double gs = c.GroundSpeedKnots;
        double agl = c.AltitudeAglFeet;
        double vs = c.VerticalSpeedFpm;
        bool onGround = c.OnGround;

        // Vraiment en vol : pas au sol + en altitude + avec de la vitesse.
        bool airborneNow = !onGround && agl > AirborneAglFeet && gs > AirborneMinGs;

        // Latch anti-rebond : plusieurs frames consécutifs requis pour acter un vol.
        if (airborneNow)
        {
            _airborneStreak++;
            if (_airborneStreak >= AirborneConfirmFrames) HasBeenAirborne = true;
        }
        else
        {
            _airborneStreak = 0;
        }

        if (!_initialized)
        {
            _initialized = true;
            _phase = airborneNow ? FlightPhase.Airborne : FlightPhase.Parked;
        }

        // RETOUR AU PARKING APRÈS UN VOL : au sol, à l'arrêt, frein de parking serré ET on a
        // déjà volé -> on EFFACE la mémoire de vol pour qu'un 2ᵉ vol reparte à zéro.
        // IMPORTANT : on exige HasBeenAirborne pour ne PAS retomber en « Parked » quand on
        // serre le frein au POINT D'ARRÊT avant le décollage (sinon TaxiOut -> Parked et
        // « prêt au départ » serait refusé pour cause de phase). Avant le 1er vol, la logique
        // normale ci-dessous garde correctement Parked (au gate) ou TaxiOut (au point d'arrêt).
        if (HasBeenAirborne && onGround && gs < TaxiSpeedKts && c.ParkingBrake)
        {
            HasBeenAirborne = false;
            _airborneStreak = 0;
            _phase = FlightPhase.Parked;
            return _phase;
        }

        if (airborneNow)
        {
            _phase = (vs < DescendFpm && agl < ApproachAglFeet) ? FlightPhase.Approach : FlightPhase.Airborne;
            return _phase;
        }

        // Pas en vol : on décide par la VITESSE SOL (robuste aux "on-ground" fantaisistes).
        if (gs > FastSpeedKts)
        {
            // ATTERRISSAGE seulement si on ARRIVE de l'air (vol / approche / atterrissage en
            // cours) ; sinon c'est un roulage au DÉCOLLAGE — même si HasBeenAirborne est resté
            // vrai d'un vol précédent (frein de parking non serré au parking).
            bool fromAir = _phase is FlightPhase.Airborne or FlightPhase.Approach or FlightPhase.Landing;
            _phase = (HasBeenAirborne && fromAir) ? FlightPhase.Landing : FlightPhase.Takeoff; // roulage rapide
        }
        else if (gs > TaxiSpeedKts)
        {
            _phase = HasBeenAirborne ? FlightPhase.TaxiIn : FlightPhase.TaxiOut;
        }
        else
        {
            // Arrêté : TAXI_IN seulement si on a réellement volé ; sinon mémoire du départ.
            if (HasBeenAirborne)
                _phase = FlightPhase.TaxiIn;
            else
                _phase = _phase switch
                {
                    FlightPhase.TaxiOut => FlightPhase.TaxiOut, // arrêté au point d'arrêt
                    FlightPhase.Takeoff => FlightPhase.TaxiOut,
                    _ => FlightPhase.Parked,                    // au parking (spawn / chargement)
                };
        }

        return _phase;
    }
}
