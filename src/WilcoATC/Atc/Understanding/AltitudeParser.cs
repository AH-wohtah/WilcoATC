using System.Text.RegularExpressions;

namespace WilcoATC.Atc.Understanding;

/// <summary>
/// Extrait l'altitude CIBLE d'une demande pilote (« climb to flight level 350 »,
/// « descend to 8000 feet », « request higher »). Best-effort, DÉTERMINISTE et gratuit :
/// c'est le Niveau A. Les formulations vraiment tordues relèvent du LLM (Niveau B).
///
/// Travaille sur le texte DÉJÀ passé par <see cref="AtcTextNormalizer"/> (chiffres collés,
/// « flight level » recollé), donc « flight level three five zero » arrive ici en
/// « flight level 350 ». Classe PURE : entièrement testable.
/// </summary>
public static class AltitudeParser
{
    /// <param name="Feet">Altitude cible en pieds, ou null si aucune n'a été comprise.</param>
    /// <param name="Climb">true = montée, false = descente, null = direction non précisée.</param>
    /// <param name="AsFlightLevel">Citer en niveau de vol (« flight level 350 ») plutôt qu'en pieds.</param>
    public readonly record struct Result(int? Feet, bool? Climb, bool AsFlightLevel);

    public static Result Parse(string? rawText)
    {
        string t = AtcTextNormalizer.Normalize(rawText);
        if (t.Length == 0) return new Result(null, null, false);

        bool? climb = null;
        if (Regex.IsMatch(t, @"\b(climb|climbing|higher)\b")) climb = true;
        else if (Regex.IsMatch(t, @"\b(descend|descending|descent|lower)\b")) climb = false;

        // 1. Niveau de vol : « flight level 350 » / « fl 350 » -> 35000 ft.
        var fl = Regex.Match(t, @"\b(?:flight level|fl)\s*(\d{2,3})\b");
        if (fl.Success && int.TryParse(fl.Groups[1].Value, out int level) && level is >= 10 and <= 600)
            return new Result(level * 100, climb, AsFlightLevel: true);

        // 2. « 8 thousand [5 hundred] » -> 8500 ft (Whisper garde parfois « thousand » en toutes lettres).
        var th = Regex.Match(t, @"\b(\d{1,2})\s*thousand(?:\s*(\d)\s*hundred)?\b");
        if (th.Success && int.TryParse(th.Groups[1].Value, out int thousands))
        {
            int feet = thousands * 1000;
            if (th.Groups[2].Success && int.TryParse(th.Groups[2].Value, out int hundreds)) feet += hundreds * 100;
            return new Result(feet, climb, AsFlightLevel: false);
        }

        // 3. Nombre en clair annoncé par un verbe/mot d'altitude : « climb to 8000 »,
        //    « maintain 5000 feet ». On exige >= 1000 pour ne pas attraper un cap ou un code.
        var ft = Regex.Match(t, @"\b(?:to|maintain|climb|descend|altitude|level|reach)\s+(\d{3,5})\b");
        if (ft.Success && int.TryParse(ft.Groups[1].Value, out int feetVal) && feetVal is >= 1000 and <= 60000)
            return new Result(feetVal, climb, AsFlightLevel: feetVal >= 18000);

        // 4. « request higher / lower » sans chiffre : direction connue, altitude non.
        return new Result(null, climb, false);
    }
}
