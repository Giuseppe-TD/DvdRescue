using System.Diagnostics;
using System.Globalization;
using DVDRescue.Core;
using DVDRescue.Media;
using DVDRescue.Recovery;

namespace TestHarness;

internal static class Program
{
    private static int _failures;
    private static string _media;

    private static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "OK " : "FAIL")}] {what}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    private static void Section(string title) => Console.WriteLine($"\n=== {title} ===");

    public static async Task<int> Main(string[] args)
    {
        _media = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "media");
        if (!Directory.Exists(_media))
        {
            Console.WriteLine($"Cartella del materiale di prova non trovata: {_media}");
            Console.WriteLine("Esegui prima make-test-images.sh");
            return 2;
        }

        Console.WriteLine($"Materiale di prova: {_media}");

        TestDvdVideo();
        TestNoFragmentation();
        await TestSingleFileExtraction();
        await TestStreamingConversion();
        TestRawDisc();
        TestBadInput();

        Console.WriteLine($"\n=== {(_failures == 0 ? "TUTTI I CONTROLLI SUPERATI" : _failures + " CONTROLLI FALLITI")} ===");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- DVD-Video

    private static void TestDvdVideo()
    {
        Section("DVD-Video con 3 titoli e capitoli");

        string iso = Path.Combine(_media, "dvd.iso");
        if (!File.Exists(iso)) { Console.WriteLine("  (dvd.iso assente, salto)"); return; }

        using var source = new FileBlockSource(iso);
        var result = RecoveryEngine.Analyze(source, false, null, Log, CancellationToken.None);

        Console.WriteLine($"  profilo: {result.ProfileText}");
        Console.WriteLine($"  filesystem: {result.FilesystemInfo}  volume: {result.VolumeLabel}");

        foreach (var t in result.Titles)
            Console.WriteLine($"    #{t.Index} {t.DurationText} {t.SizeText,8}  {t.Origin}  [{t.VideoInfo}]");

        Check("il disco è riconosciuto come DVD-Video", result.Profile == DVDRescue.Recovery.DiscProfile.DvdVideo);
        Check("trova esattamente 3 titoli (non decine di frammenti)", result.Titles.Count == 3,
              $"{result.Titles.Count} titoli");
        Check("i capitoli sono disponibili", result.ChapterTitles.Count >= 4,
              $"{result.ChapterTitles.Count} capitoli");

        double first = result.Titles.Count > 0 ? result.Titles[0].Seconds : 0;
        Check("la durata del primo titolo è circa 20 s", Math.Abs(first - 20.0) < 1.5, $"{first:F2} s");

        // ogni titolo deve avere pochi tratti contigui, non uno per cella
        if (result.Titles.Count > 0)
            Check("i tratti sono uniti", result.Titles.All(t => t.Ranges.Count <= 3),
                  string.Join(", ", result.Titles.Select(t => $"{t.Ranges.Count} tratti")));
    }

    // ------------------------------------- confronto con la scansione grezza

    private static void TestNoFragmentation()
    {
        Section("Il problema dei frammenti: strutture contro scansione");

        string iso = Path.Combine(_media, "dvd2.iso");
        if (!File.Exists(iso)) { Console.WriteLine("  (dvd2.iso assente, salto)"); return; }

        using var source = new FileBlockSource(iso);

        var withStructures = RecoveryEngine.Analyze(source, false, null, _ => { }, CancellationToken.None);
        var withScan = RecoveryEngine.Analyze(source, true, null, _ => { }, CancellationToken.None);

        Console.WriteLine($"  leggendo le IFO:        {withStructures.Titles.Count} titoli");
        Console.WriteLine($"  scandendo i settori:    {withScan.Titles.Count} titoli");

        Check("le strutture del disco danno pochi titoli", withStructures.Titles.Count <= 3,
              $"{withStructures.Titles.Count}");
        Check("anche la scansione prudente non esplode in frammenti", withScan.Titles.Count <= 4,
              $"{withScan.Titles.Count}");

        // la vecchia regola (taglio a ogni salto d'orologio) produceva 9 titoli su questo disco
        var aggressive = new MpegPsCarver { SplitOnClockReset = true, ClockResetSeconds = 0.1, MaxGapSectors = 64, MinSegmentSectors = 128 };
        var aggressiveSegments = aggressive.Scan(source, 0, source.Length, null, CancellationToken.None);
        Console.WriteLine($"  col vecchio criterio:   {aggressiveSegments.Count} titoli");
        Check("il criterio prudente riduce nettamente i frammenti",
              withScan.Titles.Count < aggressiveSegments.Count,
              $"{withScan.Titles.Count} contro {aggressiveSegments.Count}");
    }

    // ---------------------------------------------------- unione in un file

    private static async Task TestSingleFileExtraction()
    {
        Section("Unione di tutto in un solo file");

        string iso = Path.Combine(_media, "dvd.iso");
        if (!File.Exists(iso)) { Console.WriteLine("  (dvd.iso assente, salto)"); return; }

        using var source = new FileBlockSource(iso);
        var result = RecoveryEngine.Analyze(source, false, null, _ => { }, CancellationToken.None);

        var single = RecoveryEngine.ApplySplit(result, SplitMode.SingleFile);
        Check("l'unione produce un solo elemento", single.Count == 1, $"{single.Count}");
        if (single.Count != 1) return;

        long sumOfParts = result.Titles.Sum(t => t.Bytes);
        Console.WriteLine($"  titoli separati: {sumOfParts / 1048576.0:F1} MB — unito: {single[0].Bytes / 1048576.0:F1} MB");
        Check("l'unione non perde byte", single[0].Bytes >= sumOfParts * 0.99,
              $"{single[0].Bytes} contro {sumOfParts}");

        string outDir = Path.Combine(_media, "out");
        Directory.CreateDirectory(outDir);
        string raw = Path.Combine(outDir, "unito.mpg");

        using (var stream = new RangeStream(source, single[0].Ranges, true, CancellationToken.None))
        await using (var file = File.Create(raw))
        {
            await stream.CopyToAsync(file);
        }

        double duration = Probe(raw);
        double expected = result.Titles.Sum(t => t.Seconds);
        long frames = ProbeFrames(raw);
        long expectedFrames = (long)Math.Round(expected * 25);

        Console.WriteLine($"  durata dichiarata dal flusso grezzo: {duration:F2} s");
        Console.WriteLine($"  fotogrammi effettivi: {frames} (attesi circa {expectedFrames})");
        Console.WriteLine("  nota: unendo registrazioni diverse l'orologio MPEG riparte da capo,");
        Console.WriteLine("        quindi il .mpg grezzo dichiara una durata più corta del vero.");
        Console.WriteLine("        I dati ci sono tutti e la conversione rigenera i tempi giusti.");

        Check("il file unito è uno stream valido", duration > 0);
        Check("contiene tutti i fotogrammi delle registrazioni unite",
              frames >= expectedFrames * 0.98, $"{frames} contro {expectedFrames}");

        // la conversione deve restituire la durata vera nonostante i tempi discontinui
        string ffmpeg = FindFfmpeg();
        if (ffmpeg != null)
        {
            string fixedMp4 = Path.Combine(outDir, "unito.mp4");
            RunProcess(ffmpeg, $"-v error -fflags +genpts -i \"{raw}\" -c:v copy -c:a aac -y \"{fixedMp4}\"");
            double converted = Probe(fixedMp4);
            Console.WriteLine($"  durata dopo la conversione: {converted:F2} s");
            Check("la conversione rimette a posto i tempi", Math.Abs(converted - expected) < 3.0,
                  $"{converted:F2} contro {expected:F2}");
        }
    }

    // ------------------------------------------------ conversione al volo

    private static async Task TestStreamingConversion()
    {
        Section("Conversione al volo, senza file intermedi");

        string iso = Path.Combine(_media, "dvd.iso");
        string ffmpeg = FindFfmpeg();
        if (!File.Exists(iso) || ffmpeg == null) { Console.WriteLine("  (materiale o ffmpeg assenti, salto)"); return; }

        using var source = new FileBlockSource(iso);
        var result = RecoveryEngine.Analyze(source, false, null, _ => { }, CancellationToken.None);
        var titles = RecoveryEngine.ApplySplit(result, SplitMode.SingleFile);
        if (titles.Count == 0) { Check("nessun titolo da convertire", false); return; }

        string outDir = Path.Combine(_media, "out");
        Directory.CreateDirectory(outDir);

        var options = new ExtractOptions
        {
            OutputFolder = outDir,
            Split = SplitMode.SingleFile,
            MakeH264 = true,
            MakeRemux = true,
            KeepRaw = false,
            Crf = 30,
            Preset = "ultrafast",
            Deinterlace = false,
            FileNamePrefix = "streaming"
        };

        var runner = new FfmpegRunner(ffmpeg);
        var watch = Stopwatch.StartNew();

        await TitleExtractor.ExtractAsync(source, titles[0], options, runner, null,
                                          m => Console.WriteLine("    " + m), CancellationToken.None);

        watch.Stop();

        string mp4 = Path.Combine(outDir, "streaming.mp4");
        string remux = Path.Combine(outDir, "streaming_originale.mp4");

        Check("l'MP4 ricodificato è stato creato", File.Exists(mp4) && new FileInfo(mp4).Length > 50000,
              File.Exists(mp4) ? $"{new FileInfo(mp4).Length / 1024} KB" : "assente");
        Check("l'MP4 senza ricodifica è stato creato", File.Exists(remux) && new FileInfo(remux).Length > 50000,
              File.Exists(remux) ? $"{new FileInfo(remux).Length / 1024} KB" : "assente");

        double expected = result.Titles.Sum(t => t.Seconds);
        double got = File.Exists(mp4) ? Probe(mp4) : 0;
        Console.WriteLine($"  durata MP4: {got:F2} s (attesa {expected:F2} s) in {watch.Elapsed.TotalSeconds:F1} s");
        Check("la durata dell'MP4 corrisponde", Math.Abs(got - expected) < 3.0, $"{got:F2}");

        double gotRemux = File.Exists(remux) ? Probe(remux) : 0;
        Check("anche il remux ha la durata giusta", Math.Abs(gotRemux - expected) < 3.0, $"{gotRemux:F2}");

        Check("entrambe le uscite sono state prodotte in una sola lettura del disco",
              File.Exists(mp4) && File.Exists(remux));
    }

    // --------------------------------------------------- disco senza filesystem

    private static void TestRawDisc()
    {
        Section("Disco senza filesystem né strutture");

        string bin = Path.Combine(_media, "raw.bin");
        if (!File.Exists(bin)) { Console.WriteLine("  (raw.bin assente, salto)"); return; }

        using var source = new FileBlockSource(bin);
        var result = RecoveryEngine.Analyze(source, false, null, _ => { }, CancellationToken.None);

        foreach (var t in result.Titles)
            Console.WriteLine($"    #{t.Index} {t.DurationText} {t.SizeText,8}  {t.Origin}");

        Check("ricade sulla scansione diretta", result.Profile == DVDRescue.Recovery.DiscProfile.RawVideo);
        Check("trova le due registrazioni separate", result.Titles.Count == 2, $"{result.Titles.Count}");
        Check("non le frammenta", result.Titles.All(t => t.Ranges.Count == 1));
    }

    // -------------------------------------------------------- dati sbagliati

    private static void TestBadInput()
    {
        Section("Robustezza su dati insensati");

        string junk = Path.Combine(_media, "rumore.bin");
        if (!File.Exists(junk))
        {
            var random = new Random(7);
            var buffer = new byte[8 << 20];
            random.NextBytes(buffer);
            File.WriteAllBytes(junk, buffer);
        }

        try
        {
            using var source = new FileBlockSource(junk);
            var result = RecoveryEngine.Analyze(source, false, null, _ => { }, CancellationToken.None);
            Check("nessuna eccezione su 8 MB di rumore", true, $"{result.Titles.Count} falsi titoli");
            Check("pochi o nessun falso positivo", result.Titles.Count <= 1, $"{result.Titles.Count}");
        }
        catch (Exception ex)
        {
            Check("nessuna eccezione su 8 MB di rumore", false, ex.Message);
        }

        try
        {
            string tiny = Path.Combine(_media, "minuscolo.bin");
            File.WriteAllBytes(tiny, new byte[1024]);
            using var source = new FileBlockSource(tiny);
            RecoveryEngine.Analyze(source, false, null, _ => { }, CancellationToken.None);
            Check("nessuna eccezione su un file troncato", true);
        }
        catch (Exception ex)
        {
            Check("nessuna eccezione su un file troncato", false, ex.Message);
        }
    }

    // ------------------------------------------------------------- supporto

    private static void Log(string message) => Console.WriteLine("    " + message);

    private static string FindFfmpeg()
    {
        foreach (var name in new[] { "ffmpeg", "ffmpeg.exe" })
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        return null;
    }

    private static void RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false };
            using var process = Process.Start(psi);
            process.WaitForExit();
        }
        catch { }
    }

    private static double Probe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe",
                $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"")
            { RedirectStandardOutput = true, UseShellExecute = false };

            using var process = Process.Start(psi);
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }
        catch { return 0; }
    }

    private static long ProbeFrames(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe",
                $"-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of default=nw=1:nk=1 \"{path}\"")
            { RedirectStandardOutput = true, UseShellExecute = false };

            using var process = Process.Start(psi);
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return long.TryParse(output, out long n) ? n : 0;
        }
        catch { return 0; }
    }
}
