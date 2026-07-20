using System.IO;
using System.Text.Json;

namespace FreqWatch.Settings;

/// <summary>
/// Charge/sauvegarde les réglages dans %APPDATA%\FreqWatch\settings.json.
/// <see cref="Current"/> est muté en place : les composants le lisent à la volée,
/// donc un changement de réglage prend effet sans re-câblage.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreqWatch");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch { /* fichier corrompu -> valeurs par défaut */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions)); }
        catch { /* écriture impossible -> on ignore, l'app continue */ }
    }
}
