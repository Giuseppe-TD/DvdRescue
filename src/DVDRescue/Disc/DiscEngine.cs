using DVDRescue.Media;
using DVDRescue.Model;
using DVDRescue.Native;

namespace DVDRescue.Disc;

/// <summary>
/// Logica di recupero: interroga il lettore per capire quanto è stato realmente scritto,
/// crea l'immagine grezza, individua i titoli e li converte.
/// </summary>
public static class DiscEngine
{
    public const int SectorSize = 2048;

    // ------------------------------------------------------- analisi supporto

    public sealed class DriveProbeResult
    {
        public OpticalDrive Drive;
        public DriveSectorSource Source;
        public DiscAnalysis Analysis = new();
    }

    /// <summary>
    /// Apre il lettore, legge lo stato del disco e determina l'ultimo settore realmente scritto.
    /// </summary>
    public static DriveProbeResult ProbeDrive(string driveLetter, Action<string> log, CancellationToken ct,
                                              int readSpeedKbPerSec = 0)
    {
        var drive = OpticalDrive.Open(driveLetter);
        var analysis = new DiscAnalysis { SourceName = $"Unità {driveLetter}:" };

        try
        {
            if (drive.OpenedAsPhysicalDevice)
                log("Il volume non era accessibile: sto usando direttamente il device fisico del lettore.");

            if (readSpeedKbPerSec > 0)
            {
                bool set = drive.SetReadSpeed(readSpeedKbPerSec);
                log(set
                    ? $"Velocità di lettura limitata a {readSpeedKbPerSec} KB/s (≈{readSpeedKbPerSec / 1385.0:F0}x)."
                    : "Il lettore non ha accettato la limitazione di velocità.");
            }

            if (!drive.TestUnitReady())
            {
                log("Il lettore non risponde ancora, attendo l'avvio del disco...");
                for (int i = 0; i < 15 && !drive.TestUnitReady(); i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(1000);
                }
            }

            var di = drive.ReadDiscInformation();
            if (di != null)
            {
                analysis.DiscStatusText = di.StatusText;
                log($"Stato disco: {di.StatusText}, {di.Sessions} sessione/i, tracce {di.FirstTrack}–{di.LastTrackLastSession}");
                if (di.DiscStatus == 0)
                    analysis.Notes.Add("Il lettore dichiara il disco vuoto: può succedere con i dischi delle videocamere. Si prova comunque a leggere.");
            }
            else
            {
                log("READ DISC INFORMATION non supportata dal lettore.");
            }

            var phys = drive.ReadDvdPhysicalInfo();
            if (phys != null)
            {
                analysis.MediaTypeText = $"{phys.BookTypeText}, {phys.Layers} strato/i";
                log($"Supporto: {phys.BookTypeText}, area dati {phys.DataAreaStart}–{phys.DataAreaEnd}");
            }

            long lastWritten = -1;

            if (di != null)
            {
                int firstTrack = Math.Max(1, di.FirstTrack);
                int lastTrack = Math.Max(firstTrack, di.LastTrackLastSession);

                for (int t = firstTrack; t <= Math.Min(lastTrack, 99); t++)
                {
                    ct.ThrowIfCancellationRequested();
                    var ti = drive.ReadTrackInformation(t);
                    if (ti == null) continue;

                    long end = ti.EstimatedLastWrittenLba;
                    log($"Traccia {t}: start {ti.TrackStart}, dimensione {ti.TrackSize}, " +
                        $"NWA {(ti.NwaValid ? ti.NextWritableAddress.ToString() : "n/d")}, " +
                        $"ultimo registrato {(ti.LraValid ? ti.LastRecordedAddress.ToString() : "n/d")}" +
                        (ti.Blank ? " [vuota]" : "") + (ti.Damage ? " [danneggiata]" : ""));

                    if (!ti.Blank && end > lastWritten) lastWritten = end;
                }
            }

            if (lastWritten <= 0)
            {
                long cap = drive.ReadCapacity();
                if (cap > 0)
                {
                    lastWritten = cap;
                    log($"Nessuna informazione utile dalle tracce: uso READ CAPACITY ({cap} settori).");
                }
                else if (phys != null && phys.DataAreaEnd > phys.DataAreaStart)
                {
                    lastWritten = phys.DataAreaEnd - phys.DataAreaStart;
                    log($"Uso l'area dati fisica del DVD come limite ({lastWritten} settori).");
                }
                else
                {
                    lastWritten = 2_295_104; // capacità di un DVD single layer
                    log("Nessun dato dal lettore: uso la capacità standard di un DVD-5 come limite.");
                }
            }

            var source = new DriveSectorSource(drive, lastWritten + 1, ownsDrive: false);

            // verifica reale: il valore dichiarato dal drive va spesso corretto
            long verified = VerifyLastReadable(source, lastWritten, log, ct);
            if (verified > 0 && verified != lastWritten)
                log($"Limite corretto dopo la verifica: ultimo settore leggibile {verified} (dichiarato {lastWritten}).");

            if (verified > 0) lastWritten = verified;

            source.TotalSectors = lastWritten + 1;
            analysis.LastWrittenLba = lastWritten;
            analysis.ScannedSectors = lastWritten + 1;

            log($"Area da recuperare: {lastWritten + 1} settori ≈ {(lastWritten + 1) * 2048.0 / 1048576.0:F0} MB");

            return new DriveProbeResult { Drive = drive, Source = source, Analysis = analysis };
        }
        catch
        {
            drive.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Cerca l'ultimo settore effettivamente leggibile: i dischi non finalizzati
    /// dichiarano spesso valori ottimistici (o non ne dichiarano affatto).
    /// </summary>
    private static long VerifyLastReadable(DriveSectorSource source, long declared, Action<string> log, CancellationToken ct)
    {
        if (declared <= 0) return -1;

        if (source.ProbeSector(declared))
        {
            // il valore dichiarato è leggibile: prova a estendere (ricerca esponenziale)
            long hi = declared;
            long step = 1024;
            long ceiling = Math.Max(declared * 2, 4_600_000);

            while (hi + step < ceiling && source.ProbeSector(hi + step))
            {
                ct.ThrowIfCancellationRequested();
                hi += step;
                step *= 2;
            }

            if (hi == declared) return declared;

            long lo = hi, high = Math.Min(hi + step, ceiling);
            while (lo + 1 < high)
            {
                ct.ThrowIfCancellationRequested();
                long mid = lo + (high - lo) / 2;
                if (source.ProbeSector(mid)) lo = mid; else high = mid;
            }
            return lo;
        }

        // il valore dichiarato non è leggibile: ricerca binaria verso il basso
        log("L'ultimo settore dichiarato non è leggibile: cerco il limite reale...");

        long low = 0, upper = declared;
        if (!source.ProbeSector(0))
        {
            log("Attenzione: nemmeno il primo settore è leggibile. Disco illeggibile o vuoto.");
            return -1;
        }

        int iterations = 0;
        while (low + 1 < upper && iterations++ < 40)
        {
            ct.ThrowIfCancellationRequested();
            long mid = low + (upper - low) / 2;
            if (source.ProbeSector(mid)) low = mid; else upper = mid;
            if (iterations % 5 == 0)
                log($"  ricerca del limite: fra i settori {low} e {upper}...");
        }

        return low;
    }

    // ------------------------------------------------------- immagine grezza

    /// <summary>
    /// Copia l'area scritta del disco in un file immagine grezzo. Leggere una volta sola
    /// riduce lo stress su dischi vecchi e permette di rifare l'analisi senza rileggere.
    /// </summary>
    public static async Task<long> CreateImageAsync(ISectorSource source, string imagePath,
                                                    long firstLba, long lastLba,
                                                    IProgress<ProgressReport> progress,
                                                    Action<string> log, CancellationToken ct)
    {
        const int blockSectors = 256;
        var buffer = new byte[blockSectors * SectorSize];
        long total = lastLba - firstLba + 1;
        long recovered = 0;

        string imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath));
        if (!string.IsNullOrEmpty(imageDir)) Directory.CreateDirectory(imageDir);

        await using var fs = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (long lba = firstLba; lba <= lastLba; lba += blockSectors)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(blockSectors, lastLba - lba + 1);
            int ok = source.Read(lba, count, buffer, 0);
            recovered += ok;

            await fs.WriteAsync(buffer.AsMemory(0, count * SectorSize), ct);

            long done = lba + count - firstLba;
            double mb = done * 2048.0 / 1048576.0;
            double sec = sw.Elapsed.TotalSeconds;

            progress?.Report(new ProgressReport
            {
                Stage = "Copia del disco",
                Detail = $"{mb:F0} MB di {total * 2048.0 / 1048576.0:F0} MB" +
                         (source is DriveSectorSource d && d.BadSectorCount > 0 ? $" — {d.BadSectorCount} settori illeggibili" : ""),
                Percent = done * 100.0 / total,
                CurrentSector = done,
                TotalSectors = total,
                SpeedMbPerSec = sec > 0.5 ? mb / sec : 0
            });
        }

