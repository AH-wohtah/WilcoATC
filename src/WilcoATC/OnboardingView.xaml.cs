using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WilcoATC.Atc.Understanding;
using WilcoATC.Audio;
using WilcoATC.Diagnostics;
using WilcoATC.Localization;
using WilcoATC.Settings;

namespace WilcoATC;

/// <summary>
/// Assistant de premier lancement « Ice » (OB1-OB4), intégré À LA FENÊTRE PRINCIPALE en tant
/// que voile plein écran — plus de fenêtre séparée. Les services sont injectés APRÈS
/// construction via <see cref="Attach"/> (le contrôle est instancié par le XAML de la fenêtre).
///
/// PRINCIPE inchangé : il informe, ne bloque jamais. L'étape 2 lit le vol RÉEL (indicatif /
/// type / route depuis le MainViewModel hérité en DataContext) et permet l'import SimBrief ;
/// l'étape 3 règle micro + PTT avec un vrai test de bout en bout. <see cref="Completed"/> est
/// levé à la fin (ou au saut) pour que la fenêtre referme le voile.
/// </summary>
public partial class OnboardingView : UserControl
{
    private const int LastStep = 4;

    /// <summary>Voix installée par défaut : multi-locuteurs, « high ».</summary>
    private static readonly CatalogVoice DefaultVoice =
        VoiceCatalog.Voices.First(v => v.Name == "vits-piper-en_US-libritts-high");

    private SettingsService _settings = null!;
    private VoiceRepository _voices = null!;
    private SpeechModelRepository _models = null!;
    private ITtsEngine _tts = null!;
    private VoiceBus _voice = null!;
    private ISpeechToText _stt = null!;

    private bool _attached;
    private int _step = 1;
    private bool _capturingPtt;
    private bool _micListening;
    private bool _busy;
    private Window? _host;

    private AppSettings S => _settings.Current;

    /// <summary>Levé quand l'assistant se termine ou est sauté : la fenêtre referme le voile.</summary>
    public event Action? Completed;

    public OnboardingView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>Injecte les services (appelé une fois par le point de composition après création de la fenêtre).</summary>
    public void Attach(SettingsService settings, VoiceRepository voices, SpeechModelRepository models,
                       ITtsEngine tts, VoiceBus voice, ISpeechToText stt)
    {
        _settings = settings;
        _voices = voices;
        _models = models;
        _tts = tts;
        _voice = voice;
        _stt = stt;

        InputCombo.ItemsSource = AudioDeviceService.GetInputDevices();
        InputCombo.SelectedItem = ((IReadOnlyList<AudioDevice>)InputCombo.ItemsSource)
            .FirstOrDefault(d => d.Number == S.InputDeviceNumber);
        InputCombo.SelectionChanged += (_, _) =>
        {
            if (InputCombo.SelectedItem is AudioDevice d) S.InputDeviceNumber = d.Number;
        };

        _attached = true;
        ResetToStart();
    }

    /// <summary>Repart de l'étape 1 (à chaque affichage du voile).</summary>
    public void ResetToStart()
    {
        _step = 1;
        Render();
    }

