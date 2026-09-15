using DVDRescue.Bluray;
using DVDRescue.Core;
using DVDRescue.Dvd;
using DVDRescue.FileSystems;
using DVDRescue.Media;

namespace DVDRescue.Recovery;

/// <summary>
/// Riconosce che tipo di disco video si ha davanti e ne ricava i titoli.
///
/// L'ordine è quello che dà i risultati migliori: prima si prova a leggere le strutture
/// che il disco dichiara (IFO dei DVD, playlist dei Blu-ray), perché dicono esattamente
/// dove comincia e dove finisce ogni registrazione; solo se mancano o sono illeggibili
/// si passa alla scansione dei settori, che è più lenta e più grossolana.
/// </summary>
public static class RecoveryEngine
{
    /// <param name="verifyWrittenLimit">
    /// Restituisce l'ultimo settore davvero leggibile. Viene invocata solo se si arriva alla
    /// scansione dei settori: sui dischi con strutture leggibili quella ricerca, che costa
    /// secondi per ogni sondaggio a vuoto, va evitata del tutto.
    /// </param>
    /// <param name="preciseSplit">
    /// Vero quando all'utente servono le registrazioni separate: allora i confini vanno cercati
    /// davvero. Falso quando vuole un file unico, e si può prendere tutta l'area scritta.
    /// </param>
    /// <param name="allowQuickScan">
    /// Falso per andare dritti alla ricerca completa. Serve ai tentativi successivi al primo:
    /// il sondaggio rapido è già stato fatto e non ha trovato niente, rifarlo è tempo buttato.
    /// </param>
    public static RecoveryResult Analyze(IBlockSource source, bool forceDeepScan,
                                         Func<long> verifyWrittenLimit,
                                         IProgress<string> progress, Action<string> log,
                                         CancellationToken ct, bool preciseSplit = true,
                                         bool allowQuickScan = true)
    {
        log ??= _ => { };
        var result = new RecoveryResult { Source = source };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Le strutture del disco si leggono a piccoli morsi sparsi: davanti alla sorgente
        // si mette una cache con lettura anticipata, altrimenti ogni descrittore costa una
        // ricerca della testina. Le letture grandi le passano attraverso senza essere toccate.
        var cached = source as CachingBlockSource ?? new CachingBlockSource(source);

        // Riconoscimento e sondaggi sono esplorazione: si sta cercando dove stanno i dati,
        // non li si sta ancora recuperando. Qui si sonda anche fuori dall'area scritta, e
        // insistere su settori che non esistono costa minuti senza dare niente in cambio.
        var optical = Unwrap(source) as OpticalBlockSource;
        bool previousExploring = optical?.Exploring ?? false;
        if (optical != null) optical.Exploring = true;

        try
        {
            // ------------------------------------------------------------ filesystem
            if (!forceDeepScan)
            {
                ReadFileSystem(cached, result, log, ct);
                log($"Filesystem letto in {watch.ElapsedMilliseconds} ms " +
                    $"({cached.DeviceReads} letture al lettore).");
            }

            result.Profile = Classify(result.Files);
            result.ProfileText = Describe(result.Profile);
            log($"Tipo di disco: {result.ProfileText}");

            // -------------------------------------------------------------- titoli
            switch (result.Profile)
            {
                case DiscProfile.DvdVideo:
                case DiscProfile.DvdPartial:
                    if (!BuildFromDvdVideo(cached, result, log, ct))
                        BuildFromScan(source, optical, result, verifyWrittenLimit, forceDeepScan, preciseSplit, allowQuickScan, log, progress, ct);
                    break;

                case DiscProfile.DvdVr:
                    if (!BuildFromDvdVr(cached, optical, result, log, ct))
                        BuildFromScan(source, optical, result, verifyWrittenLimit, forceDeepScan, preciseSplit, allowQuickScan, log, progress, ct);
                    break;

                case DiscProfile.Bdmv:
                case DiscProfile.Bdav:
                case DiscProfile.Avchd:
                    if (!BuildFromBluray(cached, result, log, ct))
                        BuildFromScan(source, optical, result, verifyWrittenLimit, forceDeepScan, preciseSplit, allowQuickScan, log, progress, ct);
                    break;

                default:
                    BuildFromScan(source, optical, result, verifyWrittenLimit, forceDeepScan, preciseSplit, allowQuickScan, log, progress, ct);
                    break;
            }
        }
        finally
        {
            if (optical != null) optical.Exploring = previousExploring;
        }

        log($"Analisi completata in {watch.Elapsed.TotalSeconds:F1} s " +
            $"({cached.DeviceReads} letture al lettore, {cached.CacheHits} servite dalla cache).");

        for (int i = 0; i < result.Titles.Count; i++) result.Titles[i].Index = i + 1;
        for (int i = 0; i < result.ChapterTitles.Count; i++) result.ChapterTitles[i].Index = i + 1;

        result.HasChapters = result.ChapterTitles.Count > result.Titles.Count;

        double total = result.Titles.Sum(t => t.Seconds);
        log(result.Titles.Count == 0
            ? "Nessun video individuato."
            : $"Trovati {result.Titles.Count} video, durata complessiva {TimeSpan.FromSeconds(total):hh\\:mm\\:ss}.");

        return result;
    }

