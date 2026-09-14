using System.Diagnostics;
using System.Globalization;
using System.Text;
using DVDRescue.Recovery;

namespace DVDRescue.Media;

public sealed class FfmpegRunner
{
    private readonly string _ffmpeg;

    public FfmpegRunner(string ffmpegPath) => _ffmpeg = ffmpegPath;

    public string Path => _ffmpeg;

    // ------------------------------------------------------------- argomenti

    /// <summary>Parte comune: ingresso da pipe o da file, con tolleranza agli errori dello stream.</summary>
    private static string InputArgs(string input, bool transportStream, int packetSize)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -nostdin -loglevel error -progress pipe:1 -nostats ");
        sb.Append("-fflags +genpts+igndts+discardcorrupt -err_detect ignore_err ");
        sb.Append("-analyzeduration 200M -probesize 200M ");

        if (transportStream)
            sb.Append(packetSize == 192 ? "-f mpegts " : "-f mpegts ");
        else if (input == "pipe:0")
            sb.Append("-f mpeg ");

        sb.Append($"-i \"{input}\" ");
        return sb.ToString();
    }

    /// <summary>Uscita ricodificata in H.264 + AAC: si apre ovunque.</summary>
    private static string H264Output(string output, ExtractOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append($"-c:v libx264 -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p ");
        if (options.Deinterlace) sb.Append("-vf \"yadif=mode=0:parity=-1:deint=0\" ");
        sb.Append($"-c:a aac -b:a {options.AudioBitrate} -ac 2 ");
        sb.Append("-movflags +faststart -max_muxing_queue_size 8192 ");
        sb.Append($"-y \"{output}\"");
        return sb.ToString();
    }

    /// <summary>Uscita senza ricodifica del video: qualità originale, velocissima.</summary>
    private static string RemuxOutput(string output)
    {
        var sb = new StringBuilder();
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append("-c:v copy -c:a aac -b:a 192k -ac 2 ");
        sb.Append("-movflags +faststart -max_muxing_queue_size 8192 ");
        sb.Append($"-y \"{output}\"");
        return sb.ToString();
    }

    /// <summary>
    /// Costruisce un solo comando con tutte le uscite richieste: il disco viene letto
    /// una volta sola anche quando si vogliono sia l'MP4 ricodificato sia quello originale.
    /// </summary>
    public static string BuildArgs(string input, bool transportStream, int packetSize,
                                   string h264Output, string remuxOutput, ExtractOptions options)
    {
        var sb = new StringBuilder(InputArgs(input, transportStream, packetSize));
        if (h264Output != null) sb.Append(H264Output(h264Output, options)).Append(' ');
        if (remuxOutput != null) sb.Append(RemuxOutput(remuxOutput));
        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------------- esecuzione

    /// <summary>
    /// Esegue ffmpeg. Se <paramref name="input"/> non è null, i dati gli vengono passati
    /// dallo stream mentre il disco viene letto: nessun file intermedio.
    /// </summary>
    public async Task<int> RunAsync(string arguments, Stream input, double totalSeconds, string stage,
                                    IProgress<ProgressReport> progress, Action<string> log,
                                    CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var errors = new StringBuilder();
        var watch = Stopwatch.StartNew();

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            errors.AppendLine(e.Data);
            if (errors.Length < 6000) log?.Invoke("ffmpeg: " + e.Data);
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) || progress == null) return;
            if (!e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                !e.Data.StartsWith("out_time_ms=", StringComparison.Ordinal)) return;

            string value = e.Data[(e.Data.IndexOf('=') + 1)..];
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds) ||
                microseconds <= 0) return;

            double seconds = microseconds / 1_000_000.0;
            double percent = totalSeconds > 0 ? Math.Min(100, seconds * 100.0 / totalSeconds) : 0;

            string detail = totalSeconds > 0
                ? $"{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss} di {TimeSpan.FromSeconds(totalSeconds):hh\\:mm\\:ss}"
                : TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

            double speed = watch.Elapsed.TotalSeconds > 1 ? seconds / watch.Elapsed.TotalSeconds : 0;
            if (speed > 0) detail += $"  ({speed:F1}×)";

            progress.Report(new ProgressReport { Stage = stage, Detail = detail, Percent = percent });
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Task feeder = Task.CompletedTask;

        if (input != null)
        {
            feeder = Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    await process.StandardInput.BaseStream.FlushAsync(ct);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { /* ffmpeg ha chiuso l'ingresso: normale a fine lavoro */ }
                finally
                {
                    try { process.StandardInput.Close(); } catch { }
                }
            }, ct);
        }

        using (ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } }))
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        try { await feeder.ConfigureAwait(false); } catch { }

        if (process.ExitCode != 0)
            log?.Invoke($"ffmpeg è uscito con codice {process.ExitCode}.");

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

            using var process = Process.Start(psi);
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (output.Split('\n').FirstOrDefault() ?? "").Trim();
        }
        catch { return ""; }
    }
}
