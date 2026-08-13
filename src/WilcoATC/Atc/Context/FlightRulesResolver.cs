using WilcoATC.Atc.Planning;
using WilcoATC.Settings;
using WilcoATC.Sim;

namespace WilcoATC.Atc.Context;

/// <summary>Règles de vol retenues, et POURQUOI (le « pourquoi » est affiché dans l'UI).</summary>
public readonly record struct FlightRulesDecision(
    FlightRules Rules, AircraftClass Class, string Reason);

/// <summary>
/// Décide des règles de vol par une cascade de FIABILITÉ DÉCROISSANTE — même principe que
/// la résolution de fréquence Centre : on part de ce qui est certain, on ne devine qu'en
/// dernier recours, et on n'invente jamais.
///
///  1. Réglage explicite de l'utilisateur — il a toujours le dernier mot.
///  2. Règles déclarées par le plan de vol (champ SimBrief), quand il y en a.
///  3. Présence d'un SID : on ne dépose pas de procédure de départ pour un vol à vue.
///  4. Gabarit de l'appareil : un piston léger vole en VFR, le reste aux instruments.
///  5. Rien d'exploitable -> IFR, le comportement historique de l'application.
///
/// Logique PURE (aucun état, aucun effet de bord) donc testable.
/// </summary>
public static class FlightRulesResolver
{
    public static FlightRulesDecision Resolve(
        FlightRulesMode mode, FlightPlan? plan, AircraftSnapshot? aircraft)
    {
        var cls = AircraftClassifier.Classify(aircraft);

        // 1. Choix imposé par l'utilisateur.
        if (mode == FlightRulesMode.ForceVfr)
            return new FlightRulesDecision(FlightRules.Vfr, cls, "forced by settings");
        if (mode == FlightRulesMode.ForceIfr)
            return new FlightRulesDecision(FlightRules.Ifr, cls, "forced by settings");

        // 2. Le plan le dit lui-même.
        if (plan?.DeclaredRules is { } declared)
            return new FlightRulesDecision(declared, cls, "declared in the flight plan");

        // 3. Un SID déposé ne laisse guère de doute.
        if (!string.IsNullOrWhiteSpace(plan?.SidName))
            return new FlightRulesDecision(FlightRules.Ifr, cls, "flight plan has a SID");

        // 4. Déduction sur le gabarit.
        if (cls != AircraftClass.Unknown)
            return new FlightRulesDecision(
                AircraftClassifier.DefaultRulesFor(cls), cls,
                $"inferred from aircraft ({AircraftClassifier.Label(cls)})");

        // 5. Aucune information.
        return new FlightRulesDecision(FlightRules.Ifr, cls, "no aircraft data yet");
    }
}
