using System.Windows;
using FreqWatch.Audio;
using FreqWatch.Localization;

namespace FreqWatch;

/// <summary>Fenêtre de progression du téléchargement/extraction (voix ou modèle).</summary>
public partial class VoiceDownloadWindow : Window
{
    private readonly VoiceDownloader _downloader;
    private readonly string _url;
    private readonly string _destDir;
    private readonly CancellationTokenSource _cts = new();

    public bool Success { get; private set; }

    public VoiceDownloadWindow(VoiceDownloader downloader, string url, string destDir)
    {
        InitializeComponent();
        _downloader = downloader;
        _url = url;
        _destDir = destDir;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        var progress = new Progress<double>(p =>
        {
            Bar.Value = p * 100;
            PercentText.Text = $"{p * 100:F0} %";
            StatusText.Text = Loc.T(p < 0.9 ? "S.Dl.Downloading" : "S.Dl.Extracting");
        });

        try
        {
            Success = await _downloader.DownloadAsync(_url, _destDir, progress, _cts.Token);
            StatusText.Text = Loc.T(Success ? "S.Dl.Installed" : "S.Dl.Failed");
            await Task.Delay(600);
        }
        catch (OperationCanceledException)
        {
            Success = false;
        }
        catch (Exception ex)
        {
            Success = false;
            StatusText.Text = Loc.T("S.Dl.ErrorPrefix") + ex.Message;
            await Task.Delay(2000);
        }

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }
}
