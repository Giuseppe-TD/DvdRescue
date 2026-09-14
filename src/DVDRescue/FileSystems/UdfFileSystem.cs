using DVDRescue.Core;
using static DVDRescue.Core.ByteUtils;

namespace DVDRescue.FileSystems;

/// <summary>
/// Lettore UDF (ECMA-167 / UDF 1.02 → 2.60) in sola lettura.
///
/// Copre i tre schemi che contano sui supporti video:
///  - partizione fisica semplice (DVD-Video, DVD-VR su disco finalizzato);
///  - partizione virtuale con VAT (DVD-R/CD-R scritti a pacchetti, tipico dei dischi
///    "trascina e rilascia" e di certe videocamere);
///  - partizione con sparing table (DVD-RAM/DVD+RW, che rimappano i settori difettosi)
///    e partizione di metadati (UDF 2.50: Blu-ray e AVCHD).
///
/// Sui dischi delle videocamere l'UDF viene aggiornato durante la registrazione,
/// quindi spesso è leggibile anche se il disco non è mai stato chiuso.
/// </summary>
public sealed class UdfFileSystem : FileSystemBase
{
    // identificatori di descrittore (ECMA-167 parte 3/4)
    private const int TagPrimaryVolume = 1;
    private const int TagAnchor = 2;
    private const int TagPartition = 5;
    private const int TagLogicalVolume = 6;
    private const int TagTerminating = 8;
    private const int TagLogicalVolumeIntegrity = 9;
    private const int TagFileSet = 256;
    private const int TagFileIdentifier = 257;
    private const int TagAllocationExtent = 258;
    private const int TagFileEntry = 261;
    private const int TagExtendedFileEntry = 266;

    private const int FileTypeDirectory = 4;
    private const int FileTypeVat200 = 248;

    private sealed class UdfPartition
    {
        public int Reference;
        public int Number;
        public long StartLba;
        public long Blocks;
        public string Kind = "fisica";

        /// <summary>Rimappatura logico→fisico (VAT, sparing, metadati). Null = diretta.</summary>
        public Func<long, long> Map;
    }

    private readonly List<UdfPartition> _partitions = new();
    private int _blockSize = 2048;
    private long _totalBlocks;
    private string _revision = "";

    public override string TypeName => string.IsNullOrEmpty(_revision) ? "UDF" : "UDF " + _revision;

    public UdfFileSystem(IBlockSource source) : base(source)
    {
        _totalBlocks = source.Length / 2048;
    }

    public static bool Detect(IBlockSource source)
    {
        // "BEA01" nella Volume Recognition Sequence (settore 16 in avanti) oppure un anchor valido
        var buf = new byte[2048];
        for (long lba = 16; lba <= 24; lba++)
        {
            if (source.ReadBytes(lba * 2048, 2048, buf, 0) < 2048) break;
            string id = Ascii(buf, 1, 5);
            if (id == "BEA01" || id == "NSR02" || id == "NSR03") return true;
            if (id == "TEA01") break;
        }

        long total = source.Length / 2048;
        foreach (long lba in AnchorCandidates(total))
        {
            if (source.ReadBytes(lba * 2048, 2048, buf, 0) < 2048) continue;
            if (U16LE(buf, 0) == TagAnchor && TagChecksumOk(buf)) return true;
        }
        return false;
    }

    private static IEnumerable<long> AnchorCandidates(long totalBlocks)
    {
        yield return 256;
        if (totalBlocks > 1) yield return totalBlocks - 1;
        if (totalBlocks > 256) yield return totalBlocks - 256;
        yield return 512;
    }

