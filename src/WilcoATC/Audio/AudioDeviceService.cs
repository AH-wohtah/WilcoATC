using NAudio.Wave;

namespace FreqWatch.Audio;

/// <summary>
/// Énumère les périphériques de sortie audio (WinMM). Le -1 (WAVE_MAPPER) = défaut.
/// Un câble virtuel type VB-CABLE apparaît ici comme un périphérique normal, ce qui
/// permet d'envoyer la voix ATC sur une voie séparée du son du jeu.
/// (Remarque : WinMM tronque les noms à ~31 caractères — limitation connue.)
/// </summary>
public static class AudioDeviceService
{
    public static IReadOnlyList<AudioDevice> GetOutputDevices()
    {
        var list = new List<AudioDevice> { new(-1, "Périphérique par défaut") };
        try
        {
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                list.Add(new AudioDevice(i, caps.ProductName));
            }
        }
        catch { /* énumération indisponible -> au moins le périphérique par défaut */ }
        return list;
    }

    /// <summary>Périphériques d'ENTRÉE (micros) pour la reconnaissance vocale. -1 = défaut.</summary>
    public static IReadOnlyList<AudioDevice> GetInputDevices()
    {
        var list = new List<AudioDevice> { new(-1, "Microphone par défaut") };
        try
        {
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var caps = WaveIn.GetCapabilities(i);
                list.Add(new AudioDevice(i, caps.ProductName));
            }
        }
        catch { /* énumération indisponible -> au moins le micro par défaut */ }
        return list;
    }
}
