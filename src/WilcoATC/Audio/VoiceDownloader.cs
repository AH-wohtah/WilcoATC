using System.Formats.Tar;
using System.IO;
using System.Net.Http;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace FreqWatch.Audio;

/// <summary>
/// Télécharge un modèle de voix sherpa-onnx (archive <c>.tar.bz2</c>) et l'extrait
/// dans le dossier des voix, en pur managé (SharpCompress — pas d'outil externe).
/// La progression 0..1 est reportée via <see cref="IProgress{T}"/>.
/// </summary>
public sealed class VoiceDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public async Task<bool> DownloadAsync(string url, string voicesDir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(voicesDir);
        string tmp = Path.Combine(voicesDir, "_download.tar.bz2");

        try
        {
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    // Le téléchargement occupe 0..0.9 ; l'extraction le dernier 0.1.
                    if (total is > 0) progress?.Report(0.9 * read / total.Value);
                }
            }

            progress?.Report(0.9);
            Extract(tmp, voicesDir);
            progress?.Report(1.0);
            return true;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* on ignore */ }
        }
    }

    // .tar.bz2 : décompression bzip2 (SharpCompress) -> extraction tar (natif .NET 8).
    private static void Extract(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var file = File.OpenRead(archivePath);
        using var bz = BZip2Stream.Create(file, CompressionMode.Decompress, decompressConcatenated: false);
        TarFile.ExtractToDirectory(bz, destDir, overwriteFiles: true);
    }
}
