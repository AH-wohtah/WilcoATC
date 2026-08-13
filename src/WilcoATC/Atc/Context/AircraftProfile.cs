using WilcoATC.Sim;

namespace WilcoATC.Atc.Context;

/// <summary>Règles de vol appliquées par l'ATC. Détermine TOUT le comportement en aval.</summary>
public enum FlightRules
{
    /// <summary>Vol à vue : circuit, intégration, pas de niveau de vol, pas de SID/STAR.</summary>
    Vfr,
    /// <summary>Vol aux instruments : clairance, SID, transferts en route, approche aux instruments.</summary>
    Ifr,
}

/// <summary>
/// Gabarit de l'appareil. On reprend les catégories OACI de turbulence de sillage là où
/// elles existent (léger &lt; 7 t, lourd ≥ 136 t) plutôt que d'inventer des seuils.
/// </summary>
public enum AircraftClass
{
    /// <summary>Gabarit inconnu (SimConnect n'a pas encore répondu, ou avion exotique).</summary>
    Unknown,
    /// <summary>Aviation générale légère : piston, ≤ 7 t. Cessna, Cub, Bonanza…</summary>
    Light,
    /// <summary>Turbopropulseur : King Air, TBM, ATR, Dash 8.</summary>
    Turboprop,
    /// <summary>Jet d'affaires : Citation, Longitude, Vision Jet.</summary>
    BizJet,
    /// <summary>Avion de ligne : A320, 737, E-Jet.</summary>
    Airliner,
    /// <summary>Gros porteur au sens OACI : ≥ 136 t. 777, 747, A350.</summary>
    Heavy,
}

/// <summary>
/// Classe l'appareil et en DÉDUIT des règles de vol par défaut, à partir du seul gabarit
/// renvoyé par SimConnect. Logique PURE (aucune dépendance, aucun état) donc testable et
/// vérifiable à la lecture.
///
/// Le principe : le monde du piston est celui du VFR. Un turbopropulseur ou un jet vole
/// aux instruments par défaut — c'est le cas de très loin le plus courant, et l'utilisateur
/// garde le dernier mot via le réglage manuel (voir <see cref="FlightRulesResolver"/>).
/// </summary>
public static class AircraftClassifier
{
    /// <summary>Catégorie OACI « léger » : 7 tonnes.</summary>
    public const double LightMaxWeightLbs = 15_500;

    /// <summary>Catégorie OACI « lourd » : 136 tonnes.</summary>
    public const double HeavyMinWeightLbs = 300_000;

    /// <summary>Au-delà, un jet n'est plus un jet d'affaires (≈ 18,5 t).</summary>
    public const double BizJetMaxWeightLbs = 41_000;

    public static AircraftClass Classify(AircraftSnapshot? a)
    {
        if (a is null) return AircraftClass.Unknown;

        double weight = a.MaxGrossWeightLbs;
        var engine = a.Engine;

        // Sans masse exploitable, la motorisation seule tranche. Mieux vaut une classe
        // approximative qu'un « inconnu » qui ferait taire tout le comportement en aval.
        if (weight <= 0)
            return engine switch
            {
                EngineKind.Piston or EngineKind.None => AircraftClass.Light,
                EngineKind.HelicopterTurbine => AircraftClass.Light,
                EngineKind.Turboprop => AircraftClass.Turboprop,
                EngineKind.Jet => AircraftClass.Airliner,
                _ => AircraftClass.Unknown,
            };

        if (weight >= HeavyMinWeightLbs) return AircraftClass.Heavy;

        // Le piston reste léger quelle que soit la masse : un bimoteur piston lourd
        // (Beech 18…) appartient au monde de l'aviation générale, pas à celui des lignes.
        if (engine is EngineKind.Piston or EngineKind.None or EngineKind.HelicopterTurbine)
            return AircraftClass.Light;

        if (engine == EngineKind.Turboprop) return AircraftClass.Turboprop;

        if (engine == EngineKind.Jet)
            return weight < BizJetMaxWeightLbs ? AircraftClass.BizJet : AircraftClass.Airliner;

        // Motorisation non renseignée : la masse décide.
        if (weight <= LightMaxWeightLbs) return AircraftClass.Light;
        return weight < BizJetMaxWeightLbs ? AircraftClass.BizJet : AircraftClass.Airliner;
    }

    /// <summary>
    /// Règles de vol par DÉFAUT pour une classe. Ce n'est qu'un défaut : un plan de vol
    /// déposé ou un réglage explicite l'emportent (voir <see cref="FlightRulesResolver"/>).
    /// </summary>
    public static FlightRules DefaultRulesFor(AircraftClass c) => c switch
    {
        AircraftClass.Light => FlightRules.Vfr,
        AircraftClass.Unknown => FlightRules.Ifr, // sans information, le comportement historique
        _ => FlightRules.Ifr,
    };

    /// <summary>Libellé court pour l'interface et les journaux.</summary>
    public static string Label(AircraftClass c) => c switch
    {
        AircraftClass.Light => "Light aircraft",
        AircraftClass.Turboprop => "Turboprop",
        AircraftClass.BizJet => "Business jet",
        AircraftClass.Airliner => "Airliner",
        AircraftClass.Heavy => "Heavy",
        _ => "Unknown",
    };
}