    // --------------------------------------------------------------- filesystem

    /// <summary>Restituisce la sorgente sotto l'eventuale cache.</summary>
    private static IBlockSource Unwrap(IBlockSource source)
        => source is CachingBlockSource caching ? caching.Inner : source;

    private static void ReadFileSystem(IBlockSource source, RecoveryResult result,
                                       Action<string> log, CancellationToken ct)
    {
        IFileSystem best = null;
        int bestCount = 0;

        // UDF per primo: sui dischi video c'è quasi sempre ed è il più informativo.
        // Appena uno dei due dà dei file ci si ferma: leggerli entrambi raddoppia il lavoro
        // e sui DVD-Video il risultato è lo stesso.
        foreach (var candidate in EnumerateFileSystems(source))
        {
            try
            {
                candidate.Scan(false, ct);
                int count = CountFiles(candidate);
                log($"{candidate.TypeName}: {count} file.");

                if (count > bestCount) { best = candidate; bestCount = count; }
                if (bestCount > 0) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log($"{candidate.TypeName}: lettura fallita ({ex.Message}).");
            }
        }

        if (best == null || bestCount == 0)
        {
            result.FilesystemInfo = "nessun filesystem leggibile";
            log("Nessun filesystem leggibile: si passa alla ricerca diretta nei settori.");
            return;
        }

        result.FilesystemInfo = best.TypeName;
        result.VolumeLabel = best.VolumeLabel ?? "";
        foreach (var n in best.Notes) result.Notes.Add(n);

        foreach (var entry in Walk(best.Root))
        {
            if (entry.IsDirectory || entry.Extents.Count == 0) continue;

            string path = entry.FullPath.Replace('\\', '/');
            long offset = entry.Extents[0].Offset;
            long length = entry.Length > 0 ? entry.Length : entry.Extents.Sum(e => e.Length);

            if (entry.Extents.Count > 1)
                result.Notes.Add($"Il file {path} è frammentato in {entry.Extents.Count} tratti.");

            result.Files[path] = (offset, length);
        }
    }

    private static IEnumerable<IFileSystem> EnumerateFileSystems(IBlockSource source)
    {
        if (UdfFileSystem.Detect(source)) yield return new UdfFileSystem(source);
        if (Iso9660FileSystem.Detect(source)) yield return new Iso9660FileSystem(source);
    }

    private static int CountFiles(IFileSystem fs)
    {
        int n = 0;
        foreach (var e in Walk(fs.Root)) if (!e.IsDirectory) n++;
        return n;
    }

