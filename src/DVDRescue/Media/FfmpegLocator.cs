using System.IO.Compression;
using System.Net.Http;

namespace DVDRescue.Media;

/// <summary>
/// Trova ffmpeg.exe accanto all'applicazione o nel PATH; se manca, lo scarica dalle build
/// ufficiali gyan.dev e lo installa nella cartella dell'applicazione.
/// </summary>
public static class FfmpegLocator
{
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string AppFolder =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    public static string Find()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppFolder, "ffmpeg.exe"),
            Path.Combine(AppFolder, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppFolder, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppFolder, "tools", "ffmpeg.exe"),
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* voce di PATH non valida */ }
        }

        return null;
    }

    /// <summary>Scarica ed estrae ffmpeg.exe nella cartella dell'applicazione. Ritorna il percorso.</summary>
    public static async Task<string> DownloadAsync(IProgress<string> log, CancellationToken ct)
    {
        string zipPath = Path.Combine(Path.GetTempPath(), "ffmpeg-dvdrescue.zip");
        string targetDir = Path.Combine(AppFolder, "ffmpeg");
        Directory.CreateDirectory(targetDir);

        log?.Report("Scarico ffmpeg (circa 40 MB)...");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            int lastPct = -1;

            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total.HasValue && total.Value > 0)
                {
                    int pct = (int)(done * 100 / total.Value);
                    if (pct != lastPct && pct % 5 == 0) { lastPct = pct; log?.Report($"Download ffmpeg: {pct}%"); }
                }
            }
        }

        log?.Report("Estraggo ffmpeg...");

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.EndsWith("/bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                string dest = Path.Combine(targetDir, Path.GetFileName(entry.FullName));
                entry.ExtractToFile(dest, true);
            }
        }

        try { File.Delete(zipPath); } catch { }

        string result = Path.Combine(targetDir, "ffmpeg.exe");
        if (!File.Exists(result)) throw new FileNotFoundException("ffmpeg.exe non trovato nell'archivio scaricato.");

        log?.Report($"ffmpeg installato in {targetDir}");
        return result;
    }
}
