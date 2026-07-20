using FreqWatch.Settings;

namespace FreqWatch.Atc;

/// <summary>
/// Détermine la langue EFFECTIVE des transmissions. Désormais unifiée avec l'interface :
/// elle suit <see cref="AppSettings.AppLanguage"/> (une seule langue pour l'UI et l'ATC).
/// Les gabarits/grammaires ATC couvrent le français et l'anglais ; toute autre langue
/// retombe sur l'anglais pour la PHRASÉOLOGIE (la voix/reconnaissance peut, elle, être
/// dans la langue choisie si une voix correspondante est installée).
/// </summary>
public sealed class LanguageResolver
{
    private readonly SettingsService _settings;

    public LanguageResolver(SettingsService settings) => _settings = settings;

    public AtcLanguage Effective()
    {
        string code = _settings.Current.AppLanguage ?? "en";
        return code.StartsWith("fr", StringComparison.OrdinalIgnoreCase)
            ? AtcLanguage.French
            : AtcLanguage.English;
    }
}
