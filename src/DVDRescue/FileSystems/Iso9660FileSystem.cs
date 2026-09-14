using System.Text;
using DVDRescue.Core;
using static DVDRescue.Core.ByteUtils;

namespace DVDRescue.FileSystems;

/// <summary>
/// Lettore ISO 9660 con estensioni Joliet (nomi Unicode) e Rock Ridge (nomi lunghi Unix).
/// Sui DVD video è quasi sempre presente accanto all'UDF ed è più semplice da leggere:
/// funziona da ripiego quando l'UDF è danneggiato.
/// </summary>
public sealed class Iso9660FileSystem : FileSystemBase
{
    private long _rootLba;
    private long _rootLength;
    private bool _joliet;
    private int _jolietLevel;

    public override string TypeName => _joliet ? "ISO 9660 + Joliet" : "ISO 9660";

    public Iso9660FileSystem(IBlockSource source) : base(source) { }

    public static bool Detect(IBlockSource source)
    {
        var buf = new byte[2048];
        for (long lba = 16; lba <= 20; lba++)
        {
            if (source.ReadBytes(lba * 2048, 2048, buf, 0) < 2048) return false;
            if (Ascii(buf, 1, 5) == "CD001") return true;
            if (buf[0] == 0xFF) return false;
        }
        return false;
    }

    public override void Scan(bool includeDeleted, CancellationToken ct)
    {
        var buf = new byte[2048];
        long primaryRoot = -1, primaryLen = 0;
        long jolietRoot = -1, jolietLen = 0;

        for (long lba = 16; lba <= 40; lba++)
        {
            if (Source.ReadBytes(lba * 2048, 2048, buf, 0) < 2048) break;
            if (Ascii(buf, 1, 5) != "CD001") break;

            byte type = buf[0];
            if (type == 0xFF) break;

            if (type == 1 && primaryRoot < 0)
            {
                VolumeLabel = Ascii(buf, 40, 32);
                primaryRoot = U32LE(buf, 156 + 2);
                primaryLen = U32LE(buf, 156 + 10);
            }
            else if (type == 2)
            {
                // Supplementary Volume Descriptor: Joliet se l'escape è %/@ %/C %/E
                string escape = Ascii(buf, 88, 3);
                if (escape.StartsWith("%/"))
                {
                    _jolietLevel = escape[2] switch { '@' => 1, 'C' => 2, 'E' => 3, _ => 0 };
                    if (_jolietLevel > 0)
                    {
                        jolietRoot = U32LE(buf, 156 + 2);
                        jolietLen = U32LE(buf, 156 + 10);
                        string label = Utf16BE(buf, 40, 32);
                        if (!string.IsNullOrWhiteSpace(label)) VolumeLabel = label;
                    }
                }
            }
        }

        if (jolietRoot > 0) { _rootLba = jolietRoot; _rootLength = jolietLen; _joliet = true; }
        else if (primaryRoot > 0) { _rootLba = primaryRoot; _rootLength = primaryLen; }
        else { Notes.Add("Nessun Primary Volume Descriptor ISO 9660."); return; }

        WalkDirectory(_rootLba, _rootLength, Root, 0, new HashSet<long>(), ct);
        Notes.Add($"ISO 9660 letto ({(_joliet ? "Joliet" : "nomi standard")}).");
    }

    private void WalkDirectory(long lba, long length, FsEntry parent, int depth,
                               HashSet<long> visited, CancellationToken ct)
    {
        if (depth > 16 || length <= 0 || !visited.Add(lba)) return;
        ct.ThrowIfCancellationRequested();

        int sectors = (int)Math.Min(512, (length + 2047) / 2048);
        var data = new byte[sectors * 2048];
        Source.ReadBytes(lba * 2048, data.Length, data, 0);

        int pos = 0;
        while (pos < data.Length)
        {
            int recordLength = data[pos];
            if (recordLength == 0)
            {
                int next = ((pos / 2048) + 1) * 2048;
                if (next >= data.Length) break;
                pos = next;
                continue;
            }

            if (pos + recordLength > data.Length || recordLength < 33) break;

            long extentLba = U32LE(data, pos + 2);
            long extentLength = U32LE(data, pos + 10);
            byte flags = data[pos + 25];
            int nameLength = data[pos + 32];

            if (nameLength > 0 && pos + 33 + nameLength <= data.Length)
            {
                bool special = nameLength == 1 && (data[pos + 33] == 0 || data[pos + 33] == 1);

                if (!special)
                {
                    string name = _joliet
                        ? Utf16BE(data, pos + 33, nameLength)
                        : Ascii(data, pos + 33, nameLength);

                    int semicolon = name.IndexOf(';');
                    if (semicolon >= 0) name = name[..semicolon];
                    if (name.EndsWith(".")) name = name[..^1];

                    // Rock Ridge: nome lungo nel campo di sistema dopo il nome
                    string rockRidge = ReadRockRidgeName(data, pos, recordLength, nameLength);
                    if (!string.IsNullOrEmpty(rockRidge)) name = rockRidge;

                    bool isDirectory = (flags & 0x02) != 0;

                    if (isDirectory)
                    {
                        var dir = new FsEntry { Name = name, IsDirectory = true };
                        AddChild(parent, dir);
                        WalkDirectory(extentLba, extentLength, dir, depth + 1, visited, ct);
                    }
                    else
                    {
                        var file = new FsEntry
                        {
                            Name = name,
                            Length = extentLength,
                            Modified = Iso9660Time(data, pos + 18)
                        };
                        file.Extents.Add(new FsExtent(extentLba * 2048L, extentLength));
                        AddChild(parent, file);
                    }
                }
            }

            pos += recordLength;
        }
    }

    /// <summary>Voce NM (Alternate Name) delle estensioni Rock Ridge.</summary>
    private static string ReadRockRidgeName(byte[] data, int recordStart, int recordLength, int nameLength)
    {
        int sysStart = recordStart + 33 + nameLength;
        if ((nameLength & 1) == 0) sysStart++;   // padding a numero pari
        int sysEnd = recordStart + recordLength;
        if (sysStart >= sysEnd) return null;

        var sb = new StringBuilder();
        int p = sysStart;

        while (p + 4 <= sysEnd)
        {
            char a = (char)data[p], b = (char)data[p + 1];
            int len = data[p + 2];
            if (len < 3 || p + len > sysEnd) break;

            if (a == 'N' && b == 'M' && len > 5)
                sb.Append(Encoding.UTF8.GetString(data, p + 5, len - 5));

            p += len;
        }

        string name = sb.ToString().Trim('\0');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
