using DVDRescue.Disc;
using DVDRescue.Model;

namespace DVDRescue.Media;

/// <summary>
/// Individua gli stream MPEG-2 Program Stream direttamente sui settori del disco.
/// È il metodo che funziona anche quando filesystem, IFO e strutture di navigazione
/// non sono stati scritti (disco non finalizzato, registrazione interrotta, disco rovinato).
///
/// Su DVD ogni settore da 2048 byte che contiene video inizia con un pack header
/// MPEG-2 (00 00 01 BA), dal quale si legge anche l'orologio di riferimento (SCR):
/// serve sia per calcolare la durata reale sia per capire dove finisce una registrazione
/// e ne inizia un'altra.
/// </summary>
public sealed class MpegCarver
{
    /// <summary>Settori non-video tollerati dentro un titolo prima di considerarlo finito.</summary>
    public int MaxGapSectors { get; set; } = 64;

    /// <summary>Salto dell'orologio oltre il quale si considera iniziata una nuova registrazione.</summary>
    public double ScrJumpSeconds { get; set; } = 5.0;

    /// <summary>Titoli più piccoli di così vengono scartati (spurie, frammenti di IFO, ecc.).</summary>
    public long MinTitleSectors { get; set; } = 128;    // 256 KB

    /// <summary>Frammenti scartati perché sotto soglia: recuperati se non si trova nient'altro.</summary>
    public List<VideoTitle> RejectedFragments { get; } = new();

    private const int BlockSectors = 512;               // 1 MB per lettura

    public List<VideoTitle> Scan(ISectorSource src, long startLba, long endLba,
                                 IProgress<ProgressReport> progress, CancellationToken ct)
    {
        var titles = new List<VideoTitle>();
        var buffer = new byte[BlockSectors * 2048];

        VideoTitle current = null;
        long extentStart = -1, extentSectors = 0;
        long gapRun = 0;
        ulong prevScr = 0;
        bool havePrevScr = false;
        double accumulated = 0;
        long videoSectors = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long total = Math.Max(1, endLba - startLba + 1);

        void CloseExtent()
        {
            if (current != null && extentStart >= 0 && extentSectors > 0)
            {
                current.Extents.Add(new SectorExtent(extentStart, extentSectors));
                current.SectorCount += extentSectors;
            }
            extentStart = -1;
            extentSectors = 0;
        }

        void CloseTitle()
        {
            CloseExtent();
            if (current != null)
            {
                current.DurationSeconds = accumulated / 90000.0;
                if (current.SectorCount >= MinTitleSectors)
                {
                    current.Index = titles.Count + 1;
                    titles.Add(current);
                }
                else if (current.SectorCount >= 8)
                {
                    RejectedFragments.Add(current);
                }
            }
            current = null;
            accumulated = 0;
            havePrevScr = false;
        }

        for (long lba = startLba; lba <= endLba; lba += BlockSectors)
        {
            ct.ThrowIfCancellationRequested();

            int count = (int)Math.Min(BlockSectors, endLba - lba + 1);
            src.Read(lba, count, buffer, 0);

            for (int i = 0; i < count; i++)
            {
                int off = i * 2048;
                long thisLba = lba + i;

                if (!IsPackHeader(buffer, off))
                {
                    gapRun++;
                    CloseExtent();
                    if (current != null && gapRun > MaxGapSectors) CloseTitle();
                    continue;
                }

                gapRun = 0;
                videoSectors++;

                ulong scr = ReadScr(buffer, off);

                if (current == null)
                {
                    current = new VideoTitle { StartLba = thisLba };
                    accumulated = 0;
                    havePrevScr = false;
                }
                else if (havePrevScr)
                {
                    long delta = (long)scr - (long)prevScr;
                    if (delta < 0 || delta > (long)(ScrJumpSeconds * 90000))
                    {
                        // l'orologio è ripartito o ha fatto un salto: nuova registrazione
                        CloseTitle();
                        current = new VideoTitle { StartLba = thisLba };
                        accumulated = 0;
                        havePrevScr = false;
                    }
                    else
                    {
                        accumulated += delta;
                    }
                }

                prevScr = scr;
                havePrevScr = true;

                if (extentStart < 0) { extentStart = thisLba; extentSectors = 1; }
                else if (thisLba == extentStart + extentSectors) extentSectors++;
                else { CloseExtent(); extentStart = thisLba; extentSectors = 1; }
            }

            if (progress != null)
            {
                long done = lba + count - startLba;
                double mb = done * 2048.0 / 1048576.0;
                progress.Report(new ProgressReport
                {
                    Stage = "Ricerca video",
                    Detail = $"{titles.Count + (current != null ? 1 : 0)} titoli trovati — {mb:F0} MB analizzati",
                    Percent = done * 100.0 / total,
                    CurrentSector = done,
                    TotalSectors = total,
                    SpeedMbPerSec = sw.Elapsed.TotalSeconds > 0.5 ? mb / sw.Elapsed.TotalSeconds : 0
                });
            }
        }

        CloseTitle();

        for (int i = 0; i < titles.Count; i++) titles[i].Index = i + 1;
        return titles;
    }

    /// <summary>Analizza un intervallo già noto (es. un file VOB/VRO trovato via UDF).</summary>
    public List<VideoTitle> ScanExtents(ISectorSource src, List<SectorExtent> extents,
                                        IProgress<ProgressReport> progress, CancellationToken ct)
    {
        var all = new List<VideoTitle>();
        foreach (var e in extents)
        {
            var part = Scan(src, e.Lba, e.Lba + e.SectorCount - 1, progress, ct);
            all.AddRange(part);
        }
        for (int i = 0; i < all.Count; i++) all[i].Index = i + 1;
        return all;
    }

    // ------------------------------------------------------------------ MPEG

    /// <summary>True se il settore inizia con un pack header MPEG valido (marker bit compresi).</summary>
    public static bool IsPackHeader(byte[] b, int o)
    {
        if (b[o] != 0x00 || b[o + 1] != 0x00 || b[o + 2] != 0x01 || b[o + 3] != 0xBA) return false;

        int b4 = b[o + 4];
        if ((b4 & 0xC0) == 0x40)
        {
            // MPEG-2: verifica i marker bit, così i falsi positivi spariscono
            return (b[o + 4] & 0x04) != 0
                && (b[o + 6] & 0x04) != 0
                && (b[o + 8] & 0x04) != 0
                && (b[o + 9] & 0x01) != 0
                && (b[o + 12] & 0x03) == 0x03;
        }

        // MPEG-1 (raro su DVD, presente su alcuni VCD/registratori)
        return (b4 & 0xF0) == 0x20;
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

        // MPEG-1: SCR su 33 bit distribuiti nei byte 4..8
        return (((ulong)(b[o + 4] >> 1) & 0x07) << 30)
             | ((ulong)b[o + 5] << 22)
             | (((ulong)b[o + 6] >> 1) << 15)
             | ((ulong)b[o + 7] << 7)
             | ((ulong)b[o + 8] >> 1);
    }
}
