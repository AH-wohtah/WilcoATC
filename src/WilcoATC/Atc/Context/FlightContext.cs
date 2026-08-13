using WilcoATC.Common;

namespace WilcoATC.Atc.Context;

/// <summary>État courant utilisé par l'AtcBrain pour valider une intention.</summary>
public sealed record FlightContext(
    FlightPhase Phase,
    ControllerType Controller,
    bool OnGround,
    string Callsign,
    string StationName,
    string? AirportIcao,
    // Règles de vol et gabarit : ils ne VALIDENT rien (comme les phases, ils n'interdisent
    // jamais), ils choisissent la PHRASÉOLOGIE et la séquence de contrôleurs.
    FlightRules Rules = FlightRules.Ifr,
    AircraftClass Class = AircraftClass.Unknown,
    // Cap vrai courant : sert à nommer la piste RÉELLE (celle sur laquelle l'avion est aligné),
    // au lieu de la piste du plan de vol quand on ne part pas de l'aéroport prévu.
    double HeadingDeg = 0,
    // Vent du simulateur (direction VRAIE d'où il vient, et force). C'est lui qui désigne la
    // piste en service : un contrôleur fait décoller face au vent, pas dans l'axe du cap
    // qu'affiche l'avion à l'instant où il parle.
    double WindFromDeg = 0,
    double WindKnots = 0);
