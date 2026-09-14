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
/// La divisione è volutamente prudente: su un DVD-Video l'orologio interno (SCR)
/// riparte a ogni cella, quindi dividere sulle sue discontinuità produce decine di
/// frammenti inutili. Qui si divide solo dove c'è una prova concreta di stacco:
/// un buco lungo di settori non video. Il ritorno indietro dell'orologio conta solo
/// se è accompagnato da un buco, oppure se lo si chiede esplicitamente.
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

    private const int BlockSectors = 512;

    public List<MediaSegment> RejectedFragments { get; } = new();

    public List<MediaSegment> Scan(IBlockSource source, long startByte, long endByte,
                                   IProgress<string> progress, CancellationToken ct)
    {
        var segments = new List<MediaSegment>();
        var buffer = new byte[BlockSectors * 2048];

        long startSector = startByte / 2048;
        long endSector = Math.Min(endByte / 2048, source.Length / 2048 - 1);
        if (endSector < startSector) return segments;

        long segmentStart = -1, segmentEnd = -1;
        long gapRun = 0;
        bool segmentHasGaps = false;
        ulong previousScr = 0;
        bool havePreviousScr = false;
        double accumulated = 0;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        long totalSectors = endSector - startSector + 1;

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

        for (long sector = startSector; sector <= endSector; sector += BlockSectors)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(BlockSectors, endSector - sector + 1);
            source.ReadBytes(sector * 2048, count * 2048, buffer, 0);

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

                gapRun = 0;
                ulong scr = ReadScr(buffer, offset);

                if (segmentStart < 0)
                {
                    segmentStart = current;
                    accumulated = 0;
                    havePreviousScr = false;
                    segmentHasGaps = false;
                }
                else if (havePreviousScr)
                {
                    long delta = (long)scr - (long)previousScr;

                    if (delta >= 0 && delta < 90000 * 2)
                    {
                        accumulated += delta;
                    }
                    else if (SplitOnClockReset && delta < -(long)(ClockResetSeconds * 90000))
                    {
                        // stacco richiesto esplicitamente dall'utente
                        long previousEnd = segmentEnd;
                        Close();
                        segmentStart = current;
                        segmentEnd = current;
                    }
                }

                previousScr = scr;
                havePreviousScr = true;
                segmentEnd = current;
            }

            if (progress != null && watch.ElapsedMilliseconds > 400)
            {
                watch.Restart();
                long done = sector + count - startSector;
                double mb = done * 2048.0 / 1048576.0;
                progress.Report($"Ricerca video: {mb:F0} MB analizzati, {segments.Count + (segmentStart >= 0 ? 1 : 0)} tratti");
            }
        }

        Close();
        return segments;
    }

    /// <summary>True se il settore inizia con un pack header MPEG valido.</summary>
    public static bool IsPackHeader(byte[] b, int o)
    {
        if (o + 14 > b.Length) return false;
        if (b[o] != 0x00 || b[o + 1] != 0x00 || b[o + 2] != 0x01 || b[o + 3] != 0xBA) return false;

        int b4 = b[o + 4];

        if ((b4 & 0xC0) == 0x40)
        {
            // MPEG-2: i marker bit eliminano quasi tutti i falsi riconoscimenti
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

    /// <summary>Durata di un tratto già individuato, letta dagli orologi di inizio e fine.</summary>
    public static double MeasureSeconds(IBlockSource source, long startByte, long length)
    {
        var buffer = new byte[2048];
        double accumulated = 0;
        ulong previous = 0;
        bool have = false;

        long sectors = length / 2048;
        long step = Math.Max(1, sectors / 4000);   // campionamento: basta per una stima solida

        for (long i = 0; i < sectors; i += step)
        {
            if (source.ReadBytes(startByte + i * 2048, 2048, buffer, 0) < 2048) break;
            if (!IsPackHeader(buffer, 0)) continue;

            ulong scr = ReadScr(buffer, 0);
            if (have)
            {
                long delta = (long)scr - (long)previous;
                if (delta > 0 && delta < 90000L * 60 * 10) accumulated += delta;
            }
            previous = scr;
            have = true;
        }

        return accumulated / 90000.0;
    }
}
