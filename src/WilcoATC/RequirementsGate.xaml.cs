using System.Windows;
using System.Windows.Controls;
using WilcoATC.Audio;
using WilcoATC.Diagnostics;
using WilcoATC.Localization;
using WilcoATC.Settings;
using WilcoATC.ViewModels;

namespace WilcoATC;

/// <summary>
/// Voile bloquant tant que les modèles indispensables ne sont pas installés.
///
/// POURQUOI IL EXISTE. Sans voix de synthèse, le contrôleur ne peut pas parler ; sans modèle
/// de reconnaissance, il ne peut pas entendre. L'application signalait ces deux manques par
/// une ligne de statut — autant dire par rien : le pilote appuyait sur son alternat, rien ne
/// se produisait, et rien ne distinguait un modèle absent d'un micro muet ou d'une touche mal
/// assignée. Pire encore avant : la synthèse de Windows prenait la place en silence, et
/// l'utilisateur entendait une voix qu'il croyait être celle du logiciel.
///
/// Un voile modal règle les deux problèmes d'un coup : il est impossible à manquer, et chaque
/// manque porte son bouton de téléchargement.
/// </summary>
public partial class RequirementsGate : UserControl
{
    private SettingsService? _settings;
    private SettingsViewModel? _settingsVm;
    private VoiceRepository? _voices;
    private SpeechModelRepository? _speech;

    /// <summary>Levé quand les deux prérequis sont satisfaits et que l'utilisateur poursuit.</summary>
    public event Action? Satisfied;

    public RequirementsGate() => InitializeComponent();

    public void Attach(SettingsViewModel settingsVm, SettingsService settings,
                       VoiceRepository voices, SpeechModelRepository speech)
    {
        _settingsVm = settingsVm;
        _settings = settings;
        _voices = voices;
        _speech = speech;
        Refresh();
    }

    /// <summary>Les deux modèles sont-ils là ? C'est la seule condition de sortie.</summary>
    public bool IsSatisfied => _voices is { } v && _speech is { } s && v.HasAnyVoice() && s.IsInstalled;

    /// <summary>
    /// Met à jour l'affichage. Un prérequis déjà satisfait garde son bouton, désactivé : le
    /// faire disparaître donnerait l'impression que la ligne a changé de nature, alors que
    /// seule sa situation a changé.
    /// </summary>
    public void Refresh()
    {
        if (_voices is null || _speech is null) return;

        bool hasVoice = _voices.HasAnyVoice();
        bool hasAsr = _speech.IsInstalled;

        VoiceStatus.Text = hasVoice
            ? Loc.T("S.Gate.VoiceOk").Replace("{n}", _voices.List().Count.ToString())
            : Loc.T("S.Gate.VoiceMissing");
        VoiceButton.IsEnabled = !hasVoice;

        AsrStatus.Text = hasAsr ? Loc.T("S.Gate.AsrOk") : Loc.T("S.Gate.AsrMissing");
        AsrButton.IsEnabled = !hasAsr;

        ContinueButton.Visibility = hasVoice && hasAsr ? Visibility.Visible : Visibility.Collapsed;
    }

    private Window? Host() => Window.GetWindow(this);

    private void OnDownloadVoices(object sender, RoutedEventArgs e)
    {
        if (_voices is null || _settings is null) return;

        try
        {
            var installed = _voices.List().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = VoiceCatalog.Voices.Where(v => !installed.Contains(v.Name))
                                             .Select(v => (v.Name, v.Url)).ToList();
            if (missing.Count == 0) { Refresh(); return; }

            var dl = new VoiceDownloadWindow(new VoiceDownloader(), missing, _voices.VoicesDir) { Owner = Host() };
            dl.ShowDialog();

            _settingsVm?.RefreshSherpaVoices();

            // Sans voix SÉLECTIONNÉE, le moteur reste muet alors que les fichiers sont là :
            // on en choisit une d'office plutôt que de laisser l'utilisateur devant un
            // téléchargement réussi qui ne change rien.
            if (string.IsNullOrWhiteSpace(_settings.Current.SherpaVoiceName))
            {
                var first = _voices.List().FirstOrDefault();
                if (first is not null) { _settings.Current.SherpaVoiceName = first.Name; _settings.Save(); }
            }
        }
        catch (Exception ex) { FileLog.Exception("barrière : téléchargement des voix", ex); }

        Refresh();
    }

    private void OnDownloadAsr(object sender, RoutedEventArgs e)
    {
        if (_speech is null) return;

        try
        {
            var dl = new VoiceDownloadWindow(new VoiceDownloader(),
                SpeechModelRepository.DefaultModelUrl, _speech.ModelsDir) { Owner = Host() };
            dl.ShowDialog();
            _settingsVm?.RefreshSpeechModel();
        }
        catch (Exception ex) { FileLog.Exception("barrière : téléchargement de la reconnaissance", ex); }

        Refresh();
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (!IsSatisfied) { Refresh(); return; }   // garde-fou : on ne sort pas d'un état incomplet
        Satisfied?.Invoke();
    }
}
