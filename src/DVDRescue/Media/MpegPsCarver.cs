using System.Diagnostics;
using DVDRescue.Core;

namespace DVDRescue.Media;

/// <summary>Un tratto continuo di video individuato sul supporto.</summary>
public sealed class MediaSegment
{
    public long StartByte;
    public long Length;
    public double Seconds;
    public int SectorSize = 2048;
    public bool HasGaps;

    public long EndByte => StartByte + Length;
}

/// <summary>
/// Individua gli stream MPEG-2 Program Stream direttamente nei settori del disco.
/// Serve quando filesystem e strutture di navigazione non ci sono più: ogni settore
/// da 2048 byte che contiene video inizia con un pack header MPEG (00 00 01 BA).
///
/// Due accorgimenti tengono bassi i tempi senza perdere niente per strada.
///
/// Il primo riguarda come ci si muove sul disco: un lettore ottico legge di seguito a una
/// quindicina di megabyte al secondo, ma ogni salto gli costa una ricerca della testina,
/// attorno al decimo di secondo. Assaggiare il disco a intervalli fissi sembra furbo e invece
/// è il peggio dei due mondi: si riempie il lettore di salti e si rischia di non vedere una
/// ripresa più corta dell'intervallo. Qui il video si legge sempre tutto di seguito, e si salta
/// soltanto dentro il vuoto, dopo averne letto abbastanza da sapere che vuoto è davvero.
///
/// Il secondo riguarda come si divide: su un DVD-Video l'orologio interno riparte a ogni cella,
/// quindi tagliare a ogni sua discontinuità produce decine di frammenti inutili. Si divide solo
/// dove c'è una prova concreta di stacco, cioè un buco lungo di settori non video.
/// </summary>
public sealed class MpegPsCarver
{
    /// <summary>Settori non video tollerati dentro un segmento prima di chiuderlo.</summary>
    public int MaxGapSectors { get; set; } = 512;      // 1 MB

    /// <summary>Se attivo, divide anche quando l'orologio riparte da capo.</summary>
    public bool SplitOnClockReset { get; set; }

    /// <summary>Soglia per il ritorno indietro dell'orologio, in secondi.</summary>
    public double ClockResetSeconds { get; set; } = 30.0;

    /// <summary>Segmenti più corti di così vengono scartati.</summary>
    public long MinSegmentSectors { get; set; } = 128;  // 256 KB

    /// <summary>Vuoto da leggere prima di cominciare a saltare in avanti.</summary>
    public long GapBeforeSkipBytes { get; set; } = 2L << 20;

    /// <summary>
    /// Salto massimo dentro una zona vuota. È il compromesso che conta: un salto non può mai
    /// scavalcare una registrazione più lunga di così, quindi con quattro megabyte si è certi
    /// di non perdere nulla che duri più di quattro o cinque secondi. Chi vuole la certezza
    /// assoluta disattiva i salti con <see cref="SkipEmptyAreas"/>.
    /// </summary>
    public long MaxSkipBytes { get; set; } = 4L << 20;

    /// <summary>Disattiva i salti: legge tutto di seguito.</summary>
    public bool SkipEmptyAreas { get; set; } = true;

    private const int BlockSectors = 512;               // 1 MB per richiesta

    public List<MediaSegment> RejectedFragments { get; } = new();

    /// <summary>Settori effettivamente letti durante l'ultima scansione.</summary>
    public long SectorsRead { get; private set; }

    /// <summary>Richieste inoltrate al supporto: su un lettore ottico ognuna costa una ricerca.</summary>
    public long DeviceReads { get; private set; }

