using System.Text;
using DVDRescue.Model;
using DVDRescue.Native;

namespace DVDRescue.Disc;

/// <summary>
/// Lettore UDF (ECMA-167) ridotto all'essenziale: serve a scoprire dove sono i file video
/// sul disco, anche quando Windows non riesce a montare il volume.
/// Sui dischi DVD-VR (videocamere) l'UDF viene aggiornato durante la registrazione,
/// quindi spesso è leggibile anche se il disco non è stato finalizzato.
/// </summary>
public sealed class UdfVolume
{
    public string VolumeIdentifier = "";
    public long PartitionStart;
    public long PartitionLength;
    public int BlockSize = 2048;
    public List<DiscFileEntry> Files = new();
}

public static class UdfReader
{
    private const int TagPrimaryVolume = 1;
    private const int TagAnchor = 2;
    private const int TagPartition = 5;
    private const int TagLogicalVolume = 6;
    private const int TagTerminating = 8;
    private const int TagFileSet = 256;
    private const int TagFileIdentifier = 257;
    private const int TagFileEntry = 261;
    private const int TagExtendedFileEntry = 266;

    public static UdfVolume TryRead(ISectorSource source, long totalSectors, Action<string> log = null)
    {
        try
        {
            return ReadInternal(source, totalSectors, log ?? (_ => { }));
        }
        catch (Exception ex)
        {
            log?.Invoke($"UDF: lettura interrotta ({ex.Message})");
            return null;
        }
    }

    private static UdfVolume ReadInternal(ISectorSource src, long totalSectors, Action<string> log)
    {
        var sector = new byte[2048];

        // 1. Anchor Volume Descriptor Pointer: posizioni previste dalla norma
        var anchorCandidates = new List<long> { 256 };
        if (totalSectors > 1) anchorCandidates.Add(totalSectors - 1);
        if (totalSectors > 256) anchorCandidates.Add(totalSectors - 256);
        anchorCandidates.Add(512);

        long vdsStart = -1, vdsLength = 0;

        foreach (long lba in anchorCandidates)
        {
            if (lba < 0 || (totalSectors > 0 && lba >= totalSectors + 512)) continue;
            if (!src.TryReadSector(lba, sector, 0)) continue;
            if (TagId(sector) != TagAnchor || !TagValid(sector, lba)) continue;

            vdsLength = ReadLe32(sector, 16);
            vdsStart = ReadLe32(sector, 20);
            log($"UDF: anchor trovato a LBA {lba}, sequenza descrittori a {vdsStart} ({vdsLength} byte)");
            break;
        }

        if (vdsStart <= 0) { log("UDF: nessun anchor valido (disco senza filesystem o non finalizzato)"); return null; }

        // 2. Volume Descriptor Sequence: partizione + volume logico
        var vol = new UdfVolume();
        long fileSetLba = -1;
        int fileSetPartition = 0;
        bool haveP = false, haveL = false;

        int vdsSectors = (int)Math.Min(64, Math.Max(1, (vdsLength + 2047) / 2048));
        for (int i = 0; i < vdsSectors; i++)
        {
            long lba = vdsStart + i;
            if (!src.TryReadSector(lba, sector, 0)) continue;

            int tag = TagId(sector);
            if (tag == TagTerminating) break;
            if (!TagValid(sector, lba)) continue;

            switch (tag)
            {
                case TagPrimaryVolume:
                    vol.VolumeIdentifier = ReadDString(sector, 24, 32);
                    break;

                case TagPartition:
                    vol.PartitionStart = ReadLe32(sector, 188);
                    vol.PartitionLength = ReadLe32(sector, 192);
                    haveP = true;
                    break;

                case TagLogicalVolume:
                    vol.BlockSize = (int)ReadLe32(sector, 212);
                    if (vol.BlockSize <= 0 || vol.BlockSize > 4096) vol.BlockSize = 2048;
                    // LogicalVolumeContentsUse @248 = long_ad del File Set Descriptor
                    fileSetLba = ReadLe32(sector, 248 + 4);
                    fileSetPartition = ReadLe16(sector, 248 + 8);
                    haveL = true;
                    break;
            }
        }

        if (!haveP || !haveL) { log("UDF: partizione o volume logico non trovati"); return null; }
        log($"UDF: volume \"{vol.VolumeIdentifier}\", partizione a LBA {vol.PartitionStart} ({vol.PartitionLength} blocchi)");

        // 3. File Set Descriptor → ICB della directory radice
        long fsdLba = vol.PartitionStart + fileSetLba;
        if (!src.TryReadSector(fsdLba, sector, 0) || TagId(sector) != TagFileSet)
        {
            log("UDF: File Set Descriptor non leggibile");
            return null;
        }

        long rootIcb = ReadLe32(sector, 400 + 4);
        var files = new List<DiscFileEntry>();
        WalkDirectory(src, vol, rootIcb, "", files, 0, log);

        vol.Files = files;
        log($"UDF: {files.Count} file individuati");
        return vol;
    }

