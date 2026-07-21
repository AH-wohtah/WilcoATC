using FreqWatch.Atc.Context;

namespace FreqWatch.Atc;

/// <summary>
/// Logique PURE (donc testable) de l'ATC proactif : à chaque transition de phase,
/// décide quelle transmission l'ATC doit initier (clé d'événement), ou null.
///
/// Cas clés demandés :
///  - décollage SANS autorisation -> la tour rappelle (« takeoff_no_clearance ») ;
///  - décollage autorisé -> transfert de fréquence une fois en l'air (« handoff_departure ») ;
///  - puis contacts tout au long du vol : approche, atterrissage, roulage à l'arrivée.
/// </summary>
public static class FlightDirector
{
    public static string? OnPhaseTransition(FlightPhase prev, FlightPhase now, bool takeoffCleared)
    {
        // Décollage sans autorisation -> la tour rappelle. (Les transferts de fréquence
        // sont gérés par le ControllerSequencer, pas ici.)
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

    // « Unknown » n'est PAS le sol : c'est l'absence d'information. L'y inclure faisait
    // accuser le pilote d'un décollage sans autorisation dès qu'on rejoignait un vol déjà
    // en l'air (phase précédente inconnue -> phase Airborne = « il vient de décoller »).
    private static bool IsGround(FlightPhase p) =>
        p is FlightPhase.Parked or FlightPhase.Pushback or FlightPhase.TaxiOut;
}
