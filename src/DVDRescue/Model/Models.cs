namespace DVDRescue.Model;

public enum DiscKind
{
    Unknown,
    DvdVideo,       // VIDEO_TS / VOB
    DvdVr,          // DVD_RTAV / VR_MOVIE.VRO (modalità VR, tipica delle videocamere)
    DvdPlusVr,      // +VR: VIDEO_TS parziale scritto in tempo reale
    RawStream       // nessun filesystem leggibile: solo stream MPEG grezzi
}

public sealed class DiscFileEntry
{
    public string Path = "";
    public long Length;
    public DateTime? Modified;
    public List<SectorExtent> Extents = new();

    public long TotalSectors
    {
        get { long n = 0; foreach (var e in Extents) n += e.SectorCount; return n; }
    }

    public override string ToString() => $"{Path}  ({Length / 1048576.0:F1} MB)";
}

public struct SectorExtent
{
    public long Lba;
    public long SectorCount;

    public SectorExtent(long lba, long count) { Lba = lba; SectorCount = count; }
    public override string ToString() => $"LBA {Lba} +{SectorCount}";
}

/// <summary>Un blocco contiguo di stream MPEG-2 Program Stream individuato sul disco.</summary>
public sealed class VideoTitle
{
    public int Index;
    public long StartLba;
    public long SectorCount;
    public long BadSectors;
    public double DurationSeconds;
    public DateTime? Recorded;
    public string SourceFile = "";
    public bool Selected = true;

    /// <summary>Elenco dei blocchi di settori che compongono il titolo (in ordine).</summary>
    public List<SectorExtent> Extents = new();

    public long Bytes => SectorCount * 2048L;

    public string DurationText
    {
        get
        {
            if (DurationSeconds <= 0) return "?";
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                : $"{ts.Minutes:00}:{ts.Seconds:00}";
        }
    }

    public string SizeText => Bytes >= 1073741824L
        ? $"{Bytes / 1073741824.0:F2} GB"
        : $"{Bytes / 1048576.0:F0} MB";
}

public sealed class DiscAnalysis
{
    public string SourceName = "";
    public DiscKind Kind = DiscKind.Unknown;
    public string FilesystemInfo = "";
    public string DiscStatusText = "";
    public string MediaTypeText = "";
    public long FirstLba;
    public long LastWrittenLba = -1;
    public long ScannedSectors;
    public long BadSectors;
    public long VideoSectors;
    public List<DiscFileEntry> Files = new();
    public List<VideoTitle> Titles = new();
    public List<string> Notes = new();
}

public enum OutputProfile
{
    H264Aac,        // ricodifica libx264 + AAC
    RemuxMp4,       // copia MPEG-2 video, audio in AAC
    MpgOnly         // nessuna conversione
}

public sealed class ExtractOptions
{
    public string OutputFolder = "";
    public bool MakeH264 = true;
    public bool MakeRemux;
    public bool KeepMpg;
    public int Crf = 20;
    public string Preset = "medium";
    public string AudioBitrate = "192k";
    public bool Deinterlace = true;
    public string FileNamePrefix = "titolo";
}

public sealed class ProgressReport
{
    public string Stage = "";
    public string Detail = "";
    public double Percent;
    public long CurrentSector;
    public long TotalSectors;
    public double SpeedMbPerSec;
}
