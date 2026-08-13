using WilcoATC.Atc.Context;

namespace WilcoATC.Atc;

/// <summary>
/// PURE (therefore testable) logic of the proactive ATC: on every flight-phase transition,
/// decides which transmission the controller should start (an event key), or null.
///
/// The cases that matter:
///  - taking off WITHOUT a clearance -> the tower calls you back ("takeoff_no_clearance");
///  - cleared take-off -> a frequency handoff once airborne ("handoff_departure");
///  - then calls throughout the flight: approach, landing, taxi in.
/// </summary>
public static class FlightDirector
{
    public static string? OnPhaseTransition(FlightPhase prev, FlightPhase now, bool takeoffCleared)
    {
        // Taking off without a clearance -> the tower calls back. (Frequency handoffs are
        // handled by the ControllerSequencer, not here.)
        if ((now == FlightPhase.Takeoff || now == FlightPhase.Airborne) && IsGround(prev) && !takeoffCleared)
            return "takeoff_no_clearance";

        if (now == FlightPhase.Approach && prev != FlightPhase.Approach)
            return "approach";

        if (now == FlightPhase.Landing && prev != FlightPhase.Landing)
            return "landing";

        if (now == FlightPhase.TaxiIn && prev == FlightPhase.Landing)
            return "taxi_in";

        return null;
    }

    // "Unknown" is NOT the ground: it is the absence of information. Including it here used
    // to accuse the pilot of an unauthorised take-off as soon as we joined a flight already
    // airborne (previous phase unknown -> phase Airborne = "they just took off").
    private static bool IsGround(FlightPhase p) =>
        p is FlightPhase.Parked or FlightPhase.Pushback or FlightPhase.TaxiOut;
}