    private static IEnumerable<FsEntry> Walk(FsEntry root)
    {
        var stack = new Stack<FsEntry>();
        foreach (var c in root.Children) stack.Push(c);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            foreach (var c in e.Children) stack.Push(c);
        }
    }

    // ----------------------------------------------------------- classificazione

    private static DiscProfile Classify(Dictionary<string, (long Offset, long Length)> files)
    {
        if (files.Count == 0) return DiscProfile.RawVideo;

        bool Has(string fragment) =>
            files.Keys.Any(k => k.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);

        if (Has("DVD_RTAV") || Has("VR_MANGR.IFO") || Has("VR_MOVIE.VRO")) return DiscProfile.DvdVr;

        if (Has("AVCHD/BDMV") || Has("AVCHD\\BDMV") || Has("/AVCHD/")) return DiscProfile.Avchd;
        if (Has("BDAV/")) return DiscProfile.Bdav;
        if (Has("BDMV/")) return DiscProfile.Bdmv;

        if (Has("VIDEO_TS"))
            return Has("VIDEO_TS.IFO") || Has("VIDEO_TS.BUP") ? DiscProfile.DvdVideo : DiscProfile.DvdPartial;

        if (files.Keys.Any(k => k.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
                                k.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)))
            return DiscProfile.Avchd;

        if (files.Keys.Any(k => k.EndsWith(".vob", StringComparison.OrdinalIgnoreCase)))
            return DiscProfile.DvdPartial;

        return DiscProfile.RawVideo;
    }

    public static string Describe(DiscProfile profile) => profile switch
    {
        DiscProfile.DvdVideo => "DVD-Video con strutture di navigazione complete",
        DiscProfile.DvdVr => "DVD-VR (registratore o videocamera miniDVD)",
        DiscProfile.DvdPartial => "DVD-Video incompleto o registrazione non chiusa",
        DiscProfile.Bdmv => "Blu-ray video (BDMV)",
        DiscProfile.Bdav => "Blu-ray registrato (BDAV)",
        DiscProfile.Avchd => "AVCHD (videocamera)",
        DiscProfile.RawVideo => "nessuna struttura leggibile: recupero diretto dallo stream",
        _ => "sconosciuto"
    };

    // ------------------------------------------------------------- DVD-Video

    private static bool BuildFromDvdVideo(IBlockSource source, RecoveryResult result,
                                          Action<string> log, CancellationToken ct)
    {
        long Resolve(string path)
        {
            foreach (var kv in result.Files)
                if (kv.Key.EndsWith(path, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals(path, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.Offset;
            return -1;
        }

        DvdVideoStructure structure;
        try { structure = DvdVideoIfo.Parse(source, Resolve, log); }
        catch (Exception ex) { log($"Lettura delle IFO fallita: {ex.Message}"); return false; }

        foreach (var n in structure.Notes) result.Notes.Add(n);

        if (!structure.IsValid || structure.Titles.Count == 0)
        {
            log("Le IFO non sono utilizzabili: passo alla scansione dei settori.");
            return false;
        }

        foreach (var title in structure.Titles)
        {
            ct.ThrowIfCancellationRequested();

            var ranges = MergeRanges(title.Cells
                .Where(c => !c.IsAngleBlock || c.CellId <= 1)
                .Select(c => new RecoveryRange(c.FirstSector * 2048L, c.SectorCount * 2048L)));

            if (ranges.Count == 0) continue;

            var entry = new RecoveryTitle
            {
                Name = $"titolo {title.TitleNumber}",
                Origin = $"IFO — titolo {title.TitleNumber} (VTS {title.VtsNumber})",
                Seconds = title.Seconds > 0 ? title.Seconds : MpegPsCarver.MeasureSeconds(source, ranges[0].Offset, ranges[0].Length),
                VideoInfo = string.Join(", ", new[] { title.VideoFormat }.Concat(title.Audio).Where(s => !string.IsNullOrWhiteSpace(s))),
                Ranges = ranges
            };

            result.Titles.Add(entry);

            // capitoli, per chi li vuole come file separati
            int chapterNumber = 0;
            foreach (var chapter in title.Chapters)
            {
                chapterNumber++;
                var chapterRanges = MergeRanges(chapter.Cells
                    .Where(c => !c.IsAngleBlock || c.CellId <= 1)
                    .Select(c => new RecoveryRange(c.FirstSector * 2048L, c.SectorCount * 2048L)));

                if (chapterRanges.Count == 0) continue;

                result.ChapterTitles.Add(new RecoveryTitle
                {
                    Name = $"titolo {title.TitleNumber} capitolo {chapterNumber}",
                    Origin = $"IFO — titolo {title.TitleNumber}, capitolo {chapterNumber}",
                    Seconds = chapter.Seconds,
                    VideoInfo = entry.VideoInfo,
                    Ranges = chapterRanges
                });
            }
        }

        if (result.Titles.Count == 0) return false;

        log($"Strutture DVD-Video lette: {result.Titles.Count} titoli, {result.ChapterTitles.Count} capitoli.");
        return true;
    }

    // ---------------------------------------------------------------- DVD-VR

    private static bool BuildFromDvdVr(IBlockSource source, OpticalBlockSource optical,
                                       RecoveryResult result,
                                       Action<string> log, CancellationToken ct)
    {
        var ifo = result.Files.FirstOrDefault(f => f.Key.EndsWith("VR_MANGR.IFO", StringComparison.OrdinalIgnoreCase));
        var vro = result.Files.FirstOrDefault(f => f.Key.EndsWith("VR_MOVIE.VRO", StringComparison.OrdinalIgnoreCase));

        if (vro.Key == null)
        {
            log("VR_MOVIE.VRO non trovato.");
            return false;
        }

        DateTime? fileDate = null;
        long vroStart = vro.Value.Offset;
        long vroLength = vro.Value.Length;

        if (ifo.Key != null)
        {
            var data = new byte[Math.Min(ifo.Value.Length, 4 << 20)];
            source.ReadBytes(ifo.Value.Offset, data.Length, data, 0);

            DvdVrStructure structure = null;
            try { structure = DvdVrIfo.Parse(data, vroStart / 2048, log); }
            catch (Exception ex) { log($"Lettura di VR_MANGR.IFO fallita: {ex.Message}"); }

            if (structure != null)
            {
                foreach (var n in structure.Notes) result.Notes.Add(n);

                if (structure.IsValid && structure.Programs.Count > 0)
                {
                    foreach (var program in structure.Programs)
                    {
                        var ranges = MergeRanges(program.Ranges
                            .Select(r => new RecoveryRange(r.FirstSector * 2048L,
                                                           (r.LastSector - r.FirstSector + 1) * 2048L)));
                        if (ranges.Count == 0) continue;

                        result.Titles.Add(new RecoveryTitle
                        {
                            Name = string.IsNullOrWhiteSpace(program.Label) ? $"registrazione {program.Number}" : program.Label,
                            Origin = $"VR_MANGR.IFO — programma {program.Number}",
                            Seconds = program.Seconds,
                            Recorded = program.Recorded,
                            Ranges = ranges
                        });
                    }

                    if (result.Titles.Count > 0)
                    {
                        log($"Strutture DVD-VR lette: {result.Titles.Count} registrazioni.");
                        return true;
                    }
                }

                // anche senza intervalli, le date servono a nominare i file
                if (structure.Programs.Count > 0 && structure.Programs[0].Recorded.HasValue)
                    fileDate = structure.Programs[0].Recorded;
            }
        }

        // Ripiego: il VRO si analizza da solo. Qui non si esplora più, si legge il video vero,
        // quindi torna in vigore l'impegno richiesto.
        log("Analizzo direttamente VR_MOVIE.VRO.");
        if (optical != null) optical.Exploring = false;
        var carver = new MpegPsCarver();
        var segments = carver.Scan(source, vroStart, vroStart + vroLength, null, ct);

        foreach (var segment in segments)
        {
            result.Titles.Add(new RecoveryTitle
            {
                Name = $"registrazione {result.Titles.Count + 1}",
                Origin = "VR_MOVIE.VRO",
                Seconds = segment.Seconds,
                Recorded = fileDate,
                Ranges = { new RecoveryRange(segment.StartByte, segment.Length) }
            });
        }

        return result.Titles.Count > 0;
    }

    // --------------------------------------------------------------- Blu-ray

    private static bool BuildFromBluray(IBlockSource source, RecoveryResult result,
                                        Action<string> log, CancellationToken ct)
    {
        BlurayStructure structure;
        try { structure = BlurayReader.Parse(source, result.Files, log); }
        catch (Exception ex) { log($"Lettura delle strutture Blu-ray fallita: {ex.Message}"); return false; }

        foreach (var n in structure.Notes) result.Notes.Add(n);
        if (!structure.IsValid || structure.Titles.Count == 0) return false;

        var carver = new TsCarver();

        foreach (var title in structure.Titles)
        {
            ct.ThrowIfCancellationRequested();

            var ranges = MergeRanges(title.Clips
                .Where(c => c.Offset >= 0 && c.Length > 0)
                .Select(c => new RecoveryRange(c.Offset, c.Length)));

            if (ranges.Count == 0) continue;

            int packetSize = 192;
            try
            {
                var packeting = carver.DetectPacketingAt(source, ranges[0].Offset, Math.Min(ranges[0].Length, 1 << 20));
                if (packeting != TsPacketing.None) packetSize = (int)packeting;
            }
            catch { /* si resta sul valore tipico dei .m2ts */ }

            var first = title.Clips.FirstOrDefault();

            result.Titles.Add(new RecoveryTitle
            {
                Name = string.IsNullOrWhiteSpace(title.PlaylistName) ? $"titolo {title.Number}" : $"titolo {title.Number}",
                Origin = string.IsNullOrWhiteSpace(title.PlaylistName) ? "elenco dei file" : $"playlist {title.PlaylistName}",
                Seconds = title.Seconds,
                IsTransportStream = true,
                PacketSize = packetSize,
                VideoInfo = string.Join(" ", new[] { first?.Codec, first?.Resolution }.Where(s => !string.IsNullOrWhiteSpace(s))),
                Ranges = ranges
            });
        }

        return result.Titles.Count > 0;
    }

    // ------------------------------------------------------ scansione settori

    private static void BuildFromScan(IBlockSource source, OpticalBlockSource optical, RecoveryResult result,
                                      Func<long> verifyWrittenLimit, bool forceDeepScan, bool preciseSplit,
                                      bool allowQuickScan,
                                      Action<string> log, IProgress<string> progress, CancellationToken ct)
    {
        long start = 0;
        long end = source.Length;

        // se il filesystem esiste ma le strutture no, si cercano solo i file video:
        // in quel caso l'estensione dell'area scritta non serve e non va cercata
        var videoFiles = result.Files
            .Where(f => IsVideoFile(f.Key))
            .OrderBy(f => f.Value.Offset)
            .ToList();

        if (videoFiles.Count > 0)
        {
            log($"File video nel filesystem: {string.Join(", ", videoFiles.Select(f => f.Key))}");
            start = videoFiles.Min(f => f.Value.Offset);
            end = videoFiles.Max(f => f.Value.Offset + f.Value.Length);
        }
        else
        {
            // qui sì: si sta per passare in rassegna il disco, conviene sapere dove finisce
            if (verifyWrittenLimit != null)
            {
                long limit = verifyWrittenLimit();
                if (limit > 0)
                {
                    result.LastWrittenSector = limit;
                    end = (limit + 1) * 2048L;
                }
            }
            else if (result.LastWrittenSector > 0)
            {
                end = (result.LastWrittenSector + 1) * 2048L;
            }

            // Se all'utente serve un file solo, i confini fra una registrazione e l'altra non
            // interessano: si prende tutta l'area scritta e si misura la durata con due letture,
            // invece di passare in rassegna un gigabyte alla velocità del lettore.
            if (!preciseSplit && allowQuickScan && TryQuickScan(source, optical, result, start, end, log, ct))
                return;

            log($"Ricerca degli stream video fino al settore {end / 2048 - 1}...");
        }

        // Da qui in poi non si esplora più, si recupera: torna in vigore l'impegno richiesto
        // e il timeout pieno, perché ogni settore strappato al lettore è video in più.
        if (optical != null) optical.Exploring = false;

        // Program Stream (DVD). Con la scansione approfondita si rinuncia anche ai salti
        // dentro le zone vuote: più lento, ma non può sfuggire nemmeno una clip di un secondo.
        var psCarver = new MpegPsCarver { SkipEmptyAreas = !forceDeepScan };
        var psSegments = psCarver.Scan(source, start, end, progress, ct);

        foreach (var segment in psSegments)
        {
            result.Titles.Add(new RecoveryTitle
            {
                Name = $"registrazione {result.Titles.Count + 1}",
                Origin = videoFiles.Count > 0 ? OwnerOf(videoFiles, segment.StartByte) : "scansione diretta",
                Seconds = segment.Seconds,
                Ranges = { new RecoveryRange(segment.StartByte, segment.Length) }
            });
        }

        if (result.Titles.Count > 0)
        {
            if (psCarver.RejectedFragments.Count > 0)
                log($"{psCarver.RejectedFragments.Count} frammenti troppo corti ignorati.");
            return;
        }

        // Transport Stream (Blu-ray, AVCHD)
        log("Nessun Program Stream: cerco stream in formato Transport Stream...");
        var tsCarver = new TsCarver();
        var tsSegments = tsCarver.Scan(source, start, end, progress, ct);

        foreach (var segment in tsSegments)
        {
            result.Titles.Add(new RecoveryTitle
            {
                Name = $"registrazione {result.Titles.Count + 1}",
                Origin = "scansione diretta (Transport Stream)",
                Seconds = segment.Seconds,
                IsTransportStream = true,
                PacketSize = segment.PacketSize,
                VideoInfo = string.Join(" ", new[] { segment.VideoCodec, segment.Resolution }.Where(s => !string.IsNullOrWhiteSpace(s))),
                Ranges = { new RecoveryRange(segment.StartOffset, segment.Length) }
            });
        }

        // ultima spiaggia: i frammenti scartati
        if (result.Titles.Count == 0 && psCarver.RejectedFragments.Count > 0)
        {
            log($"Recupero {psCarver.RejectedFragments.Count} frammenti brevi: è tutto quello che è rimasto leggibile.");
            foreach (var fragment in psCarver.RejectedFragments)
                result.Titles.Add(new RecoveryTitle
                {
                    Name = $"frammento {result.Titles.Count + 1}",
                    Origin = "frammento",
                    Seconds = fragment.Seconds,
                    Ranges = { new RecoveryRange(fragment.StartByte, fragment.Length) }
                });
        }
    }

    /// <summary>
    /// Prende l'intera area scritta come un'unica registrazione: cerca il primo e l'ultimo
    /// settore video e si ferma lì. Costa due letture invece dell'intero disco, e va benissimo
    /// quando l'uscita richiesta è un file unico — i settori inutili vengono comunque scartati
    /// durante l'estrazione.
    /// </summary>
    private static bool TryQuickScan(IBlockSource source, OpticalBlockSource optical, RecoveryResult result,
                                     long startByte, long endByte, Action<string> log, CancellationToken ct)
    {
        const int window = 512;   // 1 MB per lettura
        long lastSector = endByte / 2048 - 1;
        long firstSector = startByte / 2048;
        if (lastSector <= firstSector) return false;

        var buffer = new byte[window * 2048];

        // Un settore solo prima di chiedere il megabyte. Se quello non risponde è quasi certo
        // che non risponda nemmeno il resto, e insistere su un megabyte di vuoto vuol dire
        // centinaia di comandi buttati: è esattamente quello che faceva aspettare minuti.
        bool Readable(long sector) => optical == null || optical.ProbeSector(sector);

        long FirstVideoIn(long sector, int count)
        {
            if (count <= 0 || !Readable(sector)) return -1;

            source.ReadBlocks(sector, count, buffer, 0);

            for (int i = 0; i < count; i++)
                if (MpegPsCarver.IsPackHeader(buffer, i * 2048)) return sector + i;

            return -1;
        }

        // ------------------------------------------------------------- inizio del video
        // Non sempre comincia subito: su un disco formattato in modalità VR la testa è occupata
        // da strutture e riserve, e il video parte anche parecchio più avanti. I primi megabyte
        // si leggono quindi di seguito, così una ripresa breve in testa non sfugge; poi si
        // assaggia a salti fino in fondo all'area scritta, invece di arrendersi a metà disco.
        long probeStep = window * 8;                          // un assaggio ogni 8 MB
        long contiguousUntil = firstSector + window * 16;     // primi 16 MB letti di seguito
        long foundAt = -1;
        long step = window;
        long sector = firstSector;

        while (sector <= lastSector && foundAt < 0)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(window, lastSector - sector + 1);
            if (count <= 0) break;

            foundAt = FirstVideoIn(sector, count);

            sector += step;
            if (sector >= contiguousUntil) step = probeStep;
        }

        if (foundAt < 0)
        {
            log("Il sondaggio rapido non ha trovato video: passo alla ricerca completa.");
            return false;
        }

        // ritorno indietro per il punto esatto di inizio: il salto può aver scavalcato la testa
        long start = foundAt;
        long backFrom = Math.Max(firstSector, foundAt - probeStep);

        for (long s = backFrom; s < foundAt; s += window)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(window, foundAt - s);
            long hit = FirstVideoIn(s, count);
            if (hit >= 0) { start = hit; break; }
        }

        // ------------------------------------------------------------- fine del video
        // Cercata a ritroso dall'ultimo settore scritto: su un disco non finalizzato il video
        // finisce dove finisce la scrittura, quindi di norma basta una lettura. Il limite evita
        // di risalire all'infinito se in coda c'è una zona vuota lunga.
        long end = -1;
        long backwardLimit = Math.Max(start, lastSector - window * 32);

        for (long s = Math.Max(start, lastSector - window + 1); s > backwardLimit; s -= window)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(window, lastSector - s + 1);
            if (count <= 0) break;

            if (Readable(s))
            {
                source.ReadBlocks(s, count, buffer, 0);

                for (int i = count - 1; i >= 0; i--)
                    if (MpegPsCarver.IsPackHeader(buffer, i * 2048)) { end = s + i; break; }
            }

            if (end >= 0) break;
        }

        if (end < 0) end = lastSector;
        if (end <= start) return false;

        long length = (end - start + 1) * 2048L;
        double seconds = MpegPsCarver.MeasureSeconds(source, start * 2048L, length, out double density);

        // La lettura rapida si regge su un'assunzione: fra il primo e l'ultimo settore video c'è
        // video, non buchi. Sui dischi di famiglia è vera, ma su un DVD-RW con registrazioni
        // cancellate no — e lì prendere tutto il tratto come un blocco unico darebbe una durata
        // sbagliata di parecchie volte. Se l'assunzione non regge si lascia perdere la
        // scorciatoia e si cercano i confini davvero.
        if (density < 0.5)
        {
            log($"Fra il primo e l'ultimo video il disco è pieno solo al {density * 100:F0}%: " +
                "ci sono zone vuote in mezzo, quindi cerco i confini veri invece di tirare a indovinare.");
            return false;
        }

        result.Titles.Add(new RecoveryTitle
        {
            Name = "registrazione completa",
            Origin = "area scritta del disco",
            Seconds = seconds,
            Ranges = { new RecoveryRange(start * 2048L, length) }
        });

        result.QuickScan = true;
        log($"Area video: settori {start}–{end}, {length / 1048576.0:F0} MB, " +
            $"pieno al {density * 100:F0}%, durata {TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}.");
        log("Lettura rapida: per separare le singole registrazioni scegli un'altra divisione e rileggi il disco.");

        return true;
    }

    private static string OwnerOf(List<KeyValuePair<string, (long Offset, long Length)>> files, long offset)
    {
        foreach (var f in files)
            if (offset >= f.Value.Offset && offset < f.Value.Offset + f.Value.Length)
                return f.Key;
        return "scansione diretta";
    }

    private static bool IsVideoFile(string path)
    {
        string p = path.ToUpperInvariant();
        return p.EndsWith(".VOB") || p.EndsWith(".VRO") || p.EndsWith(".SRO") ||
               p.EndsWith(".MPG") || p.EndsWith(".MPEG") || p.EndsWith(".M2P") ||
               p.EndsWith(".M2TS") || p.EndsWith(".MTS") || p.EndsWith(".TS") ||
               p.EndsWith(".M2T") || p.EndsWith(".TOD") || p.EndsWith(".MOD");
    }

    // ------------------------------------------------------------- divisione

    /// <summary>Unisce i tratti contigui: evita di generare migliaia di intervalli da un settore.</summary>
    public static List<RecoveryRange> MergeRanges(IEnumerable<RecoveryRange> ranges)
    {
        var sorted = ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset).ToList();
        var merged = new List<RecoveryRange>();

        foreach (var range in sorted)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (range.Offset <= last.Offset + last.Length)
                {
                    long end = Math.Max(last.Offset + last.Length, range.Offset + range.Length);
                    merged[^1] = new RecoveryRange(last.Offset, end - last.Offset);
                    continue;
                }
            }
            merged.Add(range);
        }

        return merged;
    }

    /// <summary>Applica la modalità di divisione scelta dall'utente.</summary>
    public static List<RecoveryTitle> ApplySplit(RecoveryResult result, SplitMode mode)
    {
        switch (mode)
        {
            case SplitMode.PerChapter when result.ChapterTitles.Count > 0:
                return result.ChapterTitles;

            case SplitMode.SingleFile when result.Titles.Count > 0:
            {
                var all = MergeRanges(result.Titles.SelectMany(t => t.Ranges));
                var first = result.Titles[0];

                var single = new RecoveryTitle
                {
                    Index = 1,
                    Name = "registrazione completa",
                    Origin = result.Titles.Count == 1
                        ? first.Origin
                        : $"unione di {result.Titles.Count} tratti",
                    Seconds = result.Titles.Sum(t => t.Seconds),
                    Recorded = result.Titles.Select(t => t.Recorded).FirstOrDefault(d => d.HasValue),
                    IsTransportStream = first.IsTransportStream,
                    PacketSize = first.PacketSize,
                    VideoInfo = first.VideoInfo,
                    Ranges = all
                };

                return new List<RecoveryTitle> { single };
            }

            default:
                return result.Titles;
        }
    }
}
