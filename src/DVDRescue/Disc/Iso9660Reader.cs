using System.Text;
using DVDRescue.Model;

namespace DVDRescue.Disc;

/// <summary>
/// Lettore ISO 9660 minimale, usato come ripiego quando l'UDF non c'è o è illeggibile.
/// I DVD-Video masterizzati in modalità "video" hanno quasi sempre un ISO 9660 affiancato all'UDF.
/// </summary>
public static class Iso9660Reader
{
    public static List<DiscFileEntry> TryRead(ISectorSource src, Action<string> log = null)
    {
        log ??= _ => { };

        try
        {
            var sector = new byte[2048];

            long pvdLba = -1;
            for (long lba = 16; lba <= 32; lba++)
            {
                if (!src.TryReadSector(lba, sector, 0)) continue;
                if (sector[0] == 0xFF) break; // volume descriptor set terminator
                if (sector[1] == 'C' && sector[2] == 'D' && sector[3] == '0' && sector[4] == '0' && sector[5] == '1'
                    && sector[0] == 0x01)
                {
                    pvdLba = lba;
                    break;
                }
            }

            if (pvdLba < 0) { log("ISO 9660: nessun Primary Volume Descriptor"); return null; }

            src.TryReadSector(pvdLba, sector, 0);
            string volumeId = Encoding.ASCII.GetString(sector, 40, 32).Trim();

            long rootLba = ReadLe32(sector, 156 + 2);
            long rootLen = ReadLe32(sector, 156 + 10);

            log($"ISO 9660: volume \"{volumeId}\", radice a LBA {rootLba}");

            var files = new List<DiscFileEntry>();
            WalkDirectory(src, rootLba, rootLen, "", files, 0);
            log($"ISO 9660: {files.Count} file individuati");
            return files;
        }
        catch (Exception ex)
        {
            log($"ISO 9660: lettura interrotta ({ex.Message})");
            return null;
        }
    }

    private static void WalkDirectory(ISectorSource src, long lba, long length, string path,
                                      List<DiscFileEntry> files, int depth)
    {
        if (depth > 8 || files.Count > 5000 || length <= 0) return;

        int sectors = (int)Math.Min(256, (length + 2047) / 2048);
        var data = new byte[sectors * 2048];
        for (int i = 0; i < sectors; i++)
            src.TryReadSector(lba + i, data, i * 2048);

        int pos = 0;
        while (pos < data.Length)
        {
            int recLen = data[pos];
            if (recLen == 0)
            {
                // salta al settore successivo
                int next = ((pos / 2048) + 1) * 2048;
                if (next >= data.Length) break;
                pos = next;
                continue;
            }
            if (pos + recLen > data.Length) break;

            long extLba = ReadLe32(data, pos + 2);
            long extLen = ReadLe32(data, pos + 10);
            byte flags = data[pos + 25];
            int nameLen = data[pos + 32];

            if (nameLen > 0 && pos + 33 + nameLen <= data.Length)
            {
                string name = Encoding.ASCII.GetString(data, pos + 33, nameLen);
                bool special = nameLen == 1 && (data[pos + 33] == 0 || data[pos + 33] == 1);

                if (!special)
                {
                    int semi = name.IndexOf(';');
                    if (semi >= 0) name = name.Substring(0, semi);

                    string full = string.IsNullOrEmpty(path) ? name : path + "/" + name;

                    if ((flags & 0x02) != 0)
                    {
                        WalkDirectory(src, extLba, extLen, full, files, depth + 1);
                    }
                    else
                    {
                        files.Add(new DiscFileEntry
                        {
                            Path = full,
                            Length = extLen,
                            Modified = ReadRecordingDate(data, pos + 18),
                            Extents = { new SectorExtent(extLba, (extLen + 2047) / 2048) }
                        });
                    }
                }
            }

            pos += recLen;
        }
    }

    private static DateTime? ReadRecordingDate(byte[] b, int o)
    {
        try
        {
            int year = 1900 + b[o];
            int month = b[o + 1], day = b[o + 2], hour = b[o + 3], min = b[o + 4], sec = b[o + 5];
            if (year < 1980 || year > 2100 || month < 1 || month > 12 || day < 1 || day > 31) return null;
            if (hour > 23 || min > 59 || sec > 59) return null;
            return new DateTime(year, month, day, hour, min, sec);
        }
        catch { return null; }
    }

    private static long ReadLe32(byte[] b, int o) =>
        (long)b[o] | ((long)b[o + 1] << 8) | ((long)b[o + 2] << 16) | ((long)b[o + 3] << 24);
}
