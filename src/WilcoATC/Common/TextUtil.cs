using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WilcoATC.Common;

/// <summary>Normalisation de texte partagée (minuscules, sans accents, sans ponctuation).</summary>
public static class TextUtil
{
    /// <summary>minuscules + suppression des accents + ponctuation → espace + espaces compressés.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string formD = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (char c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // accent
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static string[] Tokenize(string? s)
        => Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