    public override void Scan(bool includeDeleted, CancellationToken ct)
    {
        long vdsStart = -1, vdsLength = 0;
        var sector = new byte[2048];

        foreach (long lba in AnchorCandidates(_totalBlocks))
        {
            if (lba < 0) continue;
            if (Source.ReadBytes(lba * 2048, 2048, sector, 0) < 2048) continue;
            if (U16LE(sector, 0) != TagAnchor || !TagChecksumOk(sector)) continue;

            vdsLength = U32LE(sector, 16);
            vdsStart = U32LE(sector, 20);
            Notes.Add($"Anchor UDF trovato al settore {lba}.");
            break;
        }

        if (vdsStart <= 0)
        {
            Notes.Add("Nessun anchor UDF valido: il disco non ha un filesystem UDF leggibile.");
            return;
        }

        ReadVolumeDescriptors(vdsStart, vdsLength, ct);

        if (_partitions.Count == 0)
        {
            Notes.Add("Nessuna partizione UDF utilizzabile.");
            return;
        }

        if (_fileSetPartition < 0)
        {
            Notes.Add("Riferimento al File Set assente: uso la prima partizione.");
            _fileSetPartition = 0;
        }

        long fsdLba = Resolve(_fileSetPartition, _fileSetBlock);
        if (fsdLba < 0 || Source.ReadBytes(fsdLba * 2048, 2048, sector, 0) < 2048 ||
            U16LE(sector, 0) != TagFileSet)
        {
            // certi dischi hanno il File Set qualche blocco più avanti
            bool found = false;
            for (int delta = 1; delta <= 8 && !found; delta++)
            {
                long probe = Resolve(_fileSetPartition, _fileSetBlock + delta);
                if (probe < 0) continue;
                if (Source.ReadBytes(probe * 2048, 2048, sector, 0) < 2048) continue;
                if (U16LE(sector, 0) == TagFileSet) { found = true; }
            }
            if (!found)
            {
                Notes.Add("File Set Descriptor non leggibile.");
                return;
            }
        }

        long rootBlock = U32LE(sector, 400 + 4);
        int rootPart = U16LE(sector, 400 + 8);

        var visited = new HashSet<long>();
        WalkDirectory(rootPart, rootBlock, Root, includeDeleted, 0, visited, ct);

        Notes.Add($"Struttura UDF letta: {CountEntries(Root)} voci.");
    }

    private int _fileSetPartition = -1;
    private long _fileSetBlock;

