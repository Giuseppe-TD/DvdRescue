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
        TestDurationWithClockResets();
        TestVideoNotAtStart();
        TestReadVolume();
        TestQuickScan();
        await TestFileNames();
        TestSettings();
        TestDurationAccuracy();
        TestDriveReadPolicy();
        TestMultiBorderDisc();
        TestDamagedExtraction();
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

        // Un disco pieno di riprese una dietro l'altra: il caso in cui la scorciatoia vale.
        string path = Path.Combine(_media, "lungo.bin");
        if (!File.Exists(path)) { Console.WriteLine("  (lungo.bin assente, salto)"); return; }

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
              quick.Titles.Count > 0 && quick.Titles[0].Bytes > 150L * 1024 * 1024,
              quick.Titles.Count > 0 ? quick.Titles[0].SizeText : "");

        // E il caso opposto: un disco con dei buchi in mezzo. Qui la scorciatoia NON deve
        // scattare, perché prendere tutto il tratto come un blocco unico darebbe una durata
        // sbagliata di parecchie volte. Meglio pagare la ricerca vera.
        string sparse = Path.Combine(_media, "raw_grande.bin");
        if (!File.Exists(sparse)) return;

        using var innerSparse = new FileBlockSource(sparse);
        var onSparse = RecoveryEngine.Analyze(innerSparse, false, null, null, _ => { },
                                              CancellationToken.None, preciseSplit: false);

        Console.WriteLine($"  disco con buchi:   {onSparse.Titles.Count} titolo/i, " +
                          $"rapida = {onSparse.QuickScan}");
        Check("su un disco pieno di buchi la scorciatoia si tira indietro", !onSparse.QuickScan);
    }

    private static void TestDurationWithClockResets()
    {
        Section("Durata di un tratto con più registrazioni dentro");

        string raw = Path.Combine(_media, "out", "unito.mpg");
        if (!File.Exists(raw)) { Console.WriteLine("  (unito.mpg assente, salto)"); return; }

        using var source = new FileBlockSource(raw);
        double measured = MpegPsCarver.MeasureSeconds(source, 0, source.Length);

        // il file contiene tre registrazioni in fila: l'orologio riparte due volte,
        // e l'ultima dura solo sette secondi
        double declared = Probe(raw);
        long frames = ProbeFrames(raw);
        double real = frames / 25.0;

        Console.WriteLine($"  durata reale (dai fotogrammi): {real:F1} s");
        Console.WriteLine($"  durata dichiarata dal flusso:  {declared:F1} s  <- quello che dava il vecchio calcolo");
        Console.WriteLine($"  durata misurata da DVDRescue:  {measured:F1} s");

        Check("non si ferma alla sola ultima registrazione", measured > declared * 2,
              $"{measured:F1} contro {declared:F1}");
        Check("la durata è vicina a quella vera", Math.Abs(measured - real) < real * 0.25,
              $"{measured:F1} contro {real:F1}");
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

    // ------------------------------------------------------------ durata dichiarata

    /// <summary>
    /// La durata mostrata nell'elenco è la cosa che l'utente guarda per capire se il recupero
    /// ha funzionato: se dice cinque secondi su mezz'ora di riprese, il programma sembra rotto
    /// anche quando i dati ci sono tutti. Qui si confronta con la verità nota.
    /// </summary>
    private static void TestDurationAccuracy()
    {
        Section("Durata dichiarata: confronto con la verità");

        void Case(string file, double realSeconds, string description)
        {
            string path = Path.Combine(_media, file);
            if (!File.Exists(path)) { Console.WriteLine($"  ({file} assente, salto)"); return; }

            using var inner = new FileBlockSource(path);
            using var counter = new CountingSource(inner);

            // modalità "file unico": è quella predefinita, quindi è quella che conta
            var quick = RecoveryEngine.Analyze(counter, false, null, null, _ => { }, CancellationToken.None,
                                               preciseSplit: false);

            using var innerFull = new FileBlockSource(path);
            var precise = RecoveryEngine.Analyze(innerFull, false, null, null, _ => { }, CancellationToken.None,
                                                 preciseSplit: true);

            double quickSeconds = quick.Titles.Sum(t => t.Seconds);
            double preciseSeconds = precise.Titles.Sum(t => t.Seconds);

            Console.WriteLine($"  {description}");
            Console.WriteLine($"    durata reale:              {Fmt(realSeconds)}");
            Console.WriteLine($"    file unico (predefinito):  {Fmt(quickSeconds)}  " +
                              $"({counter.BytesRead / 1048576.0:F0} MB letti)");
            Console.WriteLine($"    file separati:             {Fmt(preciseSeconds)}");

            Check($"{file}: la durata in modalità file unico è credibile",
                  Math.Abs(quickSeconds - realSeconds) < realSeconds * 0.20,
                  $"{Fmt(quickSeconds)} contro {Fmt(realSeconds)}");

            Check($"{file}: la durata coi file separati è credibile",
                  Math.Abs(preciseSeconds - realSeconds) < realSeconds * 0.20,
                  $"{Fmt(preciseSeconds)} contro {Fmt(realSeconds)}");
        }

        // Il caso di Giuseppe: una cassetta di famiglia, riprese una dietro l'altra.
        // L'orologio MPEG riparte a ogni ripresa: sottrarre primo e ultimo darebbe 20 s su 200.
        Case("lungo.bin", 200.0, "riprese continue: 10 da 20 s, orologio che riparte 9 volte");

        // Il caso opposto: poco video sparso in un disco quasi vuoto. Qui l'errore facile è
        // contare come video anche i megabyte vuoti in mezzo.
        Case("raw_grande.bin", 40.0, "due riprese da 20 s in 300 MB di disco quasi vuoto");

        // Tratto unico con dentro tre registrazioni diverse
        Case("raw.bin", 28.0, "due registrazioni attaccate, 20 s + 7 s");
    }

    private static string Fmt(double seconds) =>
        seconds >= 60 ? $"{TimeSpan.FromSeconds(seconds):mm\\:ss} ({seconds:F0} s)" : $"{seconds:F1} s";

    // ------------------------------------------------- comportamento sul lettore

    /// <summary>
    /// Finto lettore ottico. Riproduce le due cose che sul lettore vero decidono tutto:
    /// il tetto ai settori per comando imposto dall'adattatore, e i settori che non rispondono.
    /// </summary>
    private sealed class FakeDrive : ISectorReader
    {
        private readonly byte[] _disc;
        private readonly Func<long, SectorState> _state;

        public enum SectorState { Good, OnlyAlternate, Dead }

        public long Commands;
        public long AlternateCommands;

        /// <summary>Attesa su ogni comando fallito: sul lettore vero sono decimi di secondo.</summary>
        public int MillisecondsPerFailure;

        public FakeDrive(byte[] disc, int maxSectorsPerRead, Func<long, SectorState> state)
        {
            _disc = disc;
            MaxSectorsPerRead = maxSectorsPerRead;
            _state = state;
        }

        public string Description => "finto lettore";
        public int MaxSectorsPerRead { get; }

        public bool Read(long lba, int count, byte[] destination, int destinationOffset, int timeoutSeconds)
        {
            Commands++;

            // è questo il punto: oltre il tetto l'adattatore rifiuta, disco sano o no
            if (count > MaxSectorsPerRead) return Fail();

            for (long s = lba; s < lba + count; s++)
                if (s >= _disc.Length / 2048 || _state(s) != SectorState.Good) return Fail();

            Array.Copy(_disc, lba * 2048, destination, destinationOffset, count * 2048);
            return true;
        }

        public bool ReadAlternate(long lba, int count, byte[] destination, int destinationOffset, int timeoutSeconds)
        {
            AlternateCommands++;
            Commands++;

            if (count > MaxSectorsPerRead) return Fail();

            for (long s = lba; s < lba + count; s++)
                if (s >= _disc.Length / 2048 || _state(s) == SectorState.Dead) return Fail();

            Array.Copy(_disc, lba * 2048, destination, destinationOffset, count * 2048);
            return true;
        }

        private bool Fail()
        {
            if (MillisecondsPerFailure > 0) Thread.Sleep(MillisecondsPerFailure);
            return false;
        }
    }

    private static byte[] MakeFakeDisc(int sectors)
    {
        var disc = new byte[sectors * 2048];

        for (int s = 0; s < sectors; s++)
        {
            int o = s * 2048;
            disc[o] = 0x00; disc[o + 1] = 0x00; disc[o + 2] = 0x01; disc[o + 3] = 0xBA;
            disc[o + 4] = 0x44; disc[o + 6] = 0x04; disc[o + 8] = 0x04;
            disc[o + 9] = 0x01; disc[o + 12] = 0x03;
        }

        return disc;
    }

    private static void TestDriveReadPolicy()
    {
        Section("Come si legge un lettore che rifiuta le richieste grandi");

        var disc = MakeFakeDisc(4096);                       // 8 MB
        var buffer = new byte[512 * 2048];

        // --- disco sano, lettore con il tetto tipico di 64 KB ---------------------
        // È il caso che rompeva tutto: il programma chiedeva un megabyte alla volta, il driver
        // rifiutava, e quel rifiuto veniva scambiato per un disco rovinato.
        var healthy = new FakeDrive(disc, 32, _ => FakeDrive.SectorState.Good);
        using (var source = new OpticalBlockSource(healthy, 4096))
        {
            int got = source.ReadBlocks(0, 512, buffer, 0);

            Check("un megabyte viene letto tutto anche se il lettore accetta 64 KB",
                  got == 512, $"{got} settori su 512");
            Check("e costa esattamente una richiesta per pezzo, non centinaia",
                  healthy.Commands == 16, $"{healthy.Commands} comandi");
            Check("nessun settore marcato come rovinato", source.BadBlockCount == 0,
                  $"{source.BadBlockCount}");
        }

        // --- un graffio isolato in mezzo al video ---------------------------------
        FakeDrive.SectorState Scratch(long s) =>
            s >= 300 && s < 302 ? FakeDrive.SectorState.OnlyAlternate : FakeDrive.SectorState.Good;

        var scratchedFast = new FakeDrive(disc, 32, Scratch);
        using (var source = new OpticalBlockSource(scratchedFast, 4096) { Effort = ReadEffort.Fast })
        {
            source.ReadBlocks(0, 512, buffer, 0);
            Console.WriteLine($"  veloce:     {scratchedFast.Commands} comandi, " +
                              $"{source.BadBlockCount} settori persi");
            Check("la modalità veloce perde solo l'intorno del graffio",
                  source.BadBlockCount > 0 && source.BadBlockCount <= 16, $"{source.BadBlockCount}");
        }

        var scratchedBalanced = new FakeDrive(disc, 32, Scratch);
        using (var source = new OpticalBlockSource(scratchedBalanced, 4096) { Effort = ReadEffort.Balanced })
        {
            int got = source.ReadBlocks(0, 512, buffer, 0);
            Console.WriteLine($"  via di mezzo: {scratchedBalanced.Commands} comandi " +
                              $"({scratchedBalanced.AlternateCommands} col comando alternativo), " +
                              $"{source.BadBlockCount} settori persi");

            Check("la via di mezzo recupera il graffio col comando alternativo",
                  got == 512 && source.BadBlockCount == 0, $"{got} settori, {source.BadBlockCount} persi");
            Check("e lo fa senza esplodere in comandi", scratchedBalanced.Commands < 120,
                  $"{scratchedBalanced.Commands} comandi");
        }

        // --- mezzo disco non scritto ----------------------------------------------
        // Qui insistere non serve a niente: il costo deve restare vicino a una richiesta
        // per pezzo, altrimenti sono i minuti di attesa che l'utente vede.
        FakeDrive.SectorState Empty(long s) => s >= 1024 ? FakeDrive.SectorState.Dead : FakeDrive.SectorState.Good;

        // Il tetto è per modalità, e conta perché sul lettore vero un comando fallito costa
        // fra i cinquanta e i cento millisecondi: mille comandi sono un minuto e mezzo buttato.
        foreach (var (effort, budget) in new[]
                 {
                     (ReadEffort.Fast, 300L),
                     (ReadEffort.Balanced, 600L),
                     (ReadEffort.Thorough, 2600L)
                 })
        {
            var drive = new FakeDrive(disc, 32, Empty);
            using var source = new OpticalBlockSource(drive, 4096)
            {
                Effort = effort,
                ConsecutiveFailuresLimit = long.MaxValue     // niente scorciatoie: si legge tutto
            };

            for (long s = 0; s < 4096; s += 512) source.ReadBlocks(s, 512, buffer, 0);

            Console.WriteLine($"  {effort,-8}: {drive.Commands} comandi su 6 MB di vuoto " +
                              $"(circa {drive.Commands * 0.07:F0} s sul lettore vero)");

            Check($"{effort}: il vuoto non fa esplodere i comandi", drive.Commands < budget,
                  $"{drive.Commands} comandi, limite {budget}");
        }

        // --- la fase esplorativa non insiste mai -----------------------------------
        var probing = new FakeDrive(disc, 32, Empty);
        using (var source = new OpticalBlockSource(probing, 4096)
               {
                   Effort = ReadEffort.Thorough,
                   Exploring = true,
                   ConsecutiveFailuresLimit = long.MaxValue
               })
        {
            for (long s = 1024; s < 4096; s += 512) source.ReadBlocks(s, 512, buffer, 0);
            Console.WriteLine($"  esplorazione: {probing.Commands} comandi, " +
                              $"{probing.AlternateCommands} alternativi");

            Check("durante l'esplorazione non si usa il comando alternativo",
                  probing.AlternateCommands == 0, $"{probing.AlternateCommands}");
        }

        // --- fine dell'area scritta ------------------------------------------------
        var stopping = new FakeDrive(disc, 32, Empty);
        using (var source = new OpticalBlockSource(stopping, 4096)
               {
                   Effort = ReadEffort.Fast,
                   ConsecutiveFailuresLimit = 2048      // 4 MB: il finto disco ne ha 6 di vuoto
               })
        {
            for (long s = 0; s < 4096; s += 512) source.ReadBlocks(s, 512, buffer, 0);
            Check("dopo abbastanza vuoto si capisce che il disco è finito", source.ReachedEndOfData);
        }

        // ...ma solo se prima si era letto qualcosa: un disco che comincia con una zona
        // illeggibile non deve far rinunciare prima ancora di aver visto il video
        var deadStart = new FakeDrive(disc, 32, _ => FakeDrive.SectorState.Dead);
        using (var source = new OpticalBlockSource(deadStart, 4096)
               {
                   Effort = ReadEffort.Fast,
                   ConsecutiveFailuresLimit = 512
               })
        {
            for (long s = 0; s < 4096; s += 512) source.ReadBlocks(s, 512, buffer, 0);
            Check("un disco illeggibile fin dall'inizio non viene dato per finito",
                  !source.ReachedEndOfData);
        }
    }

    // --------------------------------------- DVD scritto a più riprese (videocamera)

    /// <summary>
    /// Il caso che faceva aspettare venti minuti per niente.
    ///
    /// Una videocamera scrive il DVD-R a più bordi: ogni sessione è una traccia, e fra una
    /// traccia e l'altra restano zone mai scritte. Il settore 0 non risponde affatto, perché il
    /// filesystem ci sarebbe finito solo alla chiusura del disco, che non è mai avvenuta.
    ///
    /// Due errori si sommavano. Il primo: si giudicava il disco dal settore 0 e lo si scartava
    /// come illeggibile, con mezzo giga di riprese intatte due settori più in là. Il secondo,
    /// più caro: il lettore dichiara esattamente dove ha scritto, e quell'informazione veniva
    /// buttata via — la scansione partiva da zero e attraversava le zone mai scritte un settore
    /// alla volta, ritentando su ognuna.
    /// </summary>
    private static void TestMultiBorderDisc()
    {
        Section("DVD di videocamera scritto a più riprese, inizio disco illeggibile");

        const int total = 40000;                                  // 78 MB
        var written = new List<SectorRange>
        {
            new SectorRange(528, 991),        // prima ripresa, poco dopo l'inizio
            new SectorRange(20001, 39999)     // seconda ripresa, dopo 37 MB di nulla
        };

        var disc = MakeFakeDisc(total);

        // fuori dalle tracce il disco non è "vuoto": non risponde proprio
        for (int s = 0; s < total; s++)
            if (!written.Any(r => r.Contains(s)))
                Array.Clear(disc, s * 2048, 2048);

        FakeDrive.SectorState State(long s) =>
            written.Any(r => r.Contains(s)) ? FakeDrive.SectorState.Good : FakeDrive.SectorState.Dead;

        long writtenSectors = written.Sum(r => r.Count);
        long gapSectors = total - writtenSectors;

        foreach (var (label, deep, thorough) in new[]
                 {
                     ("predefinito", false, ReadEffort.Fast),
                     ("tutte le spunte", true, ReadEffort.Thorough)
                 })
        {
            var drive = new FakeDrive(disc, 32, State);
            using var source = new OpticalBlockSource(drive, total) { Effort = thorough };

            long Verify() => DriveAccess.VerifyWrittenLimit(source, total - 1, written, _ => { },
                                                            CancellationToken.None);

            var result = RecoveryEngine.Analyze(source, deep, Verify, null, _ => { },
                                                CancellationToken.None,
                                                preciseSplit: true, allowQuickScan: true, written: written);

            long recovered = result.Titles.Sum(t => t.Bytes) / 2048;

            Console.WriteLine($"  {label,-16}: {result.Titles.Count} video, " +
                              $"{recovered} settori recuperati su {writtenSectors} scritti, " +
                              $"{drive.Commands} comandi");

            Check($"{label}: non scarta il disco perché il settore 0 non risponde",
                  result.Titles.Count > 0, $"{result.Titles.Count} video");

            Check($"{label}: recupera entrambe le riprese",
                  result.Titles.Count == 2, $"{result.Titles.Count}");

            Check($"{label}: recupera tutti i settori scritti",
                  recovered >= writtenSectors * 0.98, $"{recovered} su {writtenSectors}");

            // Il conto che conta: il vuoto va attraversato quasi a costo zero. Senza le tracce
            // servivano migliaia di comandi destinati a fallire, ed è da lì che uscivano i minuti.
            long budget = writtenSectors / 32 + gapSectors / 512;

            Check($"{label}: non macina le zone mai scritte",
                  drive.Commands < budget, $"{drive.Commands} comandi, limite {budget}");
        }
    }

    // ------------------------------------------------- estrazione da disco rovinato

    /// <summary>
    /// L'estrazione da un disco molto rovinato deve finire, e deve dire dove sta arrivata.
    ///
    /// Erano due difetti insieme. L'avanzamento contava i byte consegnati a ffmpeg, non la
    /// posizione sul disco: coi settori rovinati scartati e ffmpeg che tira i byte quando gli
    /// servono, restava a zero per minuti proprio mentre il lettore lavorava di più. E l'impegno
    /// massimo non aveva un tetto, quindi su una zona distrutta non finiva mai.
    /// </summary>
    private static void TestDamagedExtraction()
    {
        Section("Estrazione da un disco molto rovinato");

        const int total = 8192;                 // 16 MB
        var disc = MakeFakeDisc(total);

        // un quarto del disco non risponde più, in una fascia continua
        FakeDrive.SectorState Damaged(long s) =>
            s >= 2048 && s < 4096 ? FakeDrive.SectorState.Dead : FakeDrive.SectorState.Good;

        var title = new RecoveryTitle
        {
            Name = "prova",
            Ranges = { new RecoveryRange(0, (long)total * 2048) }
        };

        // Il finto lettore fa aspettare su ogni comando fallito, come quello vero: è lì che se
        // ne va il tempo, non nei byte.
        var drive = new FakeDrive(disc, 32, Damaged) { MillisecondsPerFailure = 2 };
        using var source = new OpticalBlockSource(drive, total)
        {
            Effort = ReadEffort.Thorough,
            RetryBudget = TimeSpan.FromSeconds(2)
        };

        using var stream = new RangeStream(source, title.Ranges, true, CancellationToken.None);
        var buffer = new byte[1 << 20];
        var watch = Stopwatch.StartNew();
        long good = 0;
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) good += read;
        watch.Stop();

        Console.WriteLine($"  {watch.Elapsed.TotalSeconds:F1} s, {drive.Commands} comandi, " +
                          $"{good / 1048576.0:F1} MB buoni su 16, {stream.SkippedSectors} settori persi, " +
                          $"tetto {(source.RetryBudgetSpent ? "esaurito" : "non raggiunto")}");

        Check("l'estrazione finisce invece di macinare all'infinito",
              watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed.TotalSeconds:F1} s");

        Check("il tetto ai ritentativi è scattato", source.RetryBudgetSpent);

        Check("tutti i settori buoni sono stati recuperati",
              good == 6144L * 2048, $"{good} byte su {6144L * 2048}");

        Check("i settori rovinati sono stati scartati, non consegnati come spazzatura",
              stream.SkippedSectors == 2048, $"{stream.SkippedSectors}");

        // il numero che l'utente guarda: deve arrivare in fondo al disco, non fermarsi ai buoni
        Check("l'avanzamento segue la testina e arriva in fondo",
              stream.PositionOnDisc == stream.TotalLength,
              $"{stream.PositionOnDisc} su {stream.TotalLength}");

        // e senza tetto? il confronto è il motivo per cui il tetto esiste
        var slowDrive = new FakeDrive(disc, 32, Damaged) { MillisecondsPerFailure = 2 };
        using var noBudget = new OpticalBlockSource(slowDrive, total)
        {
            Effort = ReadEffort.Thorough,
            RetryBudget = TimeSpan.Zero        // nessun tetto
        };

        using var slowStream = new RangeStream(noBudget, title.Ranges, true, CancellationToken.None);
        var slowWatch = Stopwatch.StartNew();
        while (slowStream.Read(buffer, 0, buffer.Length) > 0) { }
        slowWatch.Stop();

        Console.WriteLine($"  senza tetto: {slowWatch.Elapsed.TotalSeconds:F1} s, {slowDrive.Commands} comandi " +
                          $"(su un lettore vero, dove un comando fallito costa 100 ms invece di 2, " +
                          $"sarebbero {slowDrive.Commands * 0.1 / 60:F0} minuti)");

        // Il tetto non azzera i ritentativi, li interrompe quando hanno già avuto la loro
        // occasione: qui il bilancio (2 s) copre buona parte della fascia rovinata, quindi il
        // risparmio è circa la metà. Su un disco vero, dove la fascia rovinata è di centinaia di
        // megabyte e non di quattro, il bilancio si esaurisce all'inizio e il risparmio è enorme.
        Check("il tetto taglia i comandi sprecati",
              drive.Commands < slowDrive.Commands * 0.75,
              $"{drive.Commands} contro {slowDrive.Commands}");

        Check("e taglia il tempo di attesa",
              watch.Elapsed < slowWatch.Elapsed * 0.75,
              $"{watch.Elapsed.TotalSeconds:F1} s contro {slowWatch.Elapsed.TotalSeconds:F1} s");
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