    // Le voile apparaît/disparaît : on remet à l'étape 1 en entrant, on branche/débranche la
    // capture clavier (touche PTT), et on referme proprement le micro s'il tournait encore.
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _host ??= Window.GetWindow(this);
            if (_host is not null) { _host.PreviewKeyDown -= HostPreviewKeyDown; _host.PreviewKeyDown += HostPreviewKeyDown; }
            if (_attached) ResetToStart();
        }
        else
        {
            if (_host is not null) _host.PreviewKeyDown -= HostPreviewKeyDown;
            _capturingPtt = false;
            if (_micListening) { _micListening = false; try { _ = _stt.StopAndTranscribeAsync(); } catch { } }
        }
    }

    // ------------------------------------------------------------------ navigation

    private void Render()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        switch (_step)
        {
            case 1: SkipLink.Text = Loc.T("S.Ob2.SkipSim"); BtnNext.Content = Loc.T("S.Ob2.Next"); break;
            case 2: SkipLink.Text = Loc.T("S.Ob2.BackCaps"); BtnNext.Content = Loc.T("S.Ob2.Next"); break;
            case 3: SkipLink.Text = Loc.T("S.Ob2.SkipTest"); BtnNext.Content = Loc.T("S.Ob2.Next"); RefreshMicStep(); break;
            case 4: SkipLink.Text = Loc.T("S.Ob2.Modify"); BtnNext.Content = Loc.T("S.Ob2.Start"); RefreshRecap(); break;
        }

        if (_step == 3) PttKeyText.Text = PttDisplay();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        // En quittant l'étape « vol », on applique ce qui a été saisi à la main (construit et
        // publie le plan) pour que la phrase à lire et la première fréquence en découlent.
        if (_step == 2 && DataContext is ViewModels.MainViewModel vm) vm.ApplyManualFlight();

        if (_step == LastStep) { Complete(); return; }
        _step++;
        Render();
    }

    /// <summary>Lien secondaire du pied : « continuer sans le sim » / « passer » avancent, « retour » / « modifier » reviennent.</summary>
    private void OnSkipLink(object sender, MouseButtonEventArgs e)
    {
        _step = _step switch
        {
            1 => 2,
            2 => 1,
            3 => 4,
            4 => 3,
            _ => _step,
        };
        Render();
    }

    /// <summary>Termine l'assistant : persiste et referme le voile.</summary>
    private void Complete()
    {
        if (_attached) { S.OnboardingCompleted = true; _settings.Save(); }
        Completed?.Invoke();
    }

    // ------------------------------------------------------------------ étape 3 : micro & PTT

    private string PttDisplay() => string.IsNullOrWhiteSpace(S.PttKeyName) ? Loc.T("S.Ob2.SetKey") : S.PttKeyName;

    private void RefreshMicStep()
    {
        bool ready = _stt.IsAvailable;
        MicTestPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        MicDownloadPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCapturePtt(object sender, RoutedEventArgs e)
    {
        _capturingPtt = true;
        PttKeyText.Text = Loc.T("S.Ob2.PressKey");
    }

    // La capture de touche passe par la fenêtre hôte (le voile n'a pas forcément le focus clavier).
    private void HostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingPtt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return;

        S.PttVirtualKey = KeyInterop.VirtualKeyFromKey(key);
        S.PttKeyName = key.ToString();
        _settings.Save();
        PttKeyText.Text = S.PttKeyName;
        _capturingPtt = false;
        e.Handled = true;
    }

    private async void OnMicTest(object sender, RoutedEventArgs e)
    {
        if (!_stt.IsAvailable) { RefreshMicStep(); return; }

        if (!_micListening)
        {
            try
            {
                _stt.StartListening();
                _micListening = true;
                BtnMicTest.Content = Loc.T("S.Ob2.StopTranscribe");
                MicResult.Text = Loc.T("S.Ob2.MicOpen");
            }
            catch (Exception ex)
            {
                MicResult.Text = Loc.T("S.Ob2.MicUnavailable") + ex.Message;
                FileLog.Exception("onboarding : ouverture du micro", ex);
            }
            return;
        }

        _micListening = false;
        BtnMicTest.Content = Loc.T("S.Ob2.TestMic");
        MicResult.Text = Loc.T("S.Ob2.MicTranscribing");

        try
        {
            string heard = await _stt.StopAndTranscribeAsync();
            MicResult.Text = string.IsNullOrWhiteSpace(heard)
                ? Loc.T("S.Ob2.MicNothing")
                : string.Format(Loc.T("S.Ob2.MicRecognizedFmt"), heard);
        }
        catch (Exception ex)
        {
            MicResult.Text = Loc.T("S.Ob2.MicFailed") + ex.Message;
            FileLog.Exception("onboarding : transcription du test micro", ex);
        }
    }

    private async void OnDownloadAsr(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        BtnDownloadAsr.IsEnabled = false;
        MicDownloadState.Text = Loc.T("S.Ob2.DlProgress");

        try
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(),
                SpeechModelRepository.DefaultModelUrl, _models.ModelsDir) { Owner = Window.GetWindow(this) };
            dl.ShowDialog();

            if (string.IsNullOrWhiteSpace(S.SherpaVoiceName))
            {
                var first = _voices.List().FirstOrDefault();
                if (first is not null) { S.SherpaVoiceName = first.Name; _settings.Save(); }
            }

            MicDownloadState.Text = _stt.IsAvailable
                ? Loc.T("S.Ob2.DlInstalled")
                : Loc.T("S.Ob2.DlMissing");
        }
        catch (Exception ex)
        {
            MicDownloadState.Text = Loc.T("S.Ob2.DlFailed") + ex.Message;
            FileLog.Exception("onboarding : téléchargement du modèle ASR", ex);
        }
        finally
        {
            _busy = false;
            BtnDownloadAsr.IsEnabled = true;
            RefreshMicStep();
        }
    }

    // ------------------------------------------------------------------ étape 4 : récapitulatif

    private void RefreshRecap()
    {
        PttRecap.Text = string.IsNullOrWhiteSpace(S.PttKeyName) ? Loc.T("S.Ob2.PttUnset") : S.PttKeyName;
        MicRecap.Text = (InputCombo.SelectedItem as AudioDevice)?.Name ?? Loc.T("S.Ob2.DefaultDevice");
    }
}
