using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WilcoATC.Atc.Understanding;
using WilcoATC.Audio;
using WilcoATC.Diagnostics;
using WilcoATC.Input;
using WilcoATC.Localization;
using WilcoATC.Settings;
using WilcoATC.ViewModels;

namespace WilcoATC;

/// <summary>
/// Assistant de PREMIÈRE CONFIGURATION (voile plein écran, une seule fois au tout premier
/// lancement) : langue d'affichage, téléchargement de TOUTES les voix ATC + du modèle de
/// reconnaissance vocale, et touche push-to-talk (clavier ET bouton d'un périphérique externe :
/// manette, joystick, throttle…). Les services sont injectés après construction via
/// <see cref="Attach"/> ; la fin lève <see cref="Completed"/> pour que la fenêtre referme le voile.
/// </summary>
public partial class SetupView : UserControl
{
    private SettingsViewModel _settingsVm = null!;
    private SettingsService _settings = null!;
    private VoiceRepository _voices = null!;
    private SpeechModelRepository _whisper = null!;
    private ISpeechToText _stt = null!;
    private bool _attached;

    private bool _capturingKey;
    private DispatcherTimer? _joyTimer;
    private int _joyTicks;
    private Window? _host;

    /// <summary>Levé quand la configuration est terminée : la fenêtre referme le voile.</summary>
    public event Action? Completed;

    public SetupView() => InitializeComponent();

    public void Attach(SettingsViewModel settingsVm, SettingsService settings,
                       VoiceRepository voices, SpeechModelRepository whisper, ISpeechToText stt)
    {
        _settingsVm = settingsVm;
        _settings = settings;
        _voices = voices;
        _whisper = whisper;
        _stt = stt;
        DataContext = settingsVm;      // langue + affichages PTT se lient au VM des réglages
        _attached = true;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (!_attached) return;
        bool hasVoice = _voices.List().Count > 0;
        VoicesStatus.Text = Loc.T(hasVoice ? "S.Setup.VoicesOk" : "S.Setup.VoicesNo");
        AsrStatus.Text = Loc.T(_stt.IsAvailable ? "S.Setup.AsrOk" : "S.Setup.AsrNo");
        KeyDisplay.Text = _settingsVm.PttKeyDisplay;
        ButtonDisplay.Text = _settingsVm.PttJoystickDisplay;
    }

    // ------------------------------------------------------------------ téléchargements

    private void OnDownloadVoices(object sender, RoutedEventArgs e)
    {
        var installed = _voices.List().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = VoiceCatalog.Voices.Where(v => !installed.Contains(v.Name)).ToList();
        if (missing.Count == 0) { RefreshStatus(); return; }

        try
        {
            var items = missing.Select(v => (v.Name, v.Url)).ToList();
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), items, _voices.VoicesDir) { Owner = Host() };
            dl.ShowDialog();

            _settingsVm.RefreshSherpaVoices();
            if (string.IsNullOrWhiteSpace(_settings.Current.SherpaVoiceName))
            {
                var first = _voices.List().FirstOrDefault();
                if (first is not null) { _settings.Current.SherpaVoiceName = first.Name; _settings.Save(); }
            }
        }
        catch (Exception ex) { FileLog.Exception("setup : téléchargement des voix", ex); }
        RefreshStatus();
    }

    private void OnDownloadAsr(object sender, RoutedEventArgs e)
    {
        try
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(),
                SpeechModelRepository.DefaultModelUrl, _whisper.ModelsDir) { Owner = Host() };
            dl.ShowDialog();
            _settingsVm.RefreshSpeechModel();
        }
        catch (Exception ex) { FileLog.Exception("setup : téléchargement de la reconnaissance", ex); }
        RefreshStatus();
    }

    // ------------------------------------------------------------------ push-to-talk : clavier

    private void OnSetKey(object sender, RoutedEventArgs e)
    {
        _capturingKey = true;
        HookHost();
        KeyDisplay.Text = Loc.T("S.Setup.Press");
    }

    private void OnClearKey(object sender, RoutedEventArgs e)
    {
        _settingsVm.ClearPttCommand.Execute(null);
        RefreshStatus();
    }

    private void HookHost()
    {
        var w = Host();
        if (w is null) return;
        w.PreviewKeyDown -= OnHostKeyDown;
        w.PreviewKeyDown += OnHostKeyDown;
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingKey) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return;

        _settingsVm.SetPttKey(key);
        _capturingKey = false;
        KeyDisplay.Text = _settingsVm.PttKeyDisplay;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ push-to-talk : manette / joystick

    private void OnSetButton(object sender, RoutedEventArgs e)
    {
        _joyTimer?.Stop();
        _joyTicks = 0;
        ButtonDisplay.Text = Loc.T("S.Setup.Press");
        GlobalJoystickButton.BeginCapture(); // ignore les boutons déjà « collés » (pilote fantôme)
        _joyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _joyTimer.Tick += (_, _) =>
        {
            _joyTicks++;
            if (GlobalJoystickButton.TryReadNewlyPressed(out int device, out int button))
            {
                _joyTimer!.Stop();
                _settingsVm.SetPttJoystick(device, button);
                RefreshStatus();
            }
            else if (_joyTicks > 200) // ~6 s sans appui -> on abandonne (ancien bouton conservé)
            {
                _joyTimer!.Stop();
                RefreshStatus();
            }
        };
        _joyTimer.Start();
    }

    private void OnClearButton(object sender, RoutedEventArgs e)
    {
        _settingsVm.ClearJoystickCommand.Execute(null);
        RefreshStatus();
    }

    // ------------------------------------------------------------------ fin

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        _joyTimer?.Stop();
        _capturingKey = false;
        if (_host is not null) _host.PreviewKeyDown -= OnHostKeyDown;
        Completed?.Invoke();
    }

    private Window Host() => _host ??= Window.GetWindow(this)!;
}
