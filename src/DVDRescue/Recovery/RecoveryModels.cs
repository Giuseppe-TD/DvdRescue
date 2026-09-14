using DVDRescue.Core;

namespace DVDRescue.Recovery;

public enum DiscProfile
{
    Unknown,
    DvdVideo,       // VIDEO_TS con strutture di navigazione
    DvdVr,          // DVD_RTAV: registratori e videocamere miniDVD
    DvdPartial,     // VIDEO_TS incompleto, registrazione interrotta, +VR
    Bdmv,           // Blu-ray video
    Bdav,           // registratori/videocamere Blu-ray
    Avchd,          // videocamere AVCHD (anche su DVD da 8 cm)
    RawVideo        // nessuna struttura: solo stream trovati nei settori
}

public enum SplitMode
{
    /// <summary>Tutto il disco in un unico file.</summary>
    SingleFile,

    /// <summary>Un file per registrazione o titolo.</summary>
    PerRecording,

    /// <summary>Un file per capitolo, dove il disco li dichiara.</summary>
    PerChapter
}

public struct RecoveryRange
{
    public long Offset;
    public long Length;

    public RecoveryRange(long offset, long length) { Offset = offset; Length = length; }
    public override string ToString() => $"@{Offset}+{Length}";
}

public sealed class RecoveryTitle
{
    public int Index;
    public string Name = "";
    public string Origin = "";
    public double Seconds;
    public DateTime? Recorded;

    /// <summary>Transport Stream (Blu-ray, AVCHD) invece di Program Stream (DVD).</summary>
    public bool IsTransportStream;

    /// <summary>188 o 192 per gli stream TS; 0 per i DVD.</summary>
    public int PacketSize;

    public string VideoInfo = "";
    public List<RecoveryRange> Ranges = new();
    public bool Selected = true;

    public long Bytes
    {
        get { long n = 0; foreach (var r in Ranges) n += r.Length; return n; }
    }

    public string DurationText
    {
        get
        {
            if (Seconds <= 0.4) return "?";
            var t = TimeSpan.FromSeconds(Seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    public string SizeText => Bytes >= 1073741824L
        ? $"{Bytes / 1073741824.0:F2} GB"
        : $"{Bytes / 1048576.0:F0} MB";

    /// <summary>Estensione naturale del flusso grezzo.</summary>
    public string RawExtension => IsTransportStream ? (PacketSize == 192 ? ".m2ts" : ".ts") : ".mpg";
}

public sealed class RecoveryResult
{
    public DiscProfile Profile = DiscProfile.Unknown;
    public string ProfileText = "";
    public string FilesystemInfo = "";
    public string VolumeLabel = "";
    public string MediaText = "";
    public string DiscStatusText = "";
    public long LastWrittenSector = -1;
    public long BadSectors;

    public List<RecoveryTitle> Titles = new();

    /// <summary>Titoli spezzati per capitolo, quando il disco li dichiara.</summary>
    public List<RecoveryTitle> ChapterTitles = new();

    public List<string> Notes = new();

    /// <summary>File trovati nel filesystem: percorso → (offset in byte, lunghezza).</summary>
    public Dictionary<string, (long Offset, long Length)> Files = new(StringComparer.OrdinalIgnoreCase);

    public IBlockSource Source;

    /// <summary>True se il disco dichiara titoli/capitoli propri (IFO, playlist).</summary>
    public bool HasChapters;
}

public sealed class ProgressReport
{
    public string Stage = "";
    public string Detail = "";
    public double Percent;
    public double SpeedMbPerSec;
}

public sealed class ExtractOptions
{
    public string OutputFolder = "";
    public SplitMode Split = SplitMode.SingleFile;

    public bool MakeH264 = true;
    public bool MakeRemux;
    public bool KeepRaw;

    public int Crf = 20;
    public string Preset = "medium";
    public string AudioBitrate = "192k";
    public bool Deinterlace = true;
    public string FileNamePrefix = "video";

    /// <summary>Converte mentre legge il disco, senza file intermedi.</summary>
    public bool StreamDirectly = true;
}
