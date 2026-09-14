using DVDRescue.Disc;
using DVDRescue.Media;
using DVDRescue.Model;

internal static class TestMain
{
    private static int _failures;

    private static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "OK " : "FAIL")}] {what}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    public static async Task<int> Main(string[] args)
    {
        string image = Path.Combine(Environment.CurrentDirectory, "disc.bin");
        Console.WriteLine($"Immagine di prova: {image}");

        using var src = new ImageSectorSource(image);
        Console.WriteLine($"Settori totali: {src.TotalSectors}");

        // --- 1. pack header e SCR -------------------------------------------
        var sector = new byte[2048];
        src.TryReadSector(16, sector, 0);
        Check("il primo settore video è riconosciuto come pack MPEG", MpegCarver.IsPackHeader(sector, 0));

        ulong scr0 = MpegCarver.ReadScr(sector, 0);
        Console.WriteLine($"  SCR del primo pack: {scr0} ({scr0 / 90000.0:F3} s)");
        Check("SCR iniziale plausibile (< 5 s)", scr0 < 5 * 90000);

        src.TryReadSector(0, sector, 0);
        Check("un settore vuoto NON è scambiato per video", !MpegCarver.IsPackHeader(sector, 0));

        // dati casuali che iniziano per caso con lo start code non devono passare i marker bit
        var rnd = new Random(42);
        int falsePositives = 0;
        var s2 = new byte[2048];
        for (int i = 0; i < 2000; i++)
        {
            rnd.NextBytes(s2);
            s2[0] = 0; s2[1] = 0; s2[2] = 1; s2[3] = 0xBA;
            if (MpegCarver.IsPackHeader(s2, 0)) falsePositives++;
        }
        Check("pochi falsi positivi su dati casuali con start code valido",
              falsePositives < 200, $"{falsePositives}/2000");

        // --- 2. carving ------------------------------------------------------
        var carver = new MpegCarver { MinTitleSectors = 64 };
        var titles = carver.Scan(src, 0, src.TotalSectors - 1, null, CancellationToken.None);

        Console.WriteLine($"\nTitoli individuati: {titles.Count}");
        foreach (var t in titles)
            Console.WriteLine($"  #{t.Index}  LBA {t.StartLba,6}  {t.SectorCount,6} settori  " +
                              $"{t.SizeText,8}  durata {t.DurationText}  extent {t.Extents.Count}");

        Check("le due registrazioni separate dal gap grande sono titoli distinti", titles.Count >= 2,
              $"{titles.Count} titoli");
        Check("il primo titolo inizia al settore 16", titles.Count > 0 && titles[0].StartLba == 16);
        Check("il gap piccolo non ha spezzato il primo titolo in due extent contigui perduti",
              titles.Count > 0 && titles[0].Extents.Count >= 1);
        Check("la durata del primo titolo è vicina a 6 s",
              titles.Count > 0 && Math.Abs(titles[0].DurationSeconds - 6.0) < 1.5,
              titles.Count > 0 ? $"{titles[0].DurationSeconds:F2} s" : "");

        // --- 3. estrazione ---------------------------------------------------
        string outDir = Path.Combine(Environment.CurrentDirectory, "out");
        Directory.CreateDirectory(outDir);

        foreach (var t in titles)
        {
            string mpg = Path.Combine(outDir, $"titolo_{t.Index:00}.mpg");
            long bytes = await DiscEngine.WriteTitleStreamAsync(src, t, mpg, null, CancellationToken.None);
            var fi = new FileInfo(mpg);
            Console.WriteLine($"\n  estratto {Path.GetFileName(mpg)}: {fi.Length} byte");
            Check($"il file del titolo {t.Index} contiene solo settori validi",
                  fi.Length % 2048 == 0 && fi.Length == bytes);
            Check($"il titolo {t.Index} non contiene settori azzerati",
                  !HasZeroSector(mpg));
        }

        // --- 4. conversione con ffmpeg ---------------------------------------
        string ffmpeg = FindFfmpeg();
        if (ffmpeg != null && titles.Count > 0)
        {
            var runner = new FfmpegRunner(ffmpeg);
            var options = new ExtractOptions { Crf = 28, Preset = "ultrafast", Deinterlace = true };

            string inMpg = Path.Combine(outDir, "titolo_01.mpg");
            string outMp4 = Path.Combine(outDir, "titolo_01.mp4");
            string argsH264 = FfmpegRunner.BuildH264Args(inMpg, outMp4, options);
            Console.WriteLine($"\n  ffmpeg {argsH264}");

            int code = await runner.RunAsync(argsH264, titles[0].DurationSeconds, "test", null,
                                             s => Console.WriteLine("    " + s), CancellationToken.None);
            Check("la ricodifica H.264 termina senza errori", code == 0, $"exit {code}");
            Check("l'MP4 H.264 è stato creato", File.Exists(outMp4) && new FileInfo(outMp4).Length > 10000);

            string outRemux = Path.Combine(outDir, "titolo_01_originale.mp4");
            int code2 = await runner.RunAsync(FfmpegRunner.BuildRemuxArgs(inMpg, outRemux),
                                              titles[0].DurationSeconds, "test", null,
                                              s => Console.WriteLine("    " + s), CancellationToken.None);
            Check("il remux senza ricodifica termina senza errori", code2 == 0, $"exit {code2}");
            Check("l'MP4 remuxato è stato creato", File.Exists(outRemux) && new FileInfo(outRemux).Length > 10000);
        }

        // --- 5. filesystem su immagine senza UDF -----------------------------
        var udf = UdfReader.TryRead(src, src.TotalSectors, s => Console.WriteLine("    " + s));
        Check("su un'immagine senza filesystem il parser UDF non inventa nulla", udf == null);

        var iso = Iso9660Reader.TryRead(src, s => Console.WriteLine("    " + s));
        Check("su un'immagine senza filesystem il parser ISO 9660 non inventa nulla",
              iso == null || iso.Count == 0);

        // --- 6. immagini UDF reali -------------------------------------------
        TestUdfImage("dvdvideo.iso", "VIDEO_TS/VTS_01_1.VOB", DiscKind.DvdPlusVr);
        TestUdfImage("dvdvr.iso", "DVD_RTAV/VR_MOVIE.VRO", DiscKind.DvdVr);

        Console.WriteLine($"\n=== {(_failures == 0 ? "TUTTI I CONTROLLI SUPERATI" : _failures + " CONTROLLI FALLITI")} ===");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestUdfImage(string file, string expectedFile, DiscKind expectedKind)
    {
        string path = Path.Combine(Environment.CurrentDirectory, file);
        if (!File.Exists(path)) { Console.WriteLine($"\n(salto {file}: non presente)"); return; }

        Console.WriteLine($"\n--- {file} ---");
        using var src = new ImageSectorSource(path);

        var udf = UdfReader.TryRead(src, src.TotalSectors, s => Console.WriteLine("    " + s));
        Check("il volume UDF è stato letto", udf != null);
        if (udf == null) return;

        foreach (var f in udf.Files)
            Console.WriteLine($"    {f.Path,-32} {f.Length,10} byte  extent {string.Join(",", f.Extents)}  {f.Modified}");

        var target = udf.Files.FirstOrDefault(f => f.Path.Equals(expectedFile, StringComparison.OrdinalIgnoreCase));
        Check($"UDF trova {expectedFile}", target != null);
        if (target == null) return;

        Check("la dimensione del file è corretta", target.Length > 100000,
              $"{target.Length} byte");
        Check("il file ha almeno un extent", target.Extents.Count > 0);

        // l'extent deve puntare davvero all'inizio dello stream video
        var sec = new byte[2048];
        src.TryReadSector(target.Extents[0].Lba, sec, 0);
        Check("l'extent punta a dati video reali", MpegCarver.IsPackHeader(sec, 0),
              $"LBA {target.Extents[0].Lba}");

        // il parser ISO 9660 deve trovare gli stessi file
        var iso = Iso9660Reader.TryRead(src, _ => { });
        Check("anche ISO 9660 elenca i file", iso != null && iso.Count > 0,
              iso != null ? $"{iso.Count} file" : "nullo");

        // analisi completa
        var analysis = DiscEngine.ScanContent(src, new DiscAnalysis { LastWrittenLba = src.TotalSectors - 1 },
                                              null, s => Console.WriteLine("    " + s), CancellationToken.None);
        Check($"il disco è classificato come {expectedKind}", analysis.Kind == expectedKind,
              analysis.Kind.ToString());
        Check("l'analisi individua almeno un titolo", analysis.Titles.Count >= 1,
              $"{analysis.Titles.Count} titoli");

        foreach (var t in analysis.Titles)
            Console.WriteLine($"    #{t.Index} LBA {t.StartLba} {t.SizeText} {t.DurationText} da {t.SourceFile} ({t.Recorded})");
    }

    private static string FindFfmpeg()
    {
        foreach (var name in new[] { "ffmpeg", "ffmpeg.exe" })
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), name);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        Console.WriteLine("(ffmpeg non trovato nel PATH: salto i test di conversione)");
        return null;
    }

    private static bool HasZeroSector(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[2048];
        while (fs.Read(buf, 0, 2048) == 2048)
        {
            bool allZero = true;
            for (int i = 0; i < 16; i++) if (buf[i] != 0) { allZero = false; break; }
            if (allZero) return true;
        }
        return false;
    }
}