        await fs.FlushAsync(ct);

        if (source is DriveSectorSource ds && ds.BadSectorCount > 0)
            log($"Copia completata con {ds.BadSectorCount} settori illeggibili (riempiti di zeri).");
        else
            log("Copia completata senza errori di lettura.");

        return total - recovered;
    }

    // ------------------------------------------------------ analisi contenuto

    public static DiscAnalysis ScanContent(ISectorSource source, DiscAnalysis analysis,
                                           IProgress<ProgressReport> progress,
                                           Action<string> log, CancellationToken ct)
    {
        analysis ??= new DiscAnalysis();
        long lastLba = analysis.LastWrittenLba > 0 ? analysis.LastWrittenLba : source.TotalSectors - 1;

        // 1. filesystem: se c'è, dà nomi, date e posizione esatta dei file video
        var udf = UdfReader.TryRead(source, source.TotalSectors, log);
        if (udf != null && udf.Files.Count > 0)
        {
            analysis.Files = udf.Files;
            analysis.FilesystemInfo = $"UDF — volume \"{udf.VolumeIdentifier}\"";
        }
        else
        {
            var iso = Iso9660Reader.TryRead(source, log);
            if (iso != null && iso.Count > 0)
            {
                analysis.Files = iso;
                analysis.FilesystemInfo = "ISO 9660";
            }
            else
            {
                analysis.FilesystemInfo = "nessun filesystem leggibile (recupero diretto dallo stream)";
            }
        }

        analysis.Kind = ClassifyDisc(analysis.Files);
        log($"Tipo di contenuto: {DescribeKind(analysis.Kind)}");

        // 2. individuazione dei titoli
        var carver = new MpegCarver();
        var videoFiles = analysis.Files
            .Where(f => IsVideoFile(f.Path))
            .OrderBy(f => f.Extents.Count > 0 ? f.Extents[0].Lba : long.MaxValue)
            .ToList();

        List<VideoTitle> titles;

        if (videoFiles.Count > 0)
        {
            log($"File video nel filesystem: {string.Join(", ", videoFiles.Select(f => f.Path))}");

            // i VOB/VRO sono fisicamente contigui: si scansiona l'intero intervallo che li contiene,
            // così una registrazione a cavallo di due file non viene spezzata in due titoli
            long minLba = long.MaxValue, maxLba = 0;
            foreach (var f in videoFiles)
                foreach (var ex in f.Extents)
                {
                    if (ex.Lba < minLba) minLba = ex.Lba;
                    if (ex.Lba + ex.SectorCount - 1 > maxLba) maxLba = ex.Lba + ex.SectorCount - 1;
                }

            if (maxLba > lastLba && lastLba > 0) maxLba = lastLba;

            titles = minLba <= maxLba
                ? carver.Scan(source, minLba, maxLba, progress, ct)
                : new List<VideoTitle>();

            foreach (var t in titles)
            {
                var owner = videoFiles.FirstOrDefault(f =>
                    f.Extents.Any(ex => t.StartLba >= ex.Lba && t.StartLba < ex.Lba + ex.SectorCount));
                if (owner != null)
                {
                    t.SourceFile = owner.Path;
                    t.Recorded ??= owner.Modified;
                }
            }

            if (titles.Count == 0)
            {
                log("I file dichiarati non contengono stream leggibili: passo alla scansione completa del disco.");
                titles = carver.Scan(source, 0, lastLba, progress, ct);
            }
        }
        else
        {
            log("Scansione completa dell'area scritta alla ricerca di stream MPEG...");
            titles = carver.Scan(source, 0, lastLba, progress, ct);
        }

        if (titles.Count == 0 && carver.RejectedFragments.Count > 0)
        {
            log($"Nessun titolo oltre la soglia minima: recupero comunque {carver.RejectedFragments.Count} frammenti più corti.");
            titles = carver.RejectedFragments.OrderBy(t => t.StartLba).ToList();
        }
        else if (carver.RejectedFragments.Count > 0)
        {
            log($"{carver.RejectedFragments.Count} frammenti troppo corti sono stati ignorati.");
        }

        for (int i = 0; i < titles.Count; i++) titles[i].Index = i + 1;

        analysis.Titles = titles;
        analysis.VideoSectors = titles.Sum(t => t.SectorCount);

        if (source is DriveSectorSource dss) analysis.BadSectors = dss.BadSectorCount;

        log(titles.Count == 0
            ? "Nessun video individuato sul disco."
            : $"Trovati {titles.Count} titoli, {analysis.VideoSectors * 2048.0 / 1048576.0:F0} MB di video.");

        return analysis;
    }

    private static bool IsVideoFile(string path)
    {
        string p = path.ToUpperInvariant();
        return p.EndsWith(".VOB") || p.EndsWith(".VRO") || p.EndsWith(".MPG") ||
               p.EndsWith(".MPEG") || p.EndsWith(".M2P") || p.EndsWith(".SRO");
    }

    private static DiscKind ClassifyDisc(List<DiscFileEntry> files)
    {
        if (files == null || files.Count == 0) return DiscKind.RawStream;

        bool vr = files.Any(f => f.Path.ToUpperInvariant().Contains("VR_MOVIE.VRO") ||
                                 f.Path.ToUpperInvariant().Contains("VR_MANGR.IFO") ||
                                 f.Path.ToUpperInvariant().Contains("DVD_RTAV"));
        if (vr) return DiscKind.DvdVr;

        bool video = files.Any(f => f.Path.ToUpperInvariant().Contains("VIDEO_TS"));
        if (video)
        {
            bool hasIfo = files.Any(f => f.Path.ToUpperInvariant().EndsWith("VIDEO_TS.IFO"));
            return hasIfo ? DiscKind.DvdVideo : DiscKind.DvdPlusVr;
        }

        return DiscKind.RawStream;
    }

    public static string DescribeKind(DiscKind kind) => kind switch
    {
        DiscKind.DvdVr => "DVD-VR (modalità VR, tipica delle videocamere miniDVD)",
        DiscKind.DvdVideo => "DVD-Video (struttura VIDEO_TS completa)",
        DiscKind.DvdPlusVr => "DVD-Video incompleto / +VR (registrazione non chiusa)",
        DiscKind.RawStream => "stream MPEG grezzi senza struttura di navigazione",
        _ => "sconosciuto"
    };

    // ------------------------------------------------------------ estrazione

    /// <summary>Scrive su file i settori di un titolo, scartando i settori non validi.</summary>
    public static async Task<long> WriteTitleStreamAsync(ISectorSource source, VideoTitle title, string outputPath,
                                                         IProgress<ProgressReport> progress, CancellationToken ct)
    {
        const int blockSectors = 256;
        var buffer = new byte[blockSectors * SectorSize];
        long written = 0, processed = 0;
        long total = Math.Max(1, title.SectorCount);

        await using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);

        foreach (var extent in title.Extents)
        {
            for (long lba = extent.Lba; lba < extent.Lba + extent.SectorCount; lba += blockSectors)
            {
                ct.ThrowIfCancellationRequested();

                int count = (int)Math.Min(blockSectors, extent.Lba + extent.SectorCount - lba);
                source.Read(lba, count, buffer, 0);

                // scrive solo i settori che contengono davvero un pack MPEG
                int runStart = -1;
                for (int i = 0; i < count; i++)
                {
                    bool valid = MpegCarver.IsPackHeader(buffer, i * SectorSize);
                    if (valid && runStart < 0) runStart = i;

                    if ((!valid || i == count - 1) && runStart >= 0)
                    {
                        int end = valid ? i + 1 : i;
                        int len = (end - runStart) * SectorSize;
                        if (len > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(runStart * SectorSize, len), ct);
                            written += len;
                        }
                        runStart = -1;
                    }
                }

                processed += count;
                progress?.Report(new ProgressReport
                {
                    Stage = $"Estrazione titolo {title.Index}",
                    Detail = $"{written / 1048576.0:F0} MB estratti",
                    Percent = processed * 100.0 / total,
                    CurrentSector = processed,
                    TotalSectors = total
                });
            }
        }

        await fs.FlushAsync(ct);
        return written;
    }

    public static string BuildBaseName(VideoTitle title, ExtractOptions options)
    {
        string name = $"{options.FileNamePrefix}_{title.Index:00}";
        if (title.Recorded.HasValue)
            name += "_" + title.Recorded.Value.ToString("yyyy-MM-dd_HHmm");
        return name;
    }
}
