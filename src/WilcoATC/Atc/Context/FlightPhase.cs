namespace WilcoATC.Atc.Context;

/// <summary>Flight phase (ground -> air -> ground).</summary>
public enum FlightPhase
{
    Parked,    // on the ground, stationary (parking brake set / not rolling yet)
    Pushback,  // pushback approved (state driven by the ATC brain)
    TaxiOut,   // taxiing to the runway (or stopped at the holding point)
    Takeoff,   // take-off roll / initial climb
    Airborne,  // airborne (cruise / en route)
    Approach,  // descending towards the airport
    Landing,   // landing roll
    TaxiIn,    // taxiing to the stand after landing
    Unknown,
}
