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

        // Bouton « Télécharger le modèle vocal » -> fenêtre de progression, puis refresh de l'état.
        vm.DownloadSpeechModelRequested += () =>
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(), vm.SpeechModelUrl, vm.SpeechModelsDir) { Owner = this };
            dl.ShowDialog();
            vm.RefreshSpeechModel();
        };

        // Bouton « Charger un OFP… » -> boîte de dialogue de fichier XML.
        vm.LoadOfpRequested += () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "OFP SimBrief (XML)|*.xml|Tous les fichiers|*.*",
                Title = "Choisir un fichier OFP SimBrief",
            };
            if (dlg.ShowDialog(this) == true) vm.ImportOfpFile(dlg.FileName);
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.SaveCommand.Execute(null);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