    public List<MediaSegment> Scan(IBlockSource source, long startByte, long endByte,
                                   IProgress<string> progress, CancellationToken ct)
    {
        SectorsRead = 0;
        DeviceReads = 0;

        long startSector = Math.Max(0, startByte / 2048);
        long endSector = Math.Min(endByte / 2048, source.Length / 2048 - 1);
        if (endSector < startSector) return new List<MediaSegment>();

        var segments = new List<MediaSegment>();
        var buffer = new byte[BlockSectors * 2048];

        long segmentStart = -1, segmentEnd = -1;
        long gapRun = 0;
        bool segmentHasGaps = false;
        ulong previousScr = 0;
        bool havePreviousScr = false;
        double accumulated = 0;

        long gapBeforeSkip = Math.Max(BlockSectors, GapBeforeSkipBytes / 2048);
        long maxSkip = Math.Max(BlockSectors, MaxSkipBytes / 2048);
        long currentSkip = 0;
        long skipStart = -1;

        var watch = Stopwatch.StartNew();
        long total = endSector - startSector + 1;

        void Close()
        {
            if (segmentStart < 0) return;

            long sectors = segmentEnd - segmentStart + 1;
            var segment = new MediaSegment
            {
                StartByte = segmentStart * 2048,
                Length = sectors * 2048,
                Seconds = accumulated / 90000.0,
                HasGaps = segmentHasGaps
            };

            if (sectors >= MinSegmentSectors) segments.Add(segment);
            else if (sectors >= 8) RejectedFragments.Add(segment);

            segmentStart = -1;
            segmentEnd = -1;
            accumulated = 0;
            havePreviousScr = false;
            segmentHasGaps = false;
        }

        long sector = startSector;

        while (sector <= endSector)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(BlockSectors, endSector - sector + 1);
            source.ReadBlocks(sector, count, buffer, 0);
            SectorsRead += count;
            DeviceReads++;

            bool anyVideo = false;

            for (int i = 0; i < count; i++)
            {
                int offset = i * 2048;
                long current = sector + i;

                if (!IsPackHeader(buffer, offset))
                {
                    gapRun++;
                    if (segmentStart >= 0)
                    {
                        if (gapRun > MaxGapSectors) Close();
                        else segmentHasGaps = true;
                    }
                    continue;
                }

                anyVideo = true;
                gapRun = 0;
                ulong scr = ReadScr(buffer, offset);

                if (segmentStart < 0)
                {
                    // se si arriva da un salto, l'inizio vero può stare nella zona saltata
                    long realStart = current;
                    if (skipStart >= 0 && skipStart < current)
                        realStart = FindFirstVideo(source, skipStart, current, ct);

                    segmentStart = realStart;
                    accumulated = 0;
                    havePreviousScr = false;
                    segmentHasGaps = false;
                }
                else if (havePreviousScr)
                {
                    long delta = (long)scr - (long)previousScr;

                    if (delta >= 0 && delta < 90000 * 2) accumulated += delta;
                    else if (SplitOnClockReset && delta < -(long)(ClockResetSeconds * 90000))
                    {
                        Close();
                        segmentStart = current;
                        segmentEnd = current;
                    }
                }

                previousScr = scr;
                havePreviousScr = true;
                segmentEnd = current;
            }

            if (source is Recovery.OpticalBlockSource optical && optical.ReachedEndOfData)
            {
                progress?.Report("Fine dell'area scritta raggiunta: interrompo la ricerca.");
                break;
            }

            if (anyVideo)
            {
                // dentro il video non si salta mai
                currentSkip = 0;
                skipStart = -1;
                sector += count;
            }
            else if (SkipEmptyAreas && gapRun >= gapBeforeSkip)
            {
                // solo l'ultimo tratto saltato va ricontrollato se dopo si trova del video:
                // tutto quello prima è già stato letto e si sa che è vuoto
                skipStart = sector + count;

                currentSkip = currentSkip == 0
                    ? BlockSectors * 2
                    : Math.Min(currentSkip * 2, maxSkip);

                gapRun += currentSkip;
                if (segmentStart >= 0 && gapRun > MaxGapSectors) Close();

                sector += count + currentSkip;
            }
            else
            {
                sector += count;
            }

            if (progress != null && watch.ElapsedMilliseconds > 400)
            {
                watch.Restart();
                long done = Math.Min(total, sector - startSector);
                progress.Report($"Ricerca video: {done * 2048.0 / 1048576.0:F0} MB di " +
                                $"{total * 2048.0 / 1048576.0:F0} MB, " +
                                $"{SectorsRead * 2048.0 / 1048576.0:F0} MB letti, " +
                                $"{segments.Count + (segmentStart >= 0 ? 1 : 0)} tratti");
            }
        }

