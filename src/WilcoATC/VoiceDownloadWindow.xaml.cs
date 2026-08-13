using System.Windows;
using WilcoATC.Audio;
using WilcoATC.Localization;

namespace WilcoATC;

/// <summary>Fenêtre de progression du téléchargement/extraction (voix ou modèle).</summary>
public partial class VoiceDownloadWindow : Window
{
    private readonly VoiceDownloader _downloader;
    private readonly IReadOnlyList<(string Name, string Url)> _items;
    private readonly string _destDir;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Vrai si TOUS les téléchargements ont réussi.</summary>
    public bool Success { get; private set; }

    /// <summary>Nombre d'éléments effectivement installés (mode lot).</summary>
    public int Installed { get; private set; }

    public VoiceDownloadWindow(VoiceDownloader downloader, string url, string destDir)
        : this(downloader, new[] { ("", url) }, destDir) { }

    /// <summary>Mode LOT : télécharge une liste d'éléments à la suite.</summary>
    public VoiceDownloadWindow(VoiceDownloader downloader,
                               IReadOnlyList<(string Name, string Url)> items, string destDir)
    {
        InitializeComponent();
        _downloader = downloader;
        _items = items;
        _destDir = destDir;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        int index = 0;
        bool all = true;

        foreach (var (name, url) in _items)
        {
            index++;
            string prefix = _items.Count > 1 ? $"[{index}/{_items.Count}] {name}  " : "";

            var progress = new Progress<double>(p =>
            {
                Bar.Value = p * 100;
                PercentText.Text = $"{p * 100:F0} %";
                StatusText.Text = prefix + Loc.T(p < 0.9 ? "S.Dl.Downloading" : "S.Dl.Extracting");
            });

            try
            {
                bool ok = await _downloader.DownloadAsync(url, _destDir, progress, _cts.Token);
                if (ok) Installed++; else all = false;
                StatusText.Text = prefix + Loc.T(ok ? "S.Dl.Installed" : "S.Dl.Failed");
                await Task.Delay(_items.Count > 1 ? 250 : 600);
            }
            catch (OperationCanceledException)
            {
                Success = false;
                Close();
                return;
            }
            catch (Exception ex)
            {
                all = false;
                StatusText.Text = prefix + Loc.T("S.Dl.ErrorPrefix") + ex.Message;
                await Task.Delay(_items.Count > 1 ? 1200 : 2000);
            }
        }

        Success = all;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }
}