    private static int CountEntries(FsEntry root)
    {
        int n = 0;
        var stack = new Stack<FsEntry>();
        foreach (var c in root.Children) stack.Push(c);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            n++;
            foreach (var c in e.Children) stack.Push(c);
        }
        return n;
    }

    // ------------------------------------------------------ descrittori volume

    private void ReadVolumeDescriptors(long vdsStart, long vdsLength, CancellationToken ct)
    {
        var sector = new byte[2048];
        var partitionDescriptors = new List<(int Number, long Start, long Length)>();
        byte[] lvd = null;

        int count = (int)Math.Min(128, Math.Max(1, (vdsLength + 2047) / 2048));

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            long lba = vdsStart + i;
            if (Source.ReadBytes(lba * 2048, 2048, sector, 0) < 2048) continue;

            int tag = U16LE(sector, 0);
            if (tag == 0) continue;
            if (tag == TagTerminating) break;
            if (!TagChecksumOk(sector)) continue;

            switch (tag)
            {
                case TagPrimaryVolume:
                    VolumeLabel = ReadDString(sector, 24, 32);
                    break;

                case TagPartition:
                    partitionDescriptors.Add((U16LE(sector, 22), U32LE(sector, 188), U32LE(sector, 192)));
                    break;

                case TagLogicalVolume:
                    lvd = (byte[])sector.Clone();
                    break;
            }
        }

        if (lvd == null)
        {
            Notes.Add("Logical Volume Descriptor mancante.");
            // ripiego: usa direttamente i Partition Descriptor trovati
            foreach (var pd in partitionDescriptors)
                _partitions.Add(new UdfPartition { Reference = _partitions.Count, Number = pd.Number, StartLba = pd.Start, Blocks = pd.Length });
            if (_partitions.Count > 0) { _fileSetPartition = 0; _fileSetBlock = 0; }
            return;
        }

        _blockSize = (int)U32LE(lvd, 212);
        if (_blockSize <= 0 || _blockSize > 8192) _blockSize = 2048;

        string domain = Ascii(lvd, 216 + 1, 22);
        if (domain.Contains("UDF"))
        {
            int rev = U16LE(lvd, 216 + 24);
            if (rev > 0) _revision = $"{(rev >> 8):X}.{(rev & 0xFF):X2}";
        }

        _fileSetBlock = U32LE(lvd, 248 + 4);
        _fileSetPartition = U16LE(lvd, 248 + 8);

        int mapCount = (int)U32LE(lvd, 268);
        int offset = 440;

        for (int m = 0; m < mapCount && offset + 2 <= lvd.Length; m++)
        {
            int type = lvd[offset];
            int len = lvd[offset + 1];
            if (len <= 0 || offset + len > lvd.Length) break;

            if (type == 1)
            {
                int number = U16LE(lvd, offset + 4);
                var pd = partitionDescriptors.FirstOrDefault(p => p.Number == number);
                _partitions.Add(new UdfPartition
                {
                    Reference = m,
                    Number = number,
                    StartLba = pd.Start,
                    Blocks = pd.Length
                });
            }
            else if (type == 2)
            {
                string id = Ascii(lvd, offset + 4 + 1, 22);
                int number = U16LE(lvd, offset + 38);
                var pd = partitionDescriptors.FirstOrDefault(p => p.Number == number);

                var part = new UdfPartition
                {
                    Reference = m,
                    Number = number,
                    StartLba = pd.Start,
                    Blocks = pd.Length
                };

                if (id.Contains("Virtual Partition"))
                {
                    part.Kind = "virtuale (VAT)";
                    BuildVat(part);
                }
                else if (id.Contains("Sparable Partition"))
                {
                    part.Kind = "con sparing table";
                    BuildSparing(part, lvd, offset);
                }
                else if (id.Contains("Metadata Partition"))
                {
                    part.Kind = "metadati";
                    BuildMetadata(part, lvd, offset);
                }
                else
                {
                    part.Kind = $"tipo 2 ({id})";
                    Notes.Add($"Mappa di partizione non riconosciuta: {id}");
                }

                _partitions.Add(part);
            }
            else
            {
                Notes.Add($"Mappa di partizione di tipo {type} ignorata.");
                _partitions.Add(new UdfPartition { Reference = m, Number = -1 });
            }

            offset += len;
        }

        foreach (var p in _partitions)
            if (p.Blocks > 0)
                Notes.Add($"Partizione {p.Number} ({p.Kind}): settore {p.StartLba}, {p.Blocks} blocchi.");
    }

    // --------------------------------------------------------------------- VAT

    /// <summary>
    /// Virtual Allocation Table: sui supporti scritti a pacchetti i blocchi logici
    /// sono rimappati da una tabella che sta nell'ultimo settore registrato.
    /// </summary>
    private void BuildVat(UdfPartition part)
    {
        long lastWritten = _totalBlocks - 1;
        var sector = new byte[2048];
        long vatIcb = -1;

        // il VAT ICB è l'ultimo settore scritto; su dischi troncati si cerca all'indietro
        for (long lba = lastWritten; lba > lastWritten - 512 && lba > 0; lba--)
        {
            if (Source.ReadBytes(lba * 2048, 2048, sector, 0) < 2048) continue;
            int tag = U16LE(sector, 0);
            if ((tag == TagFileEntry || tag == TagExtendedFileEntry) && TagChecksumOk(sector))
            {
                int fileType = sector[16 + 11];
                if (fileType == FileTypeVat200 || fileType == 0) { vatIcb = lba; break; }
            }
        }

        if (vatIcb < 0)
        {
            Notes.Add("Partizione virtuale dichiarata ma VAT non trovata: i blocchi logici non sono rimappabili.");
            return;
        }

        var entry = ReadFileEntryAt(vatIcb);
        if (entry == null) { Notes.Add("VAT illeggibile."); return; }

        byte[] data = ReadEntryData(entry, 8 * 1024 * 1024);
        if (data == null || data.Length < 4) { Notes.Add("Contenuto della VAT vuoto."); return; }

        int start = 0, end = data.Length;

        if (entry.FileType == FileTypeVat200)
        {
            int headerLength = U16LE(data, 0);
            if (headerLength >= 152 && headerLength < data.Length) start = headerLength;
            else { Notes.Add("Intestazione VAT 2.00 incoerente: provo a leggerla come UDF 1.50."); }
        }

        if (start == 0 && end > 36) end -= 36;   // UDF 1.50: coda di lunghezza fissa

        int n = (end - start) / 4;
        if (n <= 0) { Notes.Add("VAT priva di voci."); return; }

        var table = new uint[n];
        int valid = 0;
        for (int i = 0; i < n; i++)
        {
            table[i] = U32LE(data, start + i * 4);
            if (table[i] == 0xFFFFFFFF || table[i] < part.Blocks + 1) valid++;
        }

        if (valid < n * 0.9)
        {
            Notes.Add("La VAT non supera i controlli di coerenza: la ignoro e uso l'indirizzamento diretto.");
            return;
        }

        long partStart = part.StartLba;
        part.Map = logical =>
        {
            if (logical < 0 || logical >= table.Length) return -1;
            uint mapped = table[logical];
            return mapped == 0xFFFFFFFF ? -1 : partStart + mapped;
        };

        Notes.Add($"VAT attiva con {n} voci (disco scritto a pacchetti).");
    }

    // ----------------------------------------------------------------- sparing

    private void BuildSparing(UdfPartition part, byte[] lvd, int mapOffset)
    {
        int tableCount = lvd[mapOffset + 42];
        var remap = new Dictionary<long, long>();

        for (int t = 0; t < tableCount && t < 4; t++)
        {
            long loc = U32LE(lvd, mapOffset + 48 + t * 4);
            if (loc <= 0) continue;

            var head = new byte[2048];
            if (Source.ReadBytes(loc * 2048, 2048, head, 0) < 2048) continue;

            string id = Ascii(head, 16 + 1, 22);
            if (!id.Contains("Sparing Table")) continue;

            int entries = U16LE(head, 48);
            for (int i = 0; i < entries; i++)
            {
                int off = 56 + i * 8;
                if (off + 8 > head.Length) break;
                long original = U32LE(head, off);
                long mapped = U32LE(head, off + 4);
                if (original == 0xFFFFFFFF || original == 0xFFFFFFF0) continue;
                remap[original] = mapped;
            }
        }

        if (remap.Count == 0) return;

        long partStart = part.StartLba;
        part.Map = logical =>
        {
            long physical = partStart + logical;
            return remap.TryGetValue(physical, out long alt) ? alt : physical;
        };

        Notes.Add($"Sparing table attiva: {remap.Count} settori rimappati.");
    }

    // ---------------------------------------------------------------- metadati

    /// <summary>
    /// UDF 2.50 (Blu-ray, AVCHD): le strutture di directory stanno dentro un file
    /// di metadati, mentre i dati veri restano nella partizione fisica.
    /// </summary>
    private void BuildMetadata(UdfPartition part, byte[] lvd, int mapOffset)
    {
        long metadataFile = U32LE(lvd, mapOffset + 40);
        long mirrorFile = U32LE(lvd, mapOffset + 44);

        foreach (long candidate in new[] { metadataFile, mirrorFile })
        {
            if (candidate <= 0) continue;

            long lba = part.StartLba + candidate;
            var entry = ReadFileEntryAt(lba);
            if (entry == null || entry.Extents.Count == 0) continue;

            // spazio logico del file di metadati = concatenazione dei suoi extent
            var extents = entry.Extents.ToArray();
            var starts = new long[extents.Length];
            long acc = 0;
            for (int i = 0; i < extents.Length; i++) { starts[i] = acc; acc += extents[i].Blocks; }
            long totalBlocks = acc;

            part.Map = logical =>
            {
                if (logical < 0 || logical >= totalBlocks) return -1;
                for (int i = 0; i < extents.Length; i++)
                    if (logical >= starts[i] && logical < starts[i] + extents[i].Blocks)
                        return extents[i].Lba + (logical - starts[i]);
                return -1;
            };

            Notes.Add($"Partizione di metadati UDF 2.50 attiva ({totalBlocks} blocchi" +
                      (candidate == mirrorFile ? ", copia di riserva" : "") + ").");
            return;
        }

        Notes.Add("Partizione di metadati dichiarata ma il file dei metadati non è leggibile.");
    }

    // ------------------------------------------------------------- risoluzione

    private long Resolve(int partitionRef, long logicalBlock)
    {
        if (partitionRef < 0 || partitionRef >= _partitions.Count) return -1;
        var p = _partitions[partitionRef];
        if (p.Map != null) return p.Map(logicalBlock);
        if (p.Number < 0) return -1;
        return p.StartLba + logicalBlock;
    }

    // -------------------------------------------------------------- directory

    private void WalkDirectory(int partRef, long block, FsEntry parent, bool includeDeleted,
                               int depth, HashSet<long> visited, CancellationToken ct)
    {
        if (depth > 16 || parent.Children.Count > 20000) return;
        ct.ThrowIfCancellationRequested();

        long key = ((long)partRef << 48) | block;
        if (!visited.Add(key)) return;

        long lba = Resolve(partRef, block);
        if (lba < 0) return;

        var entry = ReadFileEntryAt(lba);
        if (entry == null || entry.FileType != FileTypeDirectory) return;

        byte[] data = entry.InlineData ?? ReadEntryData(entry, 32 * 1024 * 1024);
        if (data == null) return;

        int pos = 0;
        while (pos + 38 <= data.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (U16LE(data, pos) != TagFileIdentifier)
            {
                // salta al prossimo confine di blocco: capita sui dischi danneggiati
                int next = ((pos / _blockSize) + 1) * _blockSize;
                if (next <= pos || next >= data.Length) break;
                pos = next;
                continue;
            }

            byte characteristics = data[pos + 18];
            int nameLength = data[pos + 19];
            long childBlock = U32LE(data, pos + 20 + 4);
            int childPart = U16LE(data, pos + 20 + 8);
            int impUse = U16LE(data, pos + 36);

            int nameOffset = pos + 38 + impUse;
            int total = (38 + impUse + nameLength + 3) & ~3;

            bool isParent = (characteristics & 0x08) != 0;
            bool isDeleted = (characteristics & 0x04) != 0;
            bool isDirectory = (characteristics & 0x02) != 0;
            bool isHidden = (characteristics & 0x01) != 0;

            if (!isParent && nameLength > 0 && nameOffset + nameLength <= data.Length &&
                (!isDeleted || includeDeleted))
            {
                string name = DecodeDString(data, nameOffset, nameLength);
                if (!string.IsNullOrEmpty(name))
                {
                    if (isDirectory)
                    {
                        var dir = new FsEntry { Name = name, IsDirectory = true, IsDeleted = isDeleted };
                        AddChild(parent, dir);
                        if (!isDeleted)
                            WalkDirectory(childPart, childBlock, dir, includeDeleted, depth + 1, visited, ct);
                    }
                    else
                    {
                        long childLba = Resolve(childPart, childBlock);
                        var fe = childLba >= 0 ? ReadFileEntryAt(childLba) : null;

                        var file = new FsEntry
                        {
                            Name = name,
                            IsDirectory = false,
                            IsDeleted = isDeleted,
                            IsUncertain = isDeleted,
                            Length = fe?.InformationLength ?? 0,
                            Modified = fe?.Modified,
                            Created = fe?.Created,
                            InlineData = fe?.InlineData
                        };

                        if (fe != null)
                            foreach (var ex in fe.Extents)
                                file.Extents.Add(new FsExtent(ex.Lba * 2048L, ex.Blocks * 2048L));

                        AddChild(parent, file);
                    }
                }
            }

            if (total <= 0) break;
            pos += total;
        }
    }

    // ------------------------------------------------------------ file entry

    private sealed class UdfFileEntry
    {
        public int FileType;
        public long InformationLength;
        public DateTime? Modified;
        public DateTime? Created;
        public List<(long Lba, long Blocks)> Extents = new();
        public byte[] InlineData;
    }

    private UdfFileEntry ReadFileEntryAt(long lba)
    {
        if (lba < 0) return null;

        var buf = new byte[2048];
        if (Source.ReadBytes(lba * 2048, 2048, buf, 0) < 2048) return null;

        int tag = U16LE(buf, 0);
        if (tag != TagFileEntry && tag != TagExtendedFileEntry) return null;

        bool extended = tag == TagExtendedFileEntry;
        var info = new UdfFileEntry
        {
            FileType = buf[16 + 11]
        };

        int icbFlags = U16LE(buf, 16 + 18);
        int adType = icbFlags & 0x07;

        int lEaOffset, lAdOffset, dataOffset;
        if (extended)
        {
            info.InformationLength = (long)U64LE(buf, 56);
            info.Modified = UdfTime(buf, 92);
            info.Created = UdfTime(buf, 104);
            lEaOffset = 208; lAdOffset = 212; dataOffset = 216;
        }
        else
        {
            info.InformationLength = (long)U64LE(buf, 56);
            info.Modified = UdfTime(buf, 84);
            lEaOffset = 168; lAdOffset = 172; dataOffset = 176;
        }

        long lEa = U32LE(buf, lEaOffset);
        long lAd = U32LE(buf, lAdOffset);
        int adStart = (int)(dataOffset + lEa);

        if (adStart < 0 || adStart >= buf.Length) return info;

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

        ReadAllocationDescriptors(buf, adStart, (int)lAd, adType, info, 0);
        return info;
    }

    /// <summary>Legge gli allocation descriptor, seguendo le continuazioni (tipo 3).</summary>
    private void ReadAllocationDescriptors(byte[] buf, int start, int length, int adType,
                                           UdfFileEntry info, int depth)
    {
        if (depth > 8 || length <= 0) return;

        int adSize = adType == 0 ? 8 : (adType == 1 ? 16 : 20);
        int count = length / adSize;

        for (int i = 0; i < count; i++)
        {
            int off = start + i * adSize;
            if (off + adSize > buf.Length) return;

            uint rawLength = U32LE(buf, off);
            int extentType = (int)(rawLength >> 30);
            long byteLength = rawLength & 0x3FFFFFFF;
            long block = U32LE(buf, off + 4);
            int partRef = adType == 1 ? U16LE(buf, off + 8) : _fileSetPartition;

            if (extentType == 3)
            {
                // continuazione: il blocco indicato contiene altri descrittori
                long contLba = Resolve(partRef < 0 ? _fileSetPartition : partRef, block);
                if (contLba < 0) return;

                var cont = new byte[2048];
                if (Source.ReadBytes(contLba * 2048, 2048, cont, 0) < 2048) return;
                if (U16LE(cont, 0) != TagAllocationExtent) return;

                int contLen = (int)U32LE(cont, 20);
                ReadAllocationDescriptors(cont, 24, Math.Min(contLen, cont.Length - 24), adType, info, depth + 1);
                return;
            }

            if (extentType == 2 || byteLength == 0) continue;   // non registrato

            long blocks = (byteLength + 2047) / 2048;
            long lba = Resolve(partRef < 0 ? _fileSetPartition : partRef, block);
            if (lba < 0) continue;

            info.Extents.Add((lba, blocks));
            if (info.Extents.Count > 100000) return;
        }
    }

    private byte[] ReadEntryData(UdfFileEntry entry, int maxBytes)
    {
        if (entry.InlineData != null) return entry.InlineData;
        if (entry.Extents.Count == 0) return null;

        long total = 0;
        foreach (var e in entry.Extents) total += e.Blocks * 2048L;
        if (entry.InformationLength > 0 && entry.InformationLength < total) total = entry.InformationLength;
        if (total > maxBytes) total = maxBytes;
        if (total <= 0) return null;

        var data = new byte[total];
        long written = 0;

        foreach (var e in entry.Extents)
        {
            if (written >= total) break;
            int chunk = (int)Math.Min(e.Blocks * 2048L, total - written);
            Source.ReadBytes(e.Lba * 2048L, chunk, data, (int)written);
            written += chunk;
        }

        return data;
    }

    // ------------------------------------------------------------------ utili

    private static bool TagChecksumOk(byte[] b)
    {
        if (b.Length < 16) return false;
        int sum = 0;
        for (int i = 0; i < 16; i++) { if (i == 4) continue; sum += b[i]; }
        return (byte)sum == b[4];
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

        byte compression = b[offset];
        if (compression == 8 || compression == 254)
            return System.Text.Encoding.Latin1.GetString(b, offset + 1, length - 1).TrimEnd('\0', ' ');

        if (compression == 16 || compression == 255)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = offset + 1; i + 1 < offset + length; i += 2)
                sb.Append((char)((b[i] << 8) | b[i + 1]));
            return sb.ToString().TrimEnd('\0', ' ');
        }

        return System.Text.Encoding.Latin1.GetString(b, offset, length).TrimEnd('\0', ' ');
    }
}