    private static void WalkDirectory(ISectorSource src, UdfVolume vol, long icbBlock, string path,
                                      List<DiscFileEntry> files, int depth, Action<string> log)
    {
        if (depth > 8 || files.Count > 5000) return;

        var entry = ReadFileEntry(src, vol, icbBlock);
        if (entry == null || !entry.IsDirectory) return;

        byte[] dirData = ReadExtents(src, vol, entry.Extents, entry.InformationLength, entry.InlineData);
        if (dirData == null) return;

        int pos = 0;
        while (pos + 38 <= dirData.Length)
        {
            if (ReadLe16(dirData, pos) != TagFileIdentifier) break;

            int fileVersion = ReadLe16(dirData, pos + 16);
            byte characteristics = dirData[pos + 18];
            int lFi = dirData[pos + 19];
            long childBlock = ReadLe32(dirData, pos + 20 + 4);
            int lIu = ReadLe16(dirData, pos + 36);

            int nameOffset = pos + 38 + lIu;
            int total = 38 + lIu + lFi;
            total = (total + 3) & ~3; // padding a 4 byte

            bool isParent = (characteristics & 0x08) != 0;
            bool isDeleted = (characteristics & 0x04) != 0;
            bool isDir = (characteristics & 0x02) != 0;

            if (!isParent && !isDeleted && lFi > 0 && nameOffset + lFi <= dirData.Length)
            {
                string name = DecodeDString(dirData, nameOffset, lFi);
                string full = string.IsNullOrEmpty(path) ? name : path + "/" + name;

                if (isDir)
                {
                    WalkDirectory(src, vol, childBlock, full, files, depth + 1, log);
                }
                else
                {
                    var fe = ReadFileEntry(src, vol, childBlock);
                    if (fe != null)
                    {
                        files.Add(new DiscFileEntry
                        {
                            Path = full,
                            Length = fe.InformationLength,
                            Modified = fe.Modified,
                            Extents = fe.Extents
                        });
                    }
                }
            }

            if (total <= 0) break;
            pos += total;
        }
    }

    private sealed class FileEntryInfo
    {
        public bool IsDirectory;
        public long InformationLength;
        public DateTime? Modified;
        public List<SectorExtent> Extents = new();
        public byte[] InlineData;
    }

    private static FileEntryInfo ReadFileEntry(ISectorSource src, UdfVolume vol, long blockInPartition)
    {
        long lba = vol.PartitionStart + blockInPartition;
        var buf = new byte[2048];
        if (!src.TryReadSector(lba, buf, 0)) return null;

        int tag = ReadLe16(buf, 0);
        if (tag != TagFileEntry && tag != TagExtendedFileEntry) return null;

        bool extended = tag == TagExtendedFileEntry;

        // ICB Tag @16: FileType a +11, Flags a +18
        byte fileType = buf[16 + 11];
        int icbFlags = ReadLe16(buf, 16 + 18);
        int adType = icbFlags & 0x07;

        var info = new FileEntryInfo { IsDirectory = fileType == 4 };

        int lEaOffset, lAdOffset, dataOffset;
        if (extended)
        {
            info.InformationLength = (long)ReadLe64(buf, 56);
            info.Modified = ReadTimestamp(buf, 92);
            lEaOffset = 208; lAdOffset = 212; dataOffset = 216;
        }
        else
        {
            info.InformationLength = (long)ReadLe64(buf, 56);
            info.Modified = ReadTimestamp(buf, 84);
            lEaOffset = 168; lAdOffset = 172; dataOffset = 176;
        }

        long lEa = ReadLe32(buf, lEaOffset);
        long lAd = ReadLe32(buf, lAdOffset);
        int adStart = (int)(dataOffset + lEa);

        if (adStart < 0 || adStart > buf.Length) return info;

        if (adType == 3)
        {
            int len = (int)Math.Min(lAd, buf.Length - adStart);
            if (len > 0)
            {
                info.InlineData = new byte[len];
                Array.Copy(buf, adStart, info.InlineData, 0, len);
            }
            return info;
        }

        int adSize = adType == 0 ? 8 : (adType == 1 ? 16 : 20);
        int count = (int)(lAd / adSize);

        for (int i = 0; i < count; i++)
        {
            int off = adStart + i * adSize;
            if (off + adSize > buf.Length) break;

            uint rawLen = (uint)ReadLe32(buf, off);
            int extentType = (int)(rawLen >> 30);
            long byteLen = rawLen & 0x3FFFFFFF;
            long block = ReadLe32(buf, off + 4);

            if (extentType == 3) break;         // continuazione degli AD: non gestita
            if (extentType == 2) continue;      // non registrato e non allocato
            if (byteLen == 0) continue;

            long sectors = (byteLen + vol.BlockSize - 1) / vol.BlockSize;
            info.Extents.Add(new SectorExtent(vol.PartitionStart + block, sectors));
        }

        return info;
    }

