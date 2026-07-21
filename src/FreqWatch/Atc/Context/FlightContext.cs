using FreqWatch.Common;

namespace FreqWatch.Atc.Context;

/// <summary>État courant utilisé par l'AtcBrain pour valider une intention.</summary>
public sealed record FlightContext(
    FlightPhase Phase,
    ControllerType Controller,
    bool OnGround,
    string Callsign,
    string StationName,
    string? AirportIcao);
