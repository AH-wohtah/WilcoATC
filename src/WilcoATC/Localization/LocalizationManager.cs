using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;

namespace FreqWatch.Localization;

/// <summary>Une langue disponible (code + nom affiché).</summary>
public sealed record LanguageInfo(string Code, string DisplayName);

/// <summary>
/// Localisation de l'application. L'anglais est la langue de BASE (toujours fusionnée) ;
/// la langue active se superpose par-dessus (repli automatique sur l'anglais si une clé
/// manque). Les libellés XAML utilisent <c>{DynamicResource S.Xxx}</c> (changent à chaud) ;
/// le code utilise <see cref="T"/> (thread-safe via un instantané).
///
/// Langues intégrées : en, fr. Langues supplémentaires = fichiers <c>&lt;code&gt;.xaml</c>
/// (ResourceDictionary) déposés/téléchargés dans <see cref="LangDir"/>.
/// </summary>
public static class Loc
{
    public const string BaseCode = "en";

    private static ResourceDictionary _base = new();
    private static ResourceDictionary? _overlay;
    private static readonly Dictionary<string, ResourceDictionary?> _cache = new();

    // Instantané plat clé->texte (lisible depuis n'importe quel thread, ex. AtcController).
    private static Dictionary<string, string> _snapshot = new();
    private static readonly object _snapLock = new();

    public static string CurrentCode { get; private set; } = BaseCode;

    /// <summary>Levé (sur le thread UI) après un changement de langue.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Dossier des langues supplémentaires : %LOCALAPPDATA%\FreqWatch\lang.</summary>
    public static string LangDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreqWatch", "lang");

    /// <summary>À appeler une fois au démarrage (après création de l'Application).</summary>
    public static void Initialize(string? code)
    {
        _base = LoadBuiltIn(BaseCode) ?? new ResourceDictionary();
        Application.Current.Resources.MergedDictionaries.Add(_base);
        SetLanguage(string.IsNullOrWhiteSpace(code) ? BaseCode : code!);
    }

    /// <summary>Langues INTÉGRÉES (un dictionnaire Strings.&lt;code&gt;.xaml existe pour chacune).</summary>
    private static readonly LanguageInfo[] BuiltIn =
    {
        new("en", "English"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("it", "Italiano"),
        new("pt", "Português"),
        new("nl", "Nederlands"),
    };

    /// <summary>Langues proposées : intégrées + fichiers présents dans <see cref="LangDir"/>.</summary>
    public static IReadOnlyList<LanguageInfo> Available()
    {
        var list = new List<LanguageInfo>(BuiltIn);
        try
        {
            if (Directory.Exists(LangDir))
                foreach (var f in Directory.GetFiles(LangDir, "*.xaml"))
                {
                    string code = Path.GetFileNameWithoutExtension(f);
                    if (list.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new LanguageInfo(code, DisplayNameFor(code)));
                }
        }
        catch { /* dossier illisible -> seulement les intégrées */ }
        return list;
    }

    public static void SetLanguage(string code)
    {
        var res = Application.Current.Resources.MergedDictionaries;
        var dict = LoadLanguage(code);
        if (_overlay is not null) res.Remove(_overlay);
        _overlay = dict;
        if (_overlay is not null) res.Add(_overlay);
        CurrentCode = code;
        RebuildSnapshot();
        LanguageChanged?.Invoke();
    }

    /// <summary>Texte localisé pour une clé (repli : anglais, puis la clé elle-même).</summary>
    public static string T(string key)
    {
        lock (_snapLock)
            return _snapshot.TryGetValue(key, out var v) ? v : key;
    }

    private static void RebuildSnapshot()
    {
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in new[] { _base, _overlay })
        {
            if (d is null) continue;
            foreach (var k in d.Keys)
                if (k is string key && d[k] is string val) snap[key] = val;
        }
        lock (_snapLock) _snapshot = snap;
    }

    private static ResourceDictionary? LoadLanguage(string code)
    {
        if (code == BaseCode) return null; // la base est déjà fusionnée
        if (_cache.TryGetValue(code, out var cached)) return cached;
        var d = LoadBuiltIn(code) ?? LoadExternal(code);
        _cache[code] = d;
        return d;
    }

    private static ResourceDictionary? LoadBuiltIn(string code)
    {
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WilcoATC;component/Localization/Strings.{code}.xaml")
            };
        }
        catch { return null; }
    }

    private static ResourceDictionary? LoadExternal(string code)
    {
        try
        {
            string path = Path.Combine(LangDir, code + ".xaml");
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return XamlReader.Load(fs) as ResourceDictionary;
        }
        catch { return null; }
    }

    private static string DisplayNameFor(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).NativeName; }
        catch { return code; }
    }
}
