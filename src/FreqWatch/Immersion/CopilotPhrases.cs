using System.Collections.Generic;

namespace FreqWatch.Immersion;

/// <summary>
/// Texte des annonces copilote. Volontairement EN C# (et non dans les dictionnaires de
/// langue de l'interface) : c'est de la phraséologie, pas de l'habillage.
///
/// ANGLAIS UNIQUEMENT pour l'instant — c'est de toute façon le standard mondial des
/// callouts (« V one », « rotate », « positive rate »), y compris dans les compagnies
/// non anglophones.
/// </summary>
public static class CopilotPhrases
{
    private static readonly Dictionary<string, string> En = new()
    {
        ["eighty"] = "Eighty knots.",
        ["v1"] = "V one.",
        ["rotate"] = "Rotate.",
        ["v2"] = "V two.",
        ["positive_rate"] = "Positive rate.",
        ["gear_up"] = "Gear up.",
        ["climb_1000"] = "One thousand feet.",
        ["ten_thousand_up"] = "Ten thousand feet.",
        ["ten_thousand_down"] = "Ten thousand feet.",
        ["app_1000"] = "One thousand.",
        ["app_500"] = "Five hundred.",
        ["minimums"] = "Minimums.",
        ["app_100"] = "One hundred.",
        ["app_50"] = "Fifty.",
        ["spoilers"] = "Spoilers up.",
        ["reverse"] = "Reverse green.",
        ["seventy"] = "Seventy knots.",
        ["chk_before_start"] = "Before start checklist.",
        ["chk_before_takeoff"] = "Before takeoff checklist.",
        ["chk_after_takeoff"] = "After takeoff checklist.",
        ["chk_approach"] = "Approach checklist.",
        ["chk_after_landing"] = "After landing checklist.",
        ["chk_shutdown"] = "Shutdown checklist.",
    };

    /// <summary>Phrase à prononcer pour une clé d'annonce, ou null si inconnue.</summary>
    public static string? Text(string key) => En.TryGetValue(key, out var s) ? s : null;
}
