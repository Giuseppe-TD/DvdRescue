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
        TestVideoNotAtStart();
        TestReadVolume();
        TestQuickScan();
        await TestFileNames();
        TestSettings();
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
        var result = RecoveryEngine.Analyze(source, false, null, null, Log, CancellationToken.None);

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

        var withStructures = RecoveryEngine.Analyze(source, false, null, null, _ => { }, CancellationToken.None);
        var withScan = RecoveryEngine.Analyze(source, true, null, null, _ => { }, CancellationToken.None);

        Console.WriteLine($"  leggendo le IFO:        {withStructures.Titles.Count} titoli");
        Console.WriteLine($"  scandendo i settori:    {withScan.Titles.Count} titoli");

        Check("le strutture del disco danno pochi titoli", withStructures.Titles.Count <= 3,
              $"{withStructures.Titles.Count}");
        Check("anche la scansione prudente non esplode in frammenti", withScan.Titles.Count <= 4,
              $"{withScan.Titles.Count}");

        // la vecchia regola (taglio a ogni salto d'orologio) produceva 9 titoli su questo disco
        var aggressive = new MpegPsCarver
        {
            SkipEmptyAreas = false,      // scansione completa, come faceva la prima versione
            SplitOnClockReset = true,
            ClockResetSeconds = 0.1,
            MaxGapSectors = 64,
            MinSegmentSectors = 128
        };
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
        var result = RecoveryEngine.Analyze(source, false, null, null, _ => { }, CancellationToken.None);

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
        var result = RecoveryEngine.Analyze(source, false, null, null, _ => { }, CancellationToken.None);
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
        var result = RecoveryEngine.Analyze(source, false, null, null, _ => { }, CancellationToken.None);

        foreach (var t in result.Titles)
            Console.WriteLine($"    #{t.Index} {t.DurationText} {t.SizeText,8}  {t.Origin}");

        Check("ricade sulla scansione diretta", result.Profile == DVDRescue.Recovery.DiscProfile.RawVideo);
        Check("trova le due registrazioni separate", result.Titles.Count == 2, $"{result.Titles.Count}");
        Check("non le frammenta", result.Titles.All(t => t.Ranges.Count == 1));
    }

    // ------------------------------------------------ quanto disco viene letto

    /// <summary>
    /// Il tempo dell'analisi è proporzionale ai byte che si chiedono al lettore: su un lettore
    /// ottico vero si viaggia sui 10-20 MB/s, quindi leggere tutto il disco per capire dove
    /// sono i video significa minuti di attesa.
    /// </summary>
    private sealed class CountingSource : IBlockSource
    {
        private readonly IBlockSource _inner;
        public long BytesRead;

        /// <summary>Numero di richieste inoltrate: su un lettore ottico ognuna costa una ricerca.</summary>
        public long Operations;

        public CountingSource(IBlockSource inner) => _inner = inner;

        public string Name => _inner.Name;
        public int BlockSize => _inner.BlockSize;
        public long TotalBlocks => _inner.TotalBlocks;
        public long Length => _inner.Length;
        public long BadBlockCount => _inner.BadBlockCount;

        public int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
        {
            BytesRead += (long)count * BlockSize;
            Operations++;
            return _inner.ReadBlocks(block, count, destination, destinationOffset);
        }

        public bool TryReadBlock(long block, byte[] destination, int destinationOffset)
        {
            BytesRead += BlockSize;
            Operations++;
            return _inner.TryReadBlock(block, destination, destinationOffset);
        }

        public int ReadBytes(long byteOffset, int count, byte[] destination, int destinationOffset)
        {
            BytesRead += count;
            Operations++;
            return _inner.ReadBytes(byteOffset, count, destination, destinationOffset);
        }

        public void Dispose() { }
    }

    private static void TestQuickScan()
    {
        Section("Lettura rapida quando serve un file unico");

        string path = Path.Combine(_media, "raw_grande.bin");
        if (!File.Exists(path)) { Console.WriteLine("  (raw_grande.bin assente, salto)"); return; }

        using var innerFast = new FileBlockSource(path);
        using var fast = new CountingSource(innerFast);
        var quick = RecoveryEngine.Analyze(fast, false, null, null, _ => { }, CancellationToken.None,
                                           preciseSplit: false);

        using var innerFull = new FileBlockSource(path);
        using var full = new CountingSource(innerFull);
        var precise = RecoveryEngine.Analyze(full, false, null, null, _ => { }, CancellationToken.None,
                                             preciseSplit: true);

        Console.WriteLine($"  file unico:        {fast.BytesRead / 1048576.0:F1} MB letti, " +
                          $"{fast.Operations} richieste, {quick.Titles.Count} titolo/i");
        Console.WriteLine($"  separati:          {full.BytesRead / 1048576.0:F1} MB letti, " +
                          $"{full.Operations} richieste, {precise.Titles.Count} titoli");

        Check("la lettura rapida produce un titolo solo", quick.Titles.Count == 1, $"{quick.Titles.Count}");
        Check("ed è marcata come rapida", quick.QuickScan);
        Check("legge molto meno del disco", fast.BytesRead < full.BytesRead / 2,
              $"{fast.BytesRead / 1048576.0:F1} MB contro {full.BytesRead / 1048576.0:F1} MB");
        Check("la durata è comunque plausibile", quick.Titles.Count > 0 && quick.Titles[0].Seconds > 1,
              quick.Titles.Count > 0 ? $"{quick.Titles[0].Seconds:F1} s" : "");
        Check("il titolo copre tutta l'area video",
              quick.Titles.Count > 0 && quick.Titles[0].Bytes > 250L * 1024 * 1024,
              quick.Titles.Count > 0 ? quick.Titles[0].SizeText : "");
    }

    private static void TestVideoNotAtStart()
    {
        Section("Video che non comincia all'inizio del disco");

        string path = Path.Combine(_media, "raw_offset.bin");
        if (!File.Exists(path)) { Console.WriteLine("  (raw_offset.bin assente, salto)"); return; }

        using var inner = new FileBlockSource(path);
        using var counter = new CountingSource(inner);

        var quick = RecoveryEngine.Analyze(counter, false, null, null, _ => { }, CancellationToken.None,
                                           preciseSplit: false);

        Console.WriteLine($"  lettura rapida: {quick.Titles.Count} titolo/i, " +
                          $"{counter.BytesRead / 1048576.0:F1} MB letti, {counter.Operations} richieste");

        Check("trova il video anche se parte a 60 MB dall'inizio", quick.Titles.Count == 1,
              $"{quick.Titles.Count}");

        if (quick.Titles.Count == 1)
        {
            long startMb = quick.Titles[0].Ranges[0].Offset / 1048576;
            Console.WriteLine($"  inizio individuato a {startMb} MB");
            Check("l'inizio è quello giusto", startMb >= 59 && startMb <= 61, $"{startMb} MB");
        }

        Check("e non legge tutto il disco per trovarlo", counter.BytesRead < inner.Length / 2,
              $"{counter.BytesRead / 1048576.0:F1} MB su {inner.Length / 1048576.0:F1} MB");
    }

    private static void TestReadVolume()
    {
        Section("Quanto disco viene letto durante l'analisi");

        foreach (var (file, description) in new[]
                 {
                     ("dvd.iso", "DVD con strutture di navigazione"),
                     ("raw.bin", "disco piccolo senza filesystem"),
                     ("raw_grande.bin", "disco grande senza filesystem")
                 })
        {
            string path = Path.Combine(_media, file);
            if (!File.Exists(path)) continue;

            using var inner = new FileBlockSource(path);
            using var counter = new CountingSource(inner);

            var watch = Stopwatch.StartNew();
            var result = RecoveryEngine.Analyze(counter, false, null, null, _ => { }, CancellationToken.None);
            watch.Stop();

            double totalMb = inner.Length / 1048576.0;
            double readMb = counter.BytesRead / 1048576.0;
            double ratio = readMb * 100.0 / totalMb;

            Console.WriteLine($"  {description}: letti {readMb:F1} MB su {totalMb:F1} MB ({ratio:F0}%), " +
                              $"{counter.Operations} richieste al supporto, " +
                              $"{result.Titles.Count} titoli, {watch.ElapsedMilliseconds} ms");

            if (file == "dvd.iso")
            {
                Check("con le strutture si legge una frazione del disco", ratio < 25, $"{ratio:F0}%");

                // su un lettore ottico ogni richiesta costa 10-30 ms: contarle è più
                // indicativo dei byte
                Check("poche richieste al lettore grazie alla lettura anticipata",
                      counter.Operations < 80, $"{counter.Operations} richieste");

                // confronto diretto: la stessa analisi senza cache
                using var plainInner = new FileBlockSource(path);
                using var plainCounter = new CountingSource(plainInner);
                var noCache = new CachingBlockSource(plainCounter) { ReadAheadBlocks = 1 };
                RecoveryEngine.Analyze(noCache, false, null, null, _ => { }, CancellationToken.None);

                Console.WriteLine($"    senza lettura anticipata sarebbero {plainCounter.Operations} richieste");
                Check("la lettura anticipata riduce nettamente le richieste",
                      counter.Operations < plainCounter.Operations,
                      $"{counter.Operations} contro {plainCounter.Operations}");
            }
            else if (file == "raw.bin")
            {
                // tratto breve: leggere di seguito costa meno che saltare, quello che conta
                // è che le richieste restino poche e grandi
                Check("su un tratto breve si legge di seguito, con poche richieste",
                      counter.Operations < 60, $"{counter.Operations} richieste");
            }
            else
            {
                // qui il campionamento serve davvero: il disco è grande e in mezzo è vuoto
                Check("su un disco grande il campionamento evita di leggere tutto",
                      ratio < 40, $"{ratio:F0}%");
                Check("e lo fa senza riempire il lettore di salti",
                      counter.Operations < 400, $"{counter.Operations} richieste");
                Check("trova comunque le due registrazioni", result.Titles.Count == 2,
                      $"{result.Titles.Count}");
            }
        }
    }

    // ------------------------------------------------------------ nomi dei file

    private static async Task TestFileNames()
    {
        Section("Nomi dei file prodotti");

        string iso = Path.Combine(_media, "dvd.iso");
        string ffmpeg = FindFfmpeg();
        if (!File.Exists(iso) || ffmpeg == null) { Console.WriteLine("  (materiale assente, salto)"); return; }

        using var source = new FileBlockSource(iso);
        var result = RecoveryEngine.Analyze(source, false, null, null, _ => { }, CancellationToken.None);
        var titles = RecoveryEngine.ApplySplit(result, SplitMode.SingleFile);
        if (titles.Count == 0) { Check("nessun titolo", false); return; }

        var runner = new FfmpegRunner(ffmpeg);

        // caso tipico: solo il rimpacchettamento veloce
        string dir1 = Path.Combine(_media, "nomi1");
        if (Directory.Exists(dir1)) Directory.Delete(dir1, true);

        var onlyRemux = new ExtractOptions
        {
            OutputFolder = dir1,
            Split = SplitMode.SingleFile,
            MakeH264 = false,
            MakeRemux = true,
            Crf = 30,
            Preset = "ultrafast",
            FileNamePrefix = "ripresa"
        };

        await TitleExtractor.ExtractAsync(source, titles[0], onlyRemux, runner, null, _ => { }, CancellationToken.None);

        var produced = Directory.GetFiles(dir1).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Console.WriteLine($"  solo senza ricodifica: {string.Join(", ", produced)}");
        Check("il file si chiama come il titolo, senza suffissi", produced.Contains("ripresa.mp4"),
              string.Join(", ", produced));
        Check("nessun file con _originale", !produced.Any(n => n.Contains("originale")));

        // seconda estrazione nella stessa cartella: non deve sovrascrivere la prima
        await TitleExtractor.ExtractAsync(source, titles[0], onlyRemux, runner, null, _ => { }, CancellationToken.None);
        produced = Directory.GetFiles(dir1).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Console.WriteLine($"  dopo la seconda estrazione: {string.Join(", ", produced)}");
        Check("la seconda estrazione non sovrascrive la prima", produced.Count == 2,
              string.Join(", ", produced));

        // entrambe le uscite: qui due nomi diversi servono per forza
        string dir2 = Path.Combine(_media, "nomi2");
        if (Directory.Exists(dir2)) Directory.Delete(dir2, true);

        var both = new ExtractOptions
        {
            OutputFolder = dir2,
            Split = SplitMode.SingleFile,
            MakeH264 = true,
            MakeRemux = true,
            Crf = 30,
            Preset = "ultrafast",
            FileNamePrefix = "ripresa"
        };

        await TitleExtractor.ExtractAsync(source, titles[0], both, runner, null, _ => { }, CancellationToken.None);

        produced = Directory.GetFiles(dir2).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Console.WriteLine($"  entrambe le uscite: {string.Join(", ", produced)}");
        Check("l'MP4 ricodificato tiene il nome pulito", produced.Contains("ripresa.mp4"));
        Check("il secondo file è distinguibile", produced.Count == 2, string.Join(", ", produced));
    }

    // -------------------------------------------------- impostazioni salvate

    private static void TestSettings()
    {
        Section("Impostazioni conservate fra un avvio e l'altro");

        string path = AppSettings.FilePath;
        string backup = null;

        if (File.Exists(path))
        {
            backup = path + ".bak";
            File.Copy(path, backup, true);
        }

        try
        {
            var saved = new AppSettings
            {
                OutputFolder = @"\\server\condivisione\riprese",
                WorkFolder = @"\\server\condivisione\immagini",
                FileNamePrefix = "battesimo",
                SplitMode = 1,
                MakeH264 = false,
                MakeRemux = true,
                KeepRaw = true,
                Deinterlace = false,
                Crf = 18,
                Preset = "slow",
                ReadSpeed = 2,
                ThoroughRecovery = true,
                LastDrive = "E"
            };

            saved.Save();
            Check("il file delle impostazioni viene creato", File.Exists(path), path);

            var loaded = AppSettings.Load();

            Check("il percorso di rete viene conservato",
                  loaded.OutputFolder == @"\\server\condivisione\riprese", loaded.OutputFolder);
            Check("il prefisso dei file viene conservato", loaded.FileNamePrefix == "battesimo");
            Check("la modalità di divisione viene conservata", loaded.SplitMode == 1);
            Check("le scelte di formato vengono conservate",
                  !loaded.MakeH264 && loaded.MakeRemux && loaded.KeepRaw);
            Check("qualità, preset e velocità vengono conservati",
                  loaded.Crf == 18 && loaded.Preset == "slow" && loaded.ReadSpeed == 2);
            Check("il lettore usato l'ultima volta viene conservato", loaded.LastDrive == "E");

            // un file rovinato non deve impedire l'avvio
            File.WriteAllText(path, "{ questo non è JSON");
            var fallback = AppSettings.Load();
            Check("un file danneggiato non blocca il programma", fallback != null && fallback.Crf == 20);
        }
        finally
        {
            try
            {
                if (backup != null) { File.Copy(backup, path, true); File.Delete(backup); }
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
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
            var result = RecoveryEngine.Analyze(source, false, null, null, _ => { }, CancellationToken.None);
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
            RecoveryEngine.Analyze(source, false, null, null, _ => { }, CancellationToken.None);
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