    private static byte[] ReadExtents(ISectorSource src, UdfVolume vol, List<SectorExtent> extents,
                                      long maxLength, byte[] inline)
    {
        if (inline != null) return inline;
        if (extents == null || extents.Count == 0) return null;

        long totalSectors = 0;
        foreach (var e in extents) totalSectors += e.SectorCount;
        if (totalSectors <= 0 || totalSectors > 4096) totalSectors = Math.Min(totalSectors, 4096);

        var data = new byte[totalSectors * 2048];
        int written = 0;

        foreach (var e in extents)
        {
            for (long i = 0; i < e.SectorCount && written < data.Length; i++)
            {
                src.TryReadSector(e.Lba + i, data, written);
                written += 2048;
            }
        }

        if (maxLength > 0 && maxLength < data.Length)
        {
            var trimmed = new byte[maxLength];
            Array.Copy(data, trimmed, maxLength);
            return trimmed;
        }

        return data;
    }

    // ------------------------------------------------------------- utilities

    private static int TagId(byte[] b) => ReadLe16(b, 0);

    private static bool TagValid(byte[] b, long expectedLocation)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++) { if (i == 4) continue; sum += b[i]; }
        if ((byte)sum != b[4]) return false;

        long loc = ReadLe32(b, 12);
        return expectedLocation < 0 || loc == expectedLocation;
    }

    private static string ReadDString(byte[] b, int offset, int length)
    {
        if (offset + length > b.Length) return "";
        int len = b[offset + length - 1];
        if (len <= 0 || len > length - 1) len = length - 1;
        return DecodeDString(b, offset, len);
    }

    private static string DecodeDString(byte[] b, int offset, int length)
    {
        if (length <= 0 || offset + length > b.Length) return "";
        byte comp = b[offset];

        if (comp == 8)
            return Encoding.ASCII.GetString(b, offset + 1, length - 1).TrimEnd('\0', ' ');

        if (comp == 16)
        {
            var sb = new StringBuilder();
            for (int i = offset + 1; i + 1 < offset + length; i += 2)
                sb.Append((char)((b[i] << 8) | b[i + 1]));
            return sb.ToString().TrimEnd('\0', ' ');
        }

        return Encoding.ASCII.GetString(b, offset, length).TrimEnd('\0', ' ');
    }

    private static DateTime? ReadTimestamp(byte[] b, int offset)
    {
        if (offset + 12 > b.Length) return null;
        try
        {
            int year = (short)ReadLe16(b, offset + 2);
            int month = b[offset + 4];
            int day = b[offset + 5];
            int hour = b[offset + 6];
            int minute = b[offset + 7];
            int second = b[offset + 8];

            if (year < 1980 || year > 2100 || month < 1 || month > 12 || day < 1 || day > 31) return null;
            if (hour > 23 || minute > 59 || second > 59) return null;

            return new DateTime(year, month, day, hour, minute, second);
        }
        catch { return null; }
    }

    internal static int ReadLe16(byte[] b, int o) => b[o] | (b[o + 1] << 8);

    internal static long ReadLe32(byte[] b, int o) =>
        (long)b[o] | ((long)b[o + 1] << 8) | ((long)b[o + 2] << 16) | ((long)b[o + 3] << 24);

    internal static ulong ReadLe64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
        return v;
    }
}
