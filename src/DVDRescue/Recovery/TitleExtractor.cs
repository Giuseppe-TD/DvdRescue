using DVDRescue.Core;
using DVDRescue.Media;

namespace DVDRescue.Recovery;

/// <summary>
/// Porta un titolo dal disco al file finale.
///
/// Quando non serve conservare il flusso grezzo, i byte vanno direttamente da lettore a
/// ffmpeg: il disco viene letto una volta sola e la conversione procede mentre il lettore
/// gira, senza file intermedi. Se invece il grezzo va tenuto, si scrive prima quello e poi
/// si converte da lì, così il disco resta comunque letto una volta sola.
/// </summary>
public static class TitleExtractor
{
    public static async Task<string> ExtractAsync(IBlockSource source, RecoveryTitle title,
                                                  ExtractOptions options, FfmpegRunner runner,
                                                  IProgress<ProgressReport> progress,
                                                  Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutputFolder);

        bool wantsConversion = (options.MakeH264 || options.MakeRemux) && runner != null;
        bool bothOutputs = wantsConversion && options.MakeH264 && options.MakeRemux;

        // Nomi dei file: niente suffissi inutili. L'MP4 si chiama come il titolo, punto.
        // Il suffisso compare solo quando si chiedono entrambe le uscite e servirebbero
        // comunque due nomi diversi.
        var extensions = new List<string>();
        if (wantsConversion) extensions.Add(".mp4");
        if (bothOutputs) extensions.Add("_originale.mp4");
        if (options.KeepRaw || !wantsConversion) extensions.Add(title.RawExtension);

        string baseName = MakeUniqueBaseName(options.OutputFolder, BuildBaseName(title, options), extensions);

        string rawPath = Path.Combine(options.OutputFolder, baseName + title.RawExtension);
        string h264Path = null, remuxPath = null;

        if (bothOutputs)
        {
            h264Path = Path.Combine(options.OutputFolder, baseName + ".mp4");
            remuxPath = Path.Combine(options.OutputFolder, baseName + "_originale.mp4");
        }
        else if (wantsConversion && options.MakeH264)
        {
            h264Path = Path.Combine(options.OutputFolder, baseName + ".mp4");
        }
        else if (wantsConversion && options.MakeRemux)
        {
            remuxPath = Path.Combine(options.OutputFolder, baseName + ".mp4");
        }

        bool filterSectors = !title.IsTransportStream;   // il filtro pack header vale solo per i DVD

        // ---------------------------------------------------------- solo grezzo
        if (!wantsConversion)
        {
            long written = await WriteRawAsync(source, title, rawPath, filterSectors, progress, ct);
            log($"  flusso salvato: {written / 1048576.0:F0} MB in {Path.GetFileName(rawPath)}");
            return rawPath;
        }

        // -------------------------------------------- grezzo + conversione da file
        if (options.KeepRaw)
        {
            long written = await WriteRawAsync(source, title, rawPath, filterSectors, progress, ct);
            log($"  flusso salvato: {written / 1048576.0:F0} MB in {Path.GetFileName(rawPath)}");

            string args = FfmpegRunner.BuildArgs(rawPath, title.IsTransportStream, title.PacketSize,
                                                 h264Path, remuxPath, options);
            int code = await runner.RunAsync(args, null, title.Seconds, "Conversione", progress, log, ct);
            log(code == 0
                ? $"  creato {Path.GetFileName(h264Path ?? remuxPath)}"
                : $"  ffmpeg ha restituito {code}.");
            return h264Path ?? remuxPath ?? rawPath;
        }

        // ---------------------------------------------- conversione al volo da disco
        using var stream = new RangeStream(source, title.Ranges, filterSectors, ct);

        // L'avanzamento segue la testina, non ffmpeg: vedi dove sta sul disco, quanto ne è
        // uscito di buono e quanto se n'è perso, anche mentre è fermo su una zona rovinata.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var optical = source as OpticalBlockSource;

