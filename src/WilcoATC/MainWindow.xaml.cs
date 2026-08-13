using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WilcoATC.Audio;
using WilcoATC.Input;
using WilcoATC.ViewModels;

namespace WilcoATC;

public partial class MainWindow : Window
{
    // Bouton PTT « MAINTENIR » de l'écran RAD : même boucle micro que l'ancien bouton
    // (maintenir = enfoncé -> on écoute ; relâché -> on transcrit et on envoie). La capture
    // souris garantit qu'on reçoit bien le relâchement même si le curseur sort du bouton.
    private bool _pttHeld;

    private void PttButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        _pttHeld = true;
        (sender as UIElement)?.CaptureMouse();
        vm.StartListening();
        e.Handled = true;
    }

    private void PttButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ReleasePtt(sender);

    private void PttButton_MouseLeave(object sender, MouseEventArgs e)
    {
        // Sécurité : si on quitte le bouton sans avoir relâché (capture perdue), on ferme quand même.
        if (_pttHeld && e.LeftButton == MouseButtonState.Released) ReleasePtt(sender);
    }

    private void ReleasePtt(object sender)
    {
        if (!_pttHeld) return;
        _pttHeld = false;
        (sender as UIElement)?.ReleaseMouseCapture();
        (DataContext as MainViewModel)?.StopListeningAndSend();
    }

    // Raccourci GLOBAL de test (fonctionne même quand MSFS a le focus) : Ctrl + Alt + A.
    private const int HotkeyId = 0xB001;
    private const uint ModAlt = 0x0001, ModControl = 0x0002;
    private const uint VkA = 0x41;
    private const int WmHotkey = 0x0312;

    private HwndSource? _source;

    public MainWindow() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        try { RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModAlt, VkA); }
        catch { /* raccourci global indisponible -> le bouton et F1 restent utilisables */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            (DataContext as MainViewModel)?.TestTransmissionCommand.Execute(null);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Changement de section dans le sélecteur des réglages : on revient en haut. Sans ça, une
    /// section courte choisie après une longue s'ouvrirait au milieu de son propre contenu.
    /// </summary>
    private void OnCfgSectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => CfgScroll?.ScrollToTop();

    // ================================================================= réglages (écran CFG)
    //
    // Tous les réglages vivent maintenant dans l'écran CFG de CETTE fenêtre : la fenêtre
    // « Réglages avancés » a disparu, et avec elle le code qui ouvrait les boîtes de
    // téléchargement et capturait la touche push-to-talk. C'est donc ici, désormais.

    private bool _capturingPtt;
    private DispatcherTimer? _joyCaptureTimer;
    private int _joyCaptureTicks;

    /// <summary>
    /// Branche les actions des réglages qui ont besoin d'une fenêtre : boîtes de
    /// téléchargement (voix, modèle de reconnaissance), sélecteur de fichier OFP et
    /// capture de la touche / du bouton push-to-talk. Appelé par le point de composition.
    /// </summary>
    public void AttachSettings(SettingsViewModel vm)
    {
        // « Télécharger cette voix » -> fenêtre de progression, relecture du dossier, puis
        // sélection automatique de la voix fraîchement installée.
        vm.DownloadVoiceRequested += voice =>
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), voice.Url, vm.VoicesDir) { Owner = this };
            dl.ShowDialog();
            vm.RefreshSherpaVoices();
            if (dl.Success && vm.SherpaVoices.Contains(voice.Name))
                vm.SherpaVoiceName = voice.Name;
        };

        // « Tout télécharger » : une seule fenêtre, les voix manquantes à la suite.
        vm.DownloadAllVoicesRequested += missing =>
        {
            if (missing.Count == 0) return;
            var items = missing.Select(v => (v.Name, v.Url)).ToList();
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), items, vm.VoicesDir) { Owner = this };
            dl.ShowDialog();
            vm.RefreshSherpaVoices();
        };

        // « Télécharger le modèle vocal » -> fenêtre de progression, puis relecture de l'état.
        vm.DownloadSpeechModelRequested += () =>
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), vm.SpeechModelUrl, vm.SpeechModelsDir) { Owner = this };
            dl.ShowDialog();
            vm.RefreshSpeechModel();
        };

        // « Définir la touche » (push-to-talk) : on capture la frappe SUIVANTE.
        vm.CapturePttRequested += () => _capturingPtt = true;

        // « Définir le bouton » (joystick/HOTAS) : on interroge les manches jusqu'à un appui.
        vm.CaptureJoystickRequested += () => StartJoystickCapture(vm);

        // « Charger un OFP… » -> boîte de dialogue de fichier XML.
        vm.LoadOfpRequested += () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "SimBrief OFP (XML)|*.xml|All files|*.*",
                Title = "Choose a SimBrief OFP file",
            };
            if (dlg.ShowDialog(this) == true) vm.ImportOfpFile(dlg.FileName);
        };
    }

    /// <summary>
    /// Capture d'un bouton de joystick : on interroge tous les périphériques (~33 Hz) jusqu'à
    /// détecter un bouton enfoncé, puis on l'assigne. Abandon automatique après ~6 s.
    /// </summary>
    private void StartJoystickCapture(SettingsViewModel vm)
    {
        _joyCaptureTimer?.Stop();
        _joyCaptureTicks = 0;
        GlobalJoystickButton.BeginCapture(); // ignore les boutons déjà « collés »
        _joyCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _joyCaptureTimer.Tick += (_, _) =>
        {
            _joyCaptureTicks++;
            if (GlobalJoystickButton.TryReadNewlyPressed(out int device, out int button))
            {
                _joyCaptureTimer!.Stop();
                vm.SetPttJoystick(device, button);
            }
            else if (_joyCaptureTicks > 200)   // ~6 s sans appui -> on abandonne, ancien bouton conservé
            {
                _joyCaptureTimer!.Stop();
                vm.SetPttJoystick(-1, 0);
            }
        };
        _joyCaptureTimer.Start();
    }

    /// <summary>En mode capture, la prochaine touche pressée devient la touche push-to-talk.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingPtt && DataContext is MainViewModel { Settings: { } settings })
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.None)
            {
                settings.SetPttKey(key);
                _capturingPtt = false;
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _joyCaptureTimer?.Stop();
        try
        {
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HotkeyId);
        }
        catch { }
        _source?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
