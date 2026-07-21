namespace FreqWatch.Formatting;

/// <summary>
/// Nettoyage des chaînes d'identité avion renvoyées par SimConnect.
///
/// Certaines variables (notamment ATC MODEL / ATC TYPE) peuvent renvoyer des
/// jetons de localisation MSFS (« TT:ATCCOM.AC_MODEL... », « $$:... », « ....text »)
/// au lieu d'un libellé lisible. On les neutralise pour que l'UI puisse retomber
/// proprement sur un autre champ (le TITLE, lui, est toujours lisible).
/// </summary>
public static class AircraftFormatter
{
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim().Trim('\0').Trim();
        if (s.Length == 0) return "";

        // Jeton de localisation non résolu -> on préfère ne rien afficher.
        if (s.StartsWith("TT:", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("$$:", StringComparison.Ordinal) ||
            s.EndsWith(".text", StringComparison.OrdinalIgnoreCase))
            return "";

        return s;
    }
}