        using var timer = new System.Threading.Timer(_ =>
        {
            double done = stream.PositionOnDisc / 1048576.0;
            double totalMb = stream.TotalLength / 1048576.0;
            double speed = clock.Elapsed.TotalSeconds > 1 ? done / clock.Elapsed.TotalSeconds : 0;

            string detail = $"{done:F0} MB di {totalMb:F0} MB";
            if (speed > 0.05) detail += $" — {speed:F1} MB/s";
            else if (done > 0 || clock.Elapsed.TotalSeconds > 5) detail += " — lettore in difficoltà";

            long lost = stream.SkippedSectors;
            if (lost > 0) detail += $" — {lost} settori persi";
            if (optical is { RetryBudgetSpent: true }) detail += " — recupero ridotto";

            progress?.Report(new ProgressReport
            {
                Stage = "Lettura disco",
                Detail = detail,
                Percent = stream.TotalLength > 0 ? stream.PositionOnDisc * 100.0 / stream.TotalLength : 0,
                SpeedMbPerSec = speed
            });
        }, null, 1000, 1000);

        string pipeArgs = FfmpegRunner.BuildArgs("pipe:0", title.IsTransportStream, title.PacketSize,
                                                 h264Path, remuxPath, options);

        int exit = await runner.RunAsync(pipeArgs, stream, title.Seconds, "Conversione al volo",
                                         progress, log, ct);

        if (stream.SkippedSectors > 0)
            log($"  {stream.SkippedSectors} settori illeggibili o vuoti scartati durante la lettura " +
                $"({stream.SkippedSectors * 2048.0 / 1048576.0:F0} MB persi su {stream.TotalLength / 1048576.0:F0}).");

        if (optical is { RetryBudgetSpent: true })
            log($"  Il disco è messo troppo male: dopo {optical.RetryBudget.TotalMinutes:F0} minuti passati a " +
                "ritentare ho proseguito in lettura veloce, altrimenti non finiva più.");

        log(exit == 0
            ? $"  creato {Path.GetFileName(h264Path ?? remuxPath)}"
            : $"  ffmpeg ha restituito {exit}.");

        return h264Path ?? remuxPath;
    }

    private static async Task<long> WriteRawAsync(IBlockSource source, RecoveryTitle title, string path,
                                                  bool filterSectors, IProgress<ProgressReport> progress,
                                                  CancellationToken ct)
    {
        using var stream = new RangeStream(source, title.Ranges, filterSectors, ct);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);

        var buffer = new byte[1 << 20];
        long written = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;

            if (watch.ElapsedMilliseconds > 500)
            {
                watch.Restart();
                double mb = written / 1048576.0;
                double totalMb = stream.TotalLength / 1048576.0;
                progress?.Report(new ProgressReport
                {
                    Stage = "Estrazione",
                    Detail = $"{mb:F0} MB di {totalMb:F0} MB",
                    Percent = stream.TotalLength > 0 ? stream.BytesRead * 100.0 / stream.TotalLength : 0
                });
            }
        }

        await output.FlushAsync(ct);
        return written;
    }

    public static string BuildBaseName(RecoveryTitle title, ExtractOptions options)
    {
        string prefix = string.IsNullOrWhiteSpace(options.FileNamePrefix) ? "video" : options.FileNamePrefix.Trim();

        string name = options.Split == SplitMode.SingleFile
            ? prefix
            : $"{prefix}_{title.Index:00}";

        if (title.Recorded.HasValue)
            name += "_" + title.Recorded.Value.ToString("yyyy-MM-dd_HHmm");

        return ByteUtils.SafeFileName(name, prefix);
    }

    /// <summary>
    /// Evita di sovrascrivere estrazioni precedenti: con "tutto in un unico file" il nome
    /// dipende solo dal prefisso, quindi due dischi di fila finirebbero sullo stesso file.
    /// </summary>
    private static string MakeUniqueBaseName(string folder, string baseName, List<string> extensions)
    {
        if (extensions.Count == 0) return baseName;

        bool Taken(string candidate)
        {
            foreach (var extension in extensions)
                if (File.Exists(Path.Combine(folder, candidate + extension)))
                    return true;
            return false;
        }

        if (!Taken(baseName)) return baseName;

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{baseName}_{i}";
            if (!Taken(candidate)) return candidate;
        }

        return baseName + "_" + DateTime.Now.ToString("HHmmss");
    }
}
