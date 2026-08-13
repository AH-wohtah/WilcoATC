namespace WilcoATC.Atc;

/// <summary>
/// Langue parlée par un contrôleur.
///
/// L'anglais reste le standard OACI et le repli universel : tout pays dont la langue n'est
/// pas dans cette liste est contrôlé en anglais, ce qui est aussi la réalité du terrain.
/// Ajouter une langue demande trois choses, et rien d'autre :
///
///  1. une valeur ici (+ son code ISO dans <see cref="AtcLanguages.Code"/>) ;
///  2. ses phrases : <see cref="Localization.AtcPhrases"/> et les blocs « i18n » de
///     <c>atc-rules.json</c> ;
///  3. ses mots-clés pilote : <see cref="Understanding.IntentKeywords"/>.
///
/// Le reste — voix, reconnaissance vocale, choix du pays — suit tout seul.
/// </summary>
public enum AtcLanguage
{
    English,
    French,
    German,
    Spanish,
    Italian,
}

public static class AtcLanguages
{
    /// <summary>Toutes les langues gérées, l'anglais en tête.</summary>
    public static readonly IReadOnlyList<AtcLanguage> All = new[]
    {
        AtcLanguage.English, AtcLanguage.French, AtcLanguage.German,
        AtcLanguage.Spanish, AtcLanguage.Italian,
    };

    /// <summary>Code ISO 639-1, tel qu'employé par les modèles de voix Piper et la table JSON.</summary>
    public static string Code(this AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "fr",
        AtcLanguage.German => "de",
        AtcLanguage.Spanish => "es",
        AtcLanguage.Italian => "it",
        _ => "en",
    };

    /// <summary>Nom affiché dans les réglages (dans la langue elle-même).</summary>
    public static string DisplayName(this AtcLanguage lang) => lang switch
    {
        AtcLanguage.French => "Français",
        AtcLanguage.German => "Deutsch",
        AtcLanguage.Spanish => "Español",
        AtcLanguage.Italian => "Italiano",
        _ => "English",
    };

    /// <summary>Langue depuis un code ISO (« fr », « fr-FR », « FR »), anglais par défaut.</summary>
    public static AtcLanguage FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return AtcLanguage.English;
        string c = code!.Trim().ToLowerInvariant();
        if (c.Length > 2) c = c[..2];
        return c switch
        {
            "fr" => AtcLanguage.French,
            "de" => AtcLanguage.German,
            "es" => AtcLanguage.Spanish,
            "it" => AtcLanguage.Italian,
            _ => AtcLanguage.English,
        };
    }
}
