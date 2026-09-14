using System.Diagnostics;
using System.Globalization;
using System.Text;
using DVDRescue.Model;

namespace DVDRescue.Media;

public sealed class FfmpegRunner
{
    private readonly string _ffmpeg;

    public FfmpegRunner(string ffmpegPath) => _ffmpeg = ffmpegPath;

    /// <summary>Ricodifica in H.264 + AAC: il risultato si apre ovunque.</summary>
    public static string BuildH264Args(string input, string output, ExtractOptions o)
    {
        var vf = new List<string>();
        if (o.Deinterlace) vf.Add("yadif=mode=0:parity=-1:deint=0");

        var sb = new StringBuilder();
        sb.Append("-hide_banner -nostdin -loglevel error -progress pipe:1 -nostats ");
        sb.Append("-fflags +genpts+igndts -err_detect ignore_err -analyzeduration 200M -probesize 200M ");
        sb.Append($"-i \"{input}\" ");
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append($"-c:v libx264 -preset {o.Preset} -crf {o.Crf} -pix_fmt yuv420p ");
        if (vf.Count > 0) sb.Append($"-vf \"{string.Join(",", vf)}\" ");
        sb.Append($"-c:a aac -b:a {o.AudioBitrate} -ac 2 ");
        sb.Append("-movflags +faststart -max_muxing_queue_size 4096 ");
        sb.Append($"-y \"{output}\"");
        return sb.ToString();
    }

    /// <summary>Remux veloce: video MPEG-2 copiato così com'è, audio convertito in AAC per l'MP4.</summary>
    public static string BuildRemuxArgs(string input, string output)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -nostdin -loglevel error -progress pipe:1 -nostats ");
        sb.Append("-fflags +genpts+igndts -err_detect ignore_err -analyzeduration 200M -probesize 200M ");
        sb.Append($"-i \"{input}\" ");
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append("-c:v copy -c:a aac -b:a 192k -ac 2 ");
        sb.Append("-movflags +faststart -max_muxing_queue_size 4096 ");
        sb.Append($"-y \"{output}\"");
        return sb.ToString();
    }

    public async Task<int> RunAsync(string arguments, double totalSeconds, string stageName,
                                    IProgress<ProgressReport> progress, Action<string> log,
                                    CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var errorBuffer = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            errorBuffer.AppendLine(e.Data);
            if (errorBuffer.Length < 8000) log?.Invoke("ffmpeg: " + e.Data);
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) || progress == null) return;

            // il flusso -progress emette righe chiave=valore
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                e.Data.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                string v = e.Data[(e.Data.IndexOf('=') + 1)..];
                if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us > 0)
                {
                    double seconds = us / 1_000_000.0;
                    double pct = totalSeconds > 0 ? Math.Min(100, seconds * 100.0 / totalSeconds) : 0;
                    progress.Report(new ProgressReport
                    {
                        Stage = stageName,
                        Detail = totalSeconds > 0
                            ? $"{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(totalSeconds):hh\\:mm\\:ss}"
                            : TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss"),
                        Percent = pct
                    });
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } }))
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        if (process.ExitCode != 0 && errorBuffer.Length > 0)
            log?.Invoke($"ffmpeg è uscito con codice {process.ExitCode}");

        return process.ExitCode;
    }

    public async Task<string> GetVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpeg,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var first = output.Split('\n').FirstOrDefault() ?? "";
            return first.Trim();
        }
        catch
        {
            return "";
        }
    }
}
