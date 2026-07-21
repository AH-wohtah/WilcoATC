using System.Windows;
using FreqWatch.Audio;
using FreqWatch.ViewModels;

namespace FreqWatch;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Bouton « Télécharger cette voix » -> fenêtre de progression, refresh, puis
        // sélection automatique de la voix fraîchement installée.
        vm.DownloadVoiceRequested += voice =>
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), voice.Url, vm.VoicesDir) { Owner = this };
            dl.ShowDialog();
            vm.RefreshSherpaVoices();
            if (dl.Success && vm.SherpaVoices.Contains(voice.Name))
                vm.SherpaVoiceName = voice.Name;
        };

        // Bouton « Tout télécharger » : une seule fenêtre, les voix manquantes à la suite.
        vm.DownloadAllVoicesRequested += missing =>
        {
            if (missing.Count == 0) return;
            var items = missing.Select(v => (v.Name, v.Url)).ToList();
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), items, vm.VoicesDir) { Owner = this };
            dl.ShowDialog();
            vm.RefreshSherpaVoices();
        };

        // Bouton « Télécharger le modèle vocal » -> fenêtre de progression, puis refresh de l'état.
        vm.DownloadSpeechModelRequested += () =>
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), vm.SpeechModelUrl, vm.SpeechModelsDir) { Owner = this };
            dl.ShowDialog();
            vm.RefreshSpeechModel();
        };

        // Bouton « Définir la touche » (push-to-talk) : on capture la frappe SUIVANTE.
        vm.CapturePttRequested += () => _capturingPtt = true;

        // Bouton « Charger un OFP… » -> boîte de dialogue de fichier XML.
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

    private bool _capturingPtt;

    /// <summary>En mode capture, la prochaine touche pressée devient la touche push-to-talk.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (_capturingPtt && DataContext is SettingsViewModel vm)
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key != System.Windows.Input.Key.None)
            {
                vm.SetPttKey(key);
                _capturingPtt = false;
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.SaveCommand.Execute(null);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