        Close();
        return segments;
    }

    /// <summary>
    /// Torna indietro nella zona saltata per trovare il primo settore video: si legge di
    /// seguito, che sul supporto ottico costa meno di una manciata di letture sparse.
    /// </summary>
    private long FindFirstVideo(IBlockSource source, long from, long to, CancellationToken ct)
    {
        var buffer = new byte[BlockSectors * 2048];

        for (long sector = from; sector <= to; sector += BlockSectors)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(BlockSectors, to - sector + 1);
            source.ReadBlocks(sector, count, buffer, 0);
            SectorsRead += count;
            DeviceReads++;

            for (int i = 0; i < count; i++)
                if (IsPackHeader(buffer, i * 2048)) return sector + i;
        }

        return to;
    }

    // ------------------------------------------------------------------ MPEG

    /// <summary>True se il settore inizia con un pack header MPEG valido.</summary>
    public static bool IsPackHeader(byte[] b, int o)
    {
        if (o + 14 > b.Length) return false;
        if (b[o] != 0x00 || b[o + 1] != 0x00 || b[o + 2] != 0x01 || b[o + 3] != 0xBA) return false;

        int b4 = b[o + 4];

        if ((b4 & 0xC0) == 0x40)
        {
            return (b[o + 4] & 0x04) != 0
                && (b[o + 6] & 0x04) != 0
                && (b[o + 8] & 0x04) != 0
                && (b[o + 9] & 0x01) != 0
                && (b[o + 12] & 0x03) == 0x03;
        }

        return (b4 & 0xF0) == 0x20;   // MPEG-1
    }

    /// <summary>System Clock Reference in unità da 90 kHz.</summary>
    public static ulong ReadScr(byte[] b, int o)
    {
        int b4 = b[o + 4];

        if ((b4 & 0xC0) == 0x40)
        {
            return (((ulong)(b[o + 4] >> 3) & 0x07) << 30)
                 | (((ulong)b[o + 4] & 0x03) << 28)
                 | ((ulong)b[o + 5] << 20)
                 | (((ulong)(b[o + 6] >> 3) & 0x1F) << 15)
                 | (((ulong)b[o + 6] & 0x03) << 13)
                 | ((ulong)b[o + 7] << 5)
                 | (((ulong)b[o + 8] >> 3) & 0x1F);
        }

        return (((ulong)(b[o + 4] >> 1) & 0x07) << 30)
             | ((ulong)b[o + 5] << 22)
             | (((ulong)b[o + 6] >> 1) << 15)
             | ((ulong)b[o + 7] << 7)
             | ((ulong)b[o + 8] >> 1);
    }

    /// <summary>Settori sotto i quali la durata si misura leggendo tutto: esatto e costa poco.</summary>
    private const long ExactMeasureSectors = 4096;     // 8 MB

    private const int MeasureWindows = 16;

    /// <summary>
    /// Durata di un tratto.
    ///
    /// Tre trappole, e ci sono cascato in tutte e tre prima di arrivare qui.
    ///
    /// La prima: sottrarre il primo orologio dall'ultimo sembra la via diretta ed è sbagliata,
    /// perché l'orologio dello stream riparte da capo a ogni registrazione. Su un disco di
    /// famiglia con venti riprese quella differenza misura solo l'ultima: mezz'ora di video
    /// diventa cinque secondi.
    ///
    /// La seconda: nemmeno un ritmo unico (secondi per settore) applicato a tutto il tratto
    /// funziona, perché i byte al secondo cambiano parecchio — una ripresa ferma su un muro
    /// occupa pochissimo, una piena di movimento molto di più. Il ritmo va misurato pezzo
    /// per pezzo.
    ///
    /// La terza, la più insidiosa: un tratto non è tutto video. In modalità file unico si prende
    /// l'intera area scritta, e se in mezzo ci sono megabyte vuoti — un DVD-RW con registrazioni
    /// cancellate, un disco riempito a metà — contarli al ritmo del video gonfia la durata di
    /// nove volte. Per questo di ogni assaggio si misura anche <i>quanto</i> è video.
    ///
    /// L'assaggio è una lettura da un megabyte, non una manciata di settori sparsi: su un lettore
    /// ottico un megabyte di seguito costa meno di otto letture in giro per il disco, e in cambio
    /// dà sia il ritmo sia la densità di video, contati esattamente.
    /// </summary>
    public static double MeasureSeconds(IBlockSource source, long startByte, long length)
        => MeasureSeconds(source, startByte, length, out _);

    /// <param name="videoDensity">
    /// Frazione del tratto che contiene davvero video, fra 0 e 1. Serve a chi ha preso un tratto
    /// largo sperando fosse tutto pieno: se torna bassa, quel tratto ha dei buchi dentro e
    /// conviene cercarne i confini invece di fidarsi.
    /// </param>
    /// <inheritdoc cref="MeasureSeconds(IBlockSource, long, long)"/>
    public static double MeasureSeconds(IBlockSource source, long startByte, long length,
                                        out double videoDensity)
    {
        videoDensity = 0;

        long sectors = length / 2048;
        if (sectors <= 0) return 0;

        // tratto piccolo: leggerlo tutto costa quanto assaggiarlo, e il risultato è esatto
        if (sectors <= ExactMeasureSectors) return MeasureExact(source, startByte, sectors, out videoDensity);

        long segment = sectors / MeasureWindows;

        // L'assaggio è proporzionato al tratto: su un tratto piccolo non ha senso leggerne
        // un megabyte per volta, finirebbe per leggerlo tutto due volte.
        int windowSectors = (int)Math.Clamp(sectors / 64, 64, 512);

        var buffer = new byte[windowSectors * 2048];
        var rates = new double?[MeasureWindows];
        var densities = new double[MeasureWindows];
        var measured = new List<double>();

        for (int w = 0; w < MeasureWindows; w++)
        {
            long from = w * segment;
            int count = (int)Math.Min(windowSectors, sectors - from);
            if (count <= 0) break;

            Array.Clear(buffer, 0, count * 2048);
            source.ReadBytes(startByte + from * 2048, count * 2048, buffer, 0);

            int video = 0, first = -1, last = -1;

            for (int i = 0; i < count; i++)
                if (IsPackHeader(buffer, i * 2048))
                {
                    video++;
                    if (first < 0) first = i;
                    last = i;
                }

            densities[w] = (double)video / count;
            if (video < 2) continue;

            long delta = (long)ReadScr(buffer, last * 2048) - (long)ReadScr(buffer, first * 2048);

            // orologio ripartito dentro l'assaggio: il pezzo non è misurabile, lo si stimerà
            // col ritmo degli altri
            if (delta <= 0 || delta > 90000L * 120) continue;

            double rate = delta / 90000.0 / (video - 1);    // secondi per settore di video
            rates[w] = rate;
            measured.Add(rate);
        }

        double weighted = 0, spanned = 0;

        for (int w = 0; w < MeasureWindows; w++)
        {
            long span = w == MeasureWindows - 1 ? sectors - w * segment : segment;
            weighted += span * densities[w];
            spanned += span;
        }

        videoDensity = spanned > 0 ? weighted / spanned : 0;

        if (measured.Count == 0) return 0;

        measured.Sort();
        double fallback = measured[measured.Count / 2];

        double total = 0;

        for (int w = 0; w < MeasureWindows; w++)
        {
            long span = w == MeasureWindows - 1 ? sectors - w * segment : segment;
            total += (rates[w] ?? fallback) * span * densities[w];
        }

        return total;
    }

    /// <summary>
    /// Durata esatta: si legge tutto di seguito e si sommano gli scatti d'orologio fra settori
    /// video consecutivi, saltando i punti in cui riparte. I settori non video non contano.
    /// </summary>
    private static double MeasureExact(IBlockSource source, long startByte, long sectors,
                                       out double videoDensity)
    {
        var buffer = new byte[BlockSectors * 2048];
        double accumulated = 0;
        long video = 0;
        ulong previous = 0;
        bool havePrevious = false;

        for (long sector = 0; sector < sectors; sector += BlockSectors)
        {
            int count = (int)Math.Min(BlockSectors, sectors - sector);

            Array.Clear(buffer, 0, count * 2048);
            source.ReadBytes(startByte + sector * 2048, count * 2048, buffer, 0);

            for (int i = 0; i < count; i++)
            {
                if (!IsPackHeader(buffer, i * 2048)) continue;

                video++;
                ulong scr = ReadScr(buffer, i * 2048);

                if (havePrevious)
                {
                    long delta = (long)scr - (long)previous;
                    if (delta > 0 && delta < 90000L * 2) accumulated += delta;
                }

                previous = scr;
                havePrevious = true;
            }
        }

        videoDensity = sectors > 0 ? (double)video / sectors : 0;
        return accumulated / 90000.0;
    }
}
