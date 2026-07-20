namespace FreqWatch.Atc.Context;

/// <summary>Phase de vol (séquence sol -> vol -> sol).</summary>
public enum FlightPhase
{
    Parked,    // au sol, arrêté (frein parking / pas encore roulé)
    Pushback,  // repoussage autorisé (état piloté par le cerveau ATC)
    TaxiOut,   // roulage vers la piste (ou arrêté au point d'arrêt)
    Takeoff,   // roulage au décollage / montée initiale
    Airborne,  // en vol (croisière / en route)
    Approach,  // en descente vers l'aéroport
    Landing,   // roulage à l'atterrissage
    TaxiIn,    // roulage vers le parking après atterrissage
    Unknown,
}
