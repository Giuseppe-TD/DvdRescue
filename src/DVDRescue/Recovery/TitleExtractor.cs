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

        string baseName = BuildBaseName(title, options);
        string rawPath = Path.Combine(options.OutputFolder, baseName + title.RawExtension);
        string h264Path = Path.Combine(options.OutputFolder, baseName + ".mp4");
        string remuxPath = Path.Combine(options.OutputFolder, baseName + "_originale.mp4");

        bool wantsConversion = (options.MakeH264 || options.MakeRemux) && runner != null;
        bool filterSectors = !title.IsTransportStream;   // il filtro pack header vale solo per i DVD

        // ---------------------------------------------------------- solo grezzo
        if (!wantsConversion)
        {
            long written = await WriteRawAsync(source, title, rawPath, filterSectors, progress, ct);
            log($"  flusso salvato: {written / 1048576.0:F0} MB in {Path.GetFileName(rawPath)}");
            return rawPath;
        }

        string h264 = options.MakeH264 ? h264Path : null;
        string remux = options.MakeRemux ? remuxPath : null;

        // -------------------------------------------- grezzo + conversione da file
        if (options.KeepRaw)
        {
            long written = await WriteRawAsync(source, title, rawPath, filterSectors, progress, ct);
            log($"  flusso salvato: {written / 1048576.0:F0} MB in {Path.GetFileName(rawPath)}");

            string args = FfmpegRunner.BuildArgs(rawPath, title.IsTransportStream, title.PacketSize,
                                                 h264, remux, options);
            int code = await runner.RunAsync(args, null, title.Seconds, "Conversione", progress, log, ct);
            log(code == 0 ? "  conversione completata." : $"  ffmpeg ha restituito {code}.");
            return h264 ?? remux ?? rawPath;
        }

        // ---------------------------------------------- conversione al volo da disco
        using var stream = new RangeStream(source, title.Ranges, filterSectors, ct);

        // avanzamento della lettura: ffmpeg riporta il tempo elaborato, questo riporta i byte letti
        using var timer = new System.Threading.Timer(_ =>
        {
            double mb = stream.BytesRead / 1048576.0;
            double totalMb = stream.TotalLength / 1048576.0;
            progress?.Report(new ProgressReport
            {
                Stage = "Lettura disco",
                Detail = $"{mb:F0} MB di {totalMb:F0} MB letti",
                Percent = stream.TotalLength > 0 ? stream.BytesRead * 100.0 / stream.TotalLength : 0
            });
        }, null, 1000, 1000);

        string pipeArgs = FfmpegRunner.BuildArgs("pipe:0", title.IsTransportStream, title.PacketSize,
                                                 h264, remux, options);

        int exit = await runner.RunAsync(pipeArgs, stream, title.Seconds, "Conversione al volo",
                                         progress, log, ct);

        if (stream.SkippedSectors > 0)
            log($"  {stream.SkippedSectors} settori illeggibili o vuoti scartati durante la lettura.");

        log(exit == 0 ? "  conversione completata." : $"  ffmpeg ha restituito {exit}.");
        return h264 ?? remux;
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
}
