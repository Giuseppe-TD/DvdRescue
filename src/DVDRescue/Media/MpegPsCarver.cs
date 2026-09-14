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

    /// <summary>
    /// Durata di un tratto, ricavata dall'orologio interno dello stream.
    ///
    /// Ogni assaggio costa una ricerca della testina, quindi se ne fanno pochissimi: nel caso
    /// normale bastano il primo e l'ultimo pack. Gli assaggi intermedi servono solo quando fra
    /// i due l'orologio è ripartito, cosa che succede se il tratto contiene più registrazioni.
    /// </summary>
    public static double MeasureSeconds(IBlockSource source, long startByte, long length)
    {
        long sectors = length / 2048;
        if (sectors <= 0) return 0;

        ulong? first = ScrAt(source, startByte, sectors, 0, forward: true);
        ulong? last = ScrAt(source, startByte, sectors, sectors - 1, forward: false);

        if (first.HasValue && last.HasValue)
        {
            long delta = (long)last.Value - (long)first.Value;
            if (delta > 0 && delta < 90000L * 3600 * 12) return delta / 90000.0;
        }

        // l'orologio è ripartito per strada: si somma a tratti, con una dozzina di assaggi
        const int samples = 12;
        double accumulated = 0;
        ulong? previous = first;
        long step = Math.Max(1, sectors / samples);

        for (long i = step; i < sectors; i += step)
        {
            ulong? current = ScrAt(source, startByte, sectors, i, forward: true);
            if (current.HasValue && previous.HasValue)
            {
                long delta = (long)current.Value - (long)previous.Value;
                if (delta > 0 && delta < 90000L * 3600) accumulated += delta;
            }
            if (current.HasValue) previous = current;
        }

        return accumulated / 90000.0;
    }

    /// <summary>Orologio del primo pack valido a partire dal settore indicato.</summary>
    private static ulong? ScrAt(IBlockSource source, long startByte, long sectors, long sector, bool forward)
    {
        var buffer = new byte[2048];

        for (int attempt = 0; attempt < 8; attempt++)
        {
            long index = forward ? sector + attempt : sector - attempt;
            if (index < 0 || index >= sectors) break;

            if (source.ReadBytes(startByte + index * 2048, 2048, buffer, 0) < 2048) break;
            if (IsPackHeader(buffer, 0)) return ReadScr(buffer, 0);
        }

        return null;
    }
}
