using System.Text;
using DVDRescue.Core;

namespace DVDRescue.FileSystems;

/// <summary>
/// Driver NTFS in sola lettura orientato al recupero dati.
///
/// La sorgente è il VOLUME: offset 0 = settore di boot.
/// L'albero viene ricostruito scandendo tutta la MFT e seguendo i riferimenti al genitore
/// contenuti in $FILE_NAME: su un volume danneggiato è molto più robusto che seguire gli
/// indici delle directory, che possono essere illeggibili in blocco. Gli indici vengono
/// comunque letti per recuperare i nomi dei figli il cui record MFT non è più utilizzabile.
///
/// Limiti noti: EFS (file cifrati) non viene decifrato, gli stream alternativi (ADS)
/// non compaiono nell'albero, i reparse point non vengono seguiti.
/// </summary>
public sealed class NtfsFileSystem : FileSystemBase
{
    // ---- tipi di attributo ----
    private const uint AT_STANDARD_INFORMATION = 0x10;
    private const uint AT_ATTRIBUTE_LIST       = 0x20;
    private const uint AT_FILE_NAME            = 0x30;
    private const uint AT_VOLUME_NAME          = 0x60;
    private const uint AT_DATA                 = 0x80;
    private const uint AT_INDEX_ROOT           = 0x90;
    private const uint AT_INDEX_ALLOCATION     = 0xA0;
    private const uint AT_BITMAP               = 0xB0;
    private const uint AT_REPARSE_POINT        = 0xC0;
    private const uint AT_END                  = 0xFFFFFFFF;

    // flag dell'intestazione di attributo
    private const ushort ATTR_COMPRESSION_MASK = 0x00FF;
    private const ushort ATTR_ENCRYPTED        = 0x4000;
    private const ushort ATTR_SPARSE           = 0x8000;

    // flag del record MFT (offset 0x16)
    private const ushort MFT_IN_USE    = 0x0001;
    private const ushort MFT_DIRECTORY = 0x0002;

    private const int FIRST_USER_MFT = 16;   // 0..15 sono i metafile
    private const int MFT_ROOT_DIR   = 5;
    private const int MFT_VOLUME     = 3;
    private const int LZNT1_CHUNK    = 4096;

    private const string ORPHAN_DIR = "[orfani]";

    /// <summary>Nessun volume reale arriva a 2^48 cluster: oltre è per forza corruzione.</summary>
    private const long MAX_RUN_CLUSTERS = 1L << 48;

    // ---- parametri del volume ----
    private int  _bytesPerSector;
    private int  _sectorsPerCluster;
    private int  _clusterSize;
    private int  _mftRecordSize;
    private int  _indexBlockSize;
    private long _mftLcn;
    private long _mftMirrLcn;
    private long _totalSectors;
    private bool _mounted;

    private Runlist _mftRuns;
    private long    _mftRecordCount;

    // finestra di lettura sulla MFT, per non fare una lettura da disco per ogni record
    private byte[] _mftWindow;
    private long   _mftWindowStart = -1;
    private int    _mftWindowLen;

    private readonly HashSet<string> _noteSeen = new(StringComparer.Ordinal);
    private int _adsCount, _reparseCount, _badRecordCount, _hardLinkCount;
    private int _mountNoteCount = -1;

    public override string TypeName => "NTFS";

    public override long TotalBytes
    {
        get
        {
            long fromBoot = _totalSectors * (long)_bytesPerSector;
            return fromBoot > 0 ? fromBoot : Source.Length;
        }
    }

    /// <summary>Byte per cluster del volume (diagnostica).</summary>
    public int ClusterSize => _clusterSize;

    /// <summary>Dimensione in byte di un record MFT (diagnostica).</summary>
    public int MftRecordSize => _mftRecordSize;

    /// <summary>Numero di record MFT esaminabili (diagnostica).</summary>
    public long MftRecordCount => _mftRecordCount;

    public NtfsFileSystem(IBlockSource source) : base(source)
    {
        Mount();
    }

    // =====================================================================
    //  Rilevamento
    // =====================================================================

    /// <summary>Vero se la sorgente comincia con un settore di boot NTFS plausibile.</summary>
    public static bool Detect(IBlockSource source)
    {
        try
        {
            if (source == null) return false;

            var b = new byte[512];
            if (source.ReadBytes(0, 512, b, 0) < 512) return false;

            if (Encoding.ASCII.GetString(b, 3, 8) != "NTFS    ") return false;

            int bps = ByteUtils.U16LE(b, 0x0B);
            if (bps < 256 || bps > 8192 || (bps & (bps - 1)) != 0) return false;

            int spc = DecodeSectorsPerCluster(b[0x0D]);
            if (spc <= 0 || spc > 65536) return false;

            long cluster = (long)bps * spc;
            if (cluster <= 0 || cluster > 2 * 1024 * 1024) return false;

            ulong totalSectors = ByteUtils.U64LE(b, 0x28);
            if (totalSectors == 0 || totalSectors > (1UL << 52)) return false;

            long mftLcn = (long)ByteUtils.U64LE(b, 0x30);
            if (mftLcn <= 0) return false;

            // il record MFT deve avere una dimensione sensata
            int recSize = DecodeClusterOrByteSize(unchecked((sbyte)b[0x40]), (int)cluster);
            if (recSize < 256 || recSize > 65536 || (recSize & (recSize - 1)) != 0) return false;

            return true;
        }
        catch { return false; }
    }

    /// <summary>Settori per cluster: oltre 0x80 il campo è un esponente negativo (2^|n|).</summary>
    private static int DecodeSectorsPerCluster(byte v)
    {
        if (v == 0) return 0;
        if (v <= 0x80) return (v & (v - 1)) == 0 ? v : 0;
        int exp = 256 - v;
        return exp is > 0 and < 24 ? 1 << exp : 0;
    }

    /// <summary>Campi "cluster per record": se negativi valgono 2^|n| byte.</summary>
    private static int DecodeClusterOrByteSize(sbyte v, int clusterSize)
    {
        if (v < 0)
        {
            int exp = -v;
            return exp is > 0 and < 31 ? 1 << exp : 0;
        }
        return v * clusterSize;
    }

    // =====================================================================
    //  Montaggio: boot sector + runlist della MFT
    // =====================================================================

    private void Mount()
    {
        var boot = Read(0, 512);

        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            AddNote("Identificativo OEM non NTFS nel settore di boot: procedo comunque.");

        _bytesPerSector = ByteUtils.U16LE(boot, 0x0B);
        if (_bytesPerSector < 256 || _bytesPerSector > 8192 ||
            (_bytesPerSector & (_bytesPerSector - 1)) != 0)
        {
            AddNote($"Byte per settore anomali ({_bytesPerSector}): assumo 512.");
            _bytesPerSector = 512;
        }

        _sectorsPerCluster = DecodeSectorsPerCluster(boot[0x0D]);
        if (_sectorsPerCluster <= 0)
        {
            AddNote($"Settori per cluster anomali (0x{boot[0x0D]:X2}): assumo 8.");
            _sectorsPerCluster = 8;
        }

        _clusterSize = _bytesPerSector * _sectorsPerCluster;
        _totalSectors = (long)ByteUtils.U64LE(boot, 0x28);
        _mftLcn = (long)ByteUtils.U64LE(boot, 0x30);
        _mftMirrLcn = (long)ByteUtils.U64LE(boot, 0x38);

        _mftRecordSize = DecodeClusterOrByteSize(unchecked((sbyte)boot[0x40]), _clusterSize);
        if (_mftRecordSize < 256 || _mftRecordSize > 65536)
        {
            AddNote($"Dimensione record MFT anomala ({_mftRecordSize}): assumo 1024 byte.");
            _mftRecordSize = 1024;
        }

        _indexBlockSize = DecodeClusterOrByteSize(unchecked((sbyte)boot[0x44]), _clusterSize);
        if (_indexBlockSize < 256 || _indexBlockSize > 1 << 20)
        {
            _indexBlockSize = Math.Max(_clusterSize, 4096);
            AddNote($"Dimensione blocco indice anomala: assumo {_indexBlockSize} byte.");
        }

        if (_mftLcn <= 0)
        {
            AddNote("LCN della $MFT non valido nel settore di boot.");
            if (_mftMirrLcn > 0) { _mftLcn = _mftMirrLcn; AddNote("Uso la posizione di $MFTMirr al posto di $MFT."); }
            else return;
        }

        LoadMftRunlist();
        _mounted = _mftRuns != null && _mftRuns.Runs.Count > 0;

        if (_mounted)
            ReadVolumeName();
    }

    /// <summary>Legge il record 0 ($MFT) e ne ricava la runlist completa.</summary>
    private void LoadMftRunlist()
    {
        byte[] rec = ReadRecordAt(_mftLcn * (long)_clusterSize, 0);

        if (rec == null && _mftMirrLcn > 0)
        {
            AddNote("Record 0 della $MFT illeggibile o corrotto: provo con $MFTMirr.");
            rec = ReadRecordAt(_mftMirrLcn * (long)_clusterSize, 0);
        }

        if (rec == null)
        {
            AddNote("Impossibile leggere il record 0 della $MFT: il volume non è esplorabile.");
            return;
        }

        var attrs = ParseAttributes(rec, "$MFT");
        var ds = BuildStream(attrs, AT_DATA, "");
        if (ds == null || ds.Runs == null || ds.Runs.Runs.Count == 0)
        {
            AddNote("Il record 0 non contiene un attributo $DATA non residente valido.");
            return;
        }

        _mftRuns = ds.Runs;

        // MFT frammentata al punto da avere una $ATTRIBUTE_LIST: le altre parti della
        // runlist stanno in record di estensione, raggiungibili con la runlist appena letta.
        var alist = attrs.Find(a => a.Type == AT_ATTRIBUTE_LIST);
        if (alist != null)
        {
            AddNote("La $MFT usa una $ATTRIBUTE_LIST: la runlist viene completata dai record di estensione.");
            var extra = LoadFile(0);
            if (extra != null)
            {
                var full = BuildStream(extra.Attrs, AT_DATA, "");
                if (full?.Runs != null && full.Runs.Runs.Count >= _mftRuns.Runs.Count)
                {
                    _mftRuns = full.Runs;
                    ds = full;
                }
            }
        }

        long size = ds.DataSize > 0 ? ds.DataSize : _mftRuns.TotalClusters * (long)_clusterSize;
        _mftRecordCount = size / _mftRecordSize;

        long maxByRuns = _mftRuns.TotalClusters * (long)_clusterSize / _mftRecordSize;
        if (_mftRecordCount > maxByRuns && maxByRuns > 0)
        {
            AddNote($"La $MFT dichiara {_mftRecordCount} record ma ne sono mappati {maxByRuns}: la coda non è leggibile.");
            _mftRecordCount = maxByRuns;
        }
        if (_mftRecordCount <= 0)
            AddNote("La $MFT risulta vuota.");
    }

    private void ReadVolumeName()
    {
        try
        {
            var f = LoadFile(MFT_VOLUME);
            if (f == null) { AddNote("Record $Volume illeggibile: etichetta non disponibile."); return; }

            var a = f.Attrs.Find(x => x.Type == AT_VOLUME_NAME);
            if (a == null) return;

            byte[] v = a.NonResident ? null : ResidentValue(a);
            if (v != null && v.Length >= 2)
                VolumeLabel = Encoding.Unicode.GetString(v, 0, v.Length & ~1).TrimEnd('\0');
        }
        catch { AddNote("Lettura dell'etichetta di volume fallita."); }
    }

    // =====================================================================
    //  Lettura dei record MFT (con fixup)
    // =====================================================================

    /// <summary>Legge un record a un offset fisico assoluto e vi applica il fixup.</summary>
    private byte[] ReadRecordAt(long byteOffset, long expectedNumber)
    {
        var buf = new byte[_mftRecordSize];
        if (Source.ReadBytes(byteOffset, _mftRecordSize, buf, 0) <= 0) return null;
        return FinishRecord(buf, expectedNumber);
    }

    /// <summary>Legge il record MFT indicato passando per la runlist della $MFT.</summary>
    private byte[] ReadMftRecord(long number)
    {
        if (number < 0) return null;

        long offset = number * (long)_mftRecordSize;

        if (_mftRuns == null)
            return ReadRecordAt(_mftLcn * (long)_clusterSize + offset, number);

        // finestra di lettura: 256 KB allineati alla dimensione del record
        const int WindowSize = 256 * 1024;
        if (_mftWindow == null || offset < _mftWindowStart || offset + _mftRecordSize > _mftWindowStart + _mftWindowLen)
        {
            _mftWindow ??= new byte[WindowSize];
            long start = offset / WindowSize * WindowSize;
            Array.Clear(_mftWindow, 0, WindowSize);
            ReadFromRuns(_mftRuns, start, WindowSize, _mftWindow, 0);
            _mftWindowStart = start;
            _mftWindowLen = WindowSize;
        }

        int inWindow = (int)(offset - _mftWindowStart);
        if (inWindow < 0 || inWindow + _mftRecordSize > _mftWindowLen) return null;

        var buf = new byte[_mftRecordSize];
        Array.Copy(_mftWindow, inWindow, buf, 0, _mftRecordSize);
        return FinishRecord(buf, number);
    }

    private byte[] FinishRecord(byte[] buf, long number)
    {
        if (buf.Length < 0x30) return null;

        string magic = Encoding.ASCII.GetString(buf, 0, 4);
        if (magic != "FILE")
        {
            if (magic == "BAAD")
            {
                _badRecordCount++;
                AddNote($"Record MFT {number} marcato BAAD dal sistema: ignorato.");
            }
            return null;
        }

        if (!ApplyFixup(buf, buf.Length, number))
            return null;

        return buf;
    }

    /// <summary>
    /// Applica l'update sequence array: gli ultimi 2 byte di ogni settore del record
    /// contengono il numero di sequenza e vanno rimpiazzati con i valori originali.
    /// Senza questo passaggio ogni settore del record finisce con 2 byte sbagliati.
    /// </summary>
    private bool ApplyFixup(byte[] rec, int length, long number)
    {
        int usaOff = ByteUtils.U16LE(rec, 4);
        int usaCount = ByteUtils.U16LE(rec, 6);

        // l'array sta sempre dopo l'intestazione del record (0x2A su NTFS 1.2, 0x30 dalla 3.0)
        if (usaCount < 1 || usaOff < 0x28 || usaOff + usaCount * 2 > length)
        {
            AddNote($"Update sequence array fuori intervallo nel record {number}: record scartato.");
            return false;
        }

        int sectors = usaCount - 1;
        if (sectors <= 0) return true;

        // Il record deve coprire tutti i settori annunciati.
        if ((long)sectors * _bytesPerSector > length)
        {
            AddNote($"Il record {number} annuncia {sectors} settori ma ne contiene meno: fixup parziale.");
            sectors = length / _bytesPerSector;
        }

        int usn = ByteUtils.U16LE(rec, usaOff);
        bool mismatch = false;

        for (int i = 0; i < sectors; i++)
        {
            int tail = (i + 1) * _bytesPerSector - 2;
            if (tail + 2 > length) break;

            if (ByteUtils.U16LE(rec, tail) != usn) mismatch = true;

            int src = usaOff + 2 + i * 2;
            if (src + 2 > length) break;
            rec[tail] = rec[src];
            rec[tail + 1] = rec[src + 1];
        }

        if (mismatch)
        {
            _badRecordCount++;
            AddNote($"Numero di sequenza non corrispondente nel record {number}: dati probabilmente parziali.");
        }
        return true;
    }

    /// <summary>Fixup per i blocchi di indice "INDX" (stessa logica dei record MFT).</summary>
    private bool ApplyIndexFixup(byte[] blk, int offset, int length)
    {
        if (offset + 0x18 > blk.Length) return false;
        if (Encoding.ASCII.GetString(blk, offset, 4) != "INDX") return false;

        int usaOff = ByteUtils.U16LE(blk, offset + 4);
        int usaCount = ByteUtils.U16LE(blk, offset + 6);
        if (usaCount < 1 || usaOff < 0x18 || usaOff + usaCount * 2 > length) return false;

        int sectors = Math.Min(usaCount - 1, length / _bytesPerSector);
        int usn = ByteUtils.U16LE(blk, offset + usaOff);

        for (int i = 0; i < sectors; i++)
        {
            int tail = offset + (i + 1) * _bytesPerSector - 2;
            if (tail + 2 > offset + length) break;
            int src = offset + usaOff + 2 + i * 2;
            blk[tail] = blk[src];
            blk[tail + 1] = blk[src + 1];
        }
        return true;
    }

    // =====================================================================
    //  Attributi
    // =====================================================================

    private sealed class AttrRef
    {
        public byte[] Rec;
        public int    Offset;
        public int    Length;
        public uint   Type;
        public bool   NonResident;
        public ushort Flags;
        public ushort Id;
        public string Name = "";
        public long   HostMft;
    }

    private List<AttrRef> ParseAttributes(byte[] rec, string ctx)
    {
        var list = new List<AttrRef>();
        if (rec == null || rec.Length < 0x30) return list;

        int first = ByteUtils.U16LE(rec, 0x14);
        int used = (int)ByteUtils.U32LE(rec, 0x18);
        if (used <= 0 || used > rec.Length) used = rec.Length;
        if (first < 0x18 || first >= used) return list;

        int off = first;
        int guard = 0;

        while (off + 8 <= used && guard++ < 2048)
        {
            uint type = ByteUtils.U32LE(rec, off);
            if (type == AT_END) break;

            int len = (int)ByteUtils.U32LE(rec, off + 4);
            if (len < 0x18 || (len & 7) != 0 || off + len > used)
            {
                AddNote($"Attributo malformato in {ctx}: interrompo la lettura del record.");
                break;
            }

            var a = new AttrRef
            {
                Rec = rec,
                Offset = off,
                Length = len,
                Type = type,
                NonResident = rec[off + 8] != 0,
                Flags = ByteUtils.U16LE(rec, off + 0x0C),
                Id = ByteUtils.U16LE(rec, off + 0x0E)
            };

            int nameLen = rec[off + 9];
            int nameOff = ByteUtils.U16LE(rec, off + 0x0A);
            if (nameLen > 0 && nameOff >= 0x18 && off + nameOff + nameLen * 2 <= off + len)
                a.Name = Encoding.Unicode.GetString(rec, off + nameOff, nameLen * 2);

            list.Add(a);
            off += len;
        }

        return list;
    }

    private byte[] ResidentValue(AttrRef a)
    {
        if (a == null || a.NonResident) return null;

        int vlen = (int)ByteUtils.U32LE(a.Rec, a.Offset + 0x10);
        int voff = ByteUtils.U16LE(a.Rec, a.Offset + 0x14);

        if (vlen < 0 || voff < 0x18) return null;
        if (voff > a.Length) return null;

        int avail = Math.Min(a.Length - voff, a.Rec.Length - (a.Offset + voff));
        if (avail <= 0) return null;
        if (vlen > avail) vlen = avail;

        var b = new byte[vlen];
        Array.Copy(a.Rec, a.Offset + voff, b, 0, vlen);
        return b;
    }

    private static long StartVcn(AttrRef a)
        => a.NonResident ? (long)ByteUtils.U64LE(a.Rec, a.Offset + 0x10) : 0;

    // =====================================================================
    //  Runlist
    // =====================================================================

    internal sealed class Run
    {
        public long Vcn;
        public long Lcn;      // -1 = sparso
        public long Length;   // in cluster
        public bool Sparse => Lcn < 0;
    }

    internal sealed class Runlist
    {
        public readonly List<Run> Runs = new();
        public long TotalClusters;
        public bool Damaged;

        public void Add(Run r) { Runs.Add(r); TotalClusters += r.Length; }
    }

    private static long ReadSignedLE(byte[] b, int o, int size)
    {
        long v = 0;
        for (int i = size - 1; i >= 0; i--) v = (v << 8) | b[o + i];
        if (size < 8 && (b[o + size - 1] & 0x80) != 0) v |= -1L << (size * 8);
        return v;
    }

    /// <summary>Decodifica i mapping pair di un attributo non residente dentro la runlist data.</summary>
    private void DecodeRunsInto(Runlist rl, AttrRef a)
    {
        int mpOff = ByteUtils.U16LE(a.Rec, a.Offset + 0x20);
        if (mpOff < 0x18 || mpOff >= a.Length)
        {
            rl.Damaged = true;
            AddNote("Offset dei mapping pair fuori intervallo: runlist non decodificabile.");
            return;
        }

        long vcn = StartVcn(a);
        long lcn = 0;
        int p = a.Offset + mpOff;
        int end = Math.Min(a.Offset + a.Length, a.Rec.Length);
        int guard = 0;

        while (p < end && guard++ < 65536)
        {
            byte h = a.Rec[p++];
            if (h == 0) return;                       // fine regolare della runlist

            int lenSize = h & 0x0F;
            int offSize = (h >> 4) & 0x0F;

            if (lenSize == 0 || lenSize > 8 || offSize > 8 || p + lenSize + offSize > end)
            {
                rl.Damaged = true;
                AddNote("Runlist troncata o corrotta: gli extent successivi sono persi.");
                return;
            }

            long runLen = ReadSignedLE(a.Rec, p, lenSize);
            p += lenSize;

            // il limite tiene i conti su cluster*byte lontani dall'overflow
            if (runLen <= 0 || runLen > MAX_RUN_CLUSTERS || vcn > MAX_RUN_CLUSTERS)
            {
                rl.Damaged = true;
                AddNote("Lunghezza di run non valida: runlist interrotta.");
                return;
            }

            if (offSize == 0)
            {
                // offset assente = run sparso: i cluster non sono allocati e valgono zero
                rl.Add(new Run { Vcn = vcn, Lcn = -1, Length = runLen });
            }
            else
            {
                lcn += ReadSignedLE(a.Rec, p, offSize);
                p += offSize;
                if (lcn < 0)
                {
                    rl.Damaged = true;
                    AddNote("LCN negativo nella runlist: extent scartato.");
                    return;
                }
                rl.Add(new Run { Vcn = vcn, Lcn = lcn, Length = runLen });
            }

            vcn += runLen;
        }
    }

    /// <summary>Legge byte logici di un flusso attraverso la sua runlist; le zone sparse restano a zero.</summary>
    private int ReadFromRuns(Runlist rl, long logicalOffset, int count, byte[] dest, int destOff)
    {
        if (rl == null || count <= 0) return 0;
        Array.Clear(dest, destOff, Math.Min(count, dest.Length - destOff));

        long wantEnd = logicalOffset + count;
        int got = 0;

        foreach (var r in rl.Runs)
        {
            long runStart = r.Vcn * (long)_clusterSize;
            long runEnd = runStart + r.Length * (long)_clusterSize;
            if (runEnd <= logicalOffset) continue;
            if (runStart >= wantEnd) break;

            long from = Math.Max(runStart, logicalOffset);
            long to = Math.Min(runEnd, wantEnd);
            int n = (int)(to - from);
            if (n <= 0) continue;

            int dOff = destOff + (int)(from - logicalOffset);
            if (dOff < 0 || dOff + n > dest.Length) continue;

            if (r.Sparse) { got += n; continue; }      // già azzerato

            long physical = r.Lcn * (long)_clusterSize + (from - runStart);
            Source.ReadBytes(physical, n, dest, dOff);
            got += n;
        }

        return got;
    }

    // =====================================================================
    //  Flussi ($DATA, $INDEX_ALLOCATION, ...)
    // =====================================================================

    internal sealed class DataStream
    {
        public bool    Resident;
        public byte[]  ResidentData;
        public Runlist Runs;
        public long    DataSize;
        public long    AllocatedSize;
        public long    InitializedSize;
        public bool    Compressed;
        public bool    Sparse;
        public bool    Encrypted;
        public int     CompressionUnitClusters;
        public bool    Damaged;
    }

    /// <summary>Informazione specifica NTFS agganciata a <see cref="FsEntry.Tag"/>.</summary>
    public sealed class NtfsTag
    {
        public long MftNumber;
        public ushort Sequence;
        public bool Resident;
        public bool Compressed;
        public bool Sparse;
        public bool Encrypted;
        public int  CompressionUnitClusters;
        public int  Fragments;
        public bool RunlistDamaged;
        public bool HasAttributeList;
        internal DataStream Stream;

        public override string ToString()
        {
            var parts = new List<string> { $"MFT #{MftNumber}" };
            if (Resident) parts.Add("residente");
            if (Compressed) parts.Add($"compresso LZNT1 (unità {CompressionUnitClusters} cluster)");
            if (Sparse) parts.Add("sparso");
            if (Encrypted) parts.Add("cifrato EFS");
            if (HasAttributeList) parts.Add("$ATTRIBUTE_LIST");
            if (Fragments > 1) parts.Add($"{Fragments} frammenti");
            if (RunlistDamaged) parts.Add("runlist danneggiata");
            return string.Join(", ", parts);
        }
    }

    private DataStream BuildStream(List<AttrRef> attrs, uint type, string name)
    {
        List<AttrRef> parts = null;
        foreach (var a in attrs)
            if (a.Type == type && string.Equals(a.Name ?? "", name ?? "", StringComparison.Ordinal))
                (parts ??= new List<AttrRef>()).Add(a);

        if (parts == null || parts.Count == 0) return null;

        if (!parts[0].NonResident)
        {
            var body = ResidentValue(parts[0]) ?? Array.Empty<byte>();
            return new DataStream
            {
                Resident = true,
                ResidentData = body,
                DataSize = body.Length,
                AllocatedSize = body.Length,
                InitializedSize = body.Length,
                Encrypted = (parts[0].Flags & ATTR_ENCRYPTED) != 0
            };
        }

        parts.RemoveAll(p => !p.NonResident);
        if (parts.Count == 0) return null;
        parts.Sort((x, y) => StartVcn(x).CompareTo(StartVcn(y)));

        var ds = new DataStream { Runs = new Runlist() };
        bool gotHeader = false;

        foreach (var a in parts)
        {
            if (!gotHeader && StartVcn(a) == 0)
            {
                gotHeader = true;
                ds.AllocatedSize = (long)ByteUtils.U64LE(a.Rec, a.Offset + 0x28);
                ds.DataSize = (long)ByteUtils.U64LE(a.Rec, a.Offset + 0x30);
                ds.InitializedSize = (long)ByteUtils.U64LE(a.Rec, a.Offset + 0x38);

                int cuExp = ByteUtils.U16LE(a.Rec, a.Offset + 0x22);
                ds.CompressionUnitClusters = cuExp is > 0 and < 24 ? 1 << cuExp : 0;
                ds.Compressed = (a.Flags & ATTR_COMPRESSION_MASK) != 0;
                ds.Sparse = (a.Flags & ATTR_SPARSE) != 0;
                ds.Encrypted = (a.Flags & ATTR_ENCRYPTED) != 0;
            }

            DecodeRunsInto(ds.Runs, a);
        }

        if (!gotHeader)
        {
            ds.Damaged = true;
            AddNote("Flusso senza il frammento iniziale (VCN 0): dimensione sconosciuta.");
            ds.DataSize = ds.Runs.TotalClusters * (long)_clusterSize;
        }

        ds.Runs.Runs.Sort((x, y) => x.Vcn.CompareTo(y.Vcn));
        ds.Damaged |= ds.Runs.Damaged;

        if (ds.Compressed && ds.CompressionUnitClusters <= 0)
        {
            ds.CompressionUnitClusters = 16;
            AddNote("Attributo compresso senza unità di compressione dichiarata: assumo 16 cluster.");
        }

        return ds;
    }

    /// <summary>Legge per intero un flusso in memoria (usato per $ATTRIBUTE_LIST e gli indici).</summary>
    private byte[] ReadWholeStream(DataStream ds, long cap)
    {
        if (ds == null) return null;
        if (ds.Resident) return ds.ResidentData;

        long size = ds.DataSize;
        if (size <= 0 || size > cap) size = Math.Min(Math.Max(size, 0), cap);
        if (size <= 0) return null;

        var buf = new byte[size];
        ReadFromRuns(ds.Runs, 0, (int)size, buf, 0);
        return buf;
    }

    // =====================================================================
    //  File: record base + record di estensione ($ATTRIBUTE_LIST)
    // =====================================================================

    private sealed class FileRecord
    {
        public long   Mft;
        public byte[] Base;
        public List<AttrRef> Attrs = new();
        public bool   InUse;
        public bool   IsDirectory;
        public ushort Sequence;
        public bool   HasAttributeList;
        public bool   ExtensionsMissing;
    }

    private FileRecord LoadFile(long mft) => LoadFile(mft, ReadMftRecord(mft));

    private FileRecord LoadFile(long mft, byte[] rec)
    {
        if (rec == null) return null;

        var f = new FileRecord
        {
            Mft = mft,
            Base = rec,
            Sequence = ByteUtils.U16LE(rec, 0x10),
            InUse = (ByteUtils.U16LE(rec, 0x16) & MFT_IN_USE) != 0,
            IsDirectory = (ByteUtils.U16LE(rec, 0x16) & MFT_DIRECTORY) != 0
        };

        f.Attrs.AddRange(ParseAttributes(rec, $"record {mft}"));
        foreach (var a in f.Attrs) a.HostMft = mft;

        var alist = f.Attrs.Find(a => a.Type == AT_ATTRIBUTE_LIST);
        if (alist == null) return f;

        f.HasAttributeList = true;

        DataStream ds = alist.NonResident
            ? BuildStream(f.Attrs, AT_ATTRIBUTE_LIST, alist.Name)
            : new DataStream { Resident = true, ResidentData = ResidentValue(alist) ?? Array.Empty<byte>() };

        byte[] data = ReadWholeStream(ds, 8 * 1024 * 1024);
        if (data == null || data.Length < 0x19)
        {
            f.ExtensionsMissing = true;
            AddNote($"$ATTRIBUTE_LIST del record {mft} illeggibile: alcuni attributi mancano.");
            return f;
        }

        var extra = new List<long>();
        int p = 0, guard = 0;
        while (p + 0x18 <= data.Length && guard++ < 65536)
        {
            int len = ByteUtils.U16LE(data, p + 4);
            if (len < 0x18 || p + len > data.Length) break;

            ulong fref = ByteUtils.U64LE(data, p + 0x10);
            long host = (long)(fref & 0x0000FFFFFFFFFFFFUL);
            if (host != mft && host > 0 && !extra.Contains(host)) extra.Add(host);

            p += len;
        }

        foreach (long h in extra)
        {
            if (extra.Count > 4096) break;
            byte[] er = ReadMftRecord(h);
            if (er == null)
            {
                f.ExtensionsMissing = true;
                AddNote($"Record di estensione {h} (base {mft}) illeggibile: attributi parziali.");
                continue;
            }

            long baseRef = (long)(ByteUtils.U64LE(er, 0x20) & 0x0000FFFFFFFFFFFFUL);
            if (baseRef != 0 && baseRef != mft)
            {
                f.ExtensionsMissing = true;
                AddNote($"Il record {h} non appartiene al file {mft}: ignorato.");
                continue;
            }

            var ea = ParseAttributes(er, $"estensione {h}");
            foreach (var a in ea) a.HostMft = h;
            f.Attrs.AddRange(ea);
        }

        return f;
    }

    // =====================================================================
    //  Scansione
    // =====================================================================

    private sealed class Node
    {
        public long   Mft;
        public long   ParentMft = -1;
        public string Name;
        public bool   IsDirectory;
        public bool   Deleted;
        public FsEntry Entry;
        public bool   Placed;
        public bool   Visiting;

        /// <summary>Note rimandate: vanno riportate solo se la voce finisce davvero nell'albero.</summary>
        public List<string> Pending;

        // conteggi riportati solo per le voci visibili (i metafile ne hanno di loro)
        public int  Ads;
        public bool Reparse;
        public bool ExtraNames;
    }

    public override void Scan(bool includeDeleted, CancellationToken ct)
    {
        Root = new FsEntry { IsDirectory = true, Name = "", FullPath = "" };
        _adsCount = _reparseCount = _hardLinkCount = _badRecordCount = 0;

        // le note del montaggio restano, quelle della scansione precedente no
        if (_mountNoteCount < 0) _mountNoteCount = Notes.Count;
        if (Notes.Count > _mountNoteCount)
        {
            for (int i = _mountNoteCount; i < Notes.Count; i++) _noteSeen.Remove(Notes[i]);
            Notes.RemoveRange(_mountNoteCount, Notes.Count - _mountNoteCount);
        }

        if (!_mounted)
        {
            AddNote("Volume NTFS non montabile: albero vuoto.");
            return;
        }

        var nodes = new Dictionary<long, Node>();

        // --- passo 1: tutti i record MFT ---
        for (long i = 0; i < _mftRecordCount; i++)
        {
            if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

            byte[] rec;
            try { rec = ReadMftRecord(i); }
            catch (OperationCanceledException) { throw; }
            catch { rec = null; }
            if (rec == null) continue;

            ushort flags = ByteUtils.U16LE(rec, 0x16);
            bool inUse = (flags & MFT_IN_USE) != 0;
            if (!inUse && !includeDeleted) continue;

            // i record di estensione appartengono al loro record base
            if ((ByteUtils.U64LE(rec, 0x20) & 0x0000FFFFFFFFFFFFUL) != 0) continue;

            FileRecord f;
            try { f = LoadFile(i, rec); }
            catch (OperationCanceledException) { throw; }
            catch { f = null; }
            if (f == null) continue;

            var node = BuildNode(f);
            if (node != null) nodes[i] = node;
        }

        if (nodes.Count == 0)
            AddNote("Nessun record MFT utilizzabile: la tabella dei file è probabilmente distrutta.");

        // --- passo 2: collocazione nell'albero ---
        FsEntry orphans = null;

        foreach (var n in nodes.Values.OrderBy(x => x.Mft))
        {
            ct.ThrowIfCancellationRequested();
            if (n.Mft < FIRST_USER_MFT) continue;           // metafile: non visibili
            if (n.Placed) continue;

            var container = ResolveContainer(n.ParentMft, nodes, 0, out bool hidden);
            if (hidden) { n.Placed = true; continue; }      // figlio di un metafile ($Extend & co.)

            if (container == null)
            {
                orphans ??= CreateOrphanFolder();
                container = orphans;
                n.Entry.IsUncertain = true;
            }

            Place(n, container);
        }

        // --- passo 3: indici delle directory, per i figli il cui record MFT è perduto ---
        RecoverFromIndexes(nodes, ct);

        // --- riepilogo ---
        if (_adsCount > 0)
            AddNote($"{_adsCount} flussi di dati alternativi (ADS) presenti e non esposti nell'albero.");
        if (_reparseCount > 0)
            AddNote($"{_reparseCount} reparse point (collegamenti/giunzioni) non seguiti.");
        if (_hardLinkCount > 0)
            AddNote($"{_hardLinkCount} file con più nomi (hard link): mostrato solo il primo percorso.");
        if (_badRecordCount > 0)
            AddNote($"{_badRecordCount} record MFT danneggiati incontrati durante la scansione.");
    }

    private FsEntry CreateOrphanFolder()
    {
        var e = new FsEntry { Name = ORPHAN_DIR, IsDirectory = true, IsUncertain = true };
        AddChild(Root, e);
        AddNote("Trovate voci senza genitore valido: raccolte in \"" + ORPHAN_DIR + "\".");
        return e;
    }

    /// <summary>Oltre questa profondità la catena dei genitori è per forza corrotta.</summary>
    private const int MAX_DEPTH = 512;

    /// <summary>
    /// Restituisce la cartella che deve contenere i figli del record indicato.
    /// hidden = il record è un metafile, i discendenti non vanno mostrati.
    /// null senza hidden = genitore perduto, la voce è orfana.
    /// </summary>
    private FsEntry ResolveContainer(long mft, Dictionary<long, Node> nodes, int depth, out bool hidden)
    {
        hidden = false;
        if (mft < 0) return null;
        if (mft == MFT_ROOT_DIR) return Root;
        if (mft < FIRST_USER_MFT) { hidden = true; return null; }

        if (depth >= MAX_DEPTH)
        {
            AddNote($"Catena dei genitori più profonda di {MAX_DEPTH} livelli attorno al record {mft}: interrotta.");
            return null;
        }

        if (!nodes.TryGetValue(mft, out var n)) return null;
        if (!n.IsDirectory) return null;                     // genitore che non è una cartella

        if (n.Placed) return n.Entry;

        if (n.Visiting)
        {
            AddNote($"Ciclo nei riferimenti al genitore attorno al record {mft}: spezzato.");
            return null;
        }

        n.Visiting = true;
        try
        {
            var container = ResolveContainer(n.ParentMft, nodes, depth + 1, out bool parentHidden);
            if (parentHidden) { hidden = true; n.Placed = true; return null; }

            if (container == null)
            {
                var orphans = Root.Children.Find(c => c.Name == ORPHAN_DIR) ?? CreateOrphanFolder();
                container = orphans;
                n.Entry.IsUncertain = true;
            }

            Place(n, container);
            return n.Entry;
        }
        finally { n.Visiting = false; }
    }

    /// <summary>Aggancia la voce alla cartella e riporta le note che la riguardano.</summary>
    private void Place(Node n, FsEntry container)
    {
        AddChild(container, n.Entry);
        n.Placed = true;

        _adsCount += n.Ads;
        if (n.Reparse) _reparseCount++;
        if (n.ExtraNames) _hardLinkCount++;

        if (n.Pending == null) return;
        foreach (string note in n.Pending) AddNote(note);
        n.Pending = null;
    }

    private Node BuildNode(FileRecord f)
    {
        // --- nome e genitore da $FILE_NAME ---
        string name = null;
        long parent = -1;
        int bestScore = -1;
        int nameCount = 0;
        DateTime? fnCreated = null, fnModified = null;

        foreach (var a in f.Attrs)
        {
            if (a.Type != AT_FILE_NAME || a.NonResident) continue;
            var v = ResidentValue(a);
            if (v == null || v.Length < 0x42) continue;

            int nameLen = v[0x40];
            byte ns = v[0x41];
            if (nameLen <= 0 || 0x42 + nameLen * 2 > v.Length) continue;

            nameCount++;

            // Win32 e Win32&DOS hanno la precedenza, poi POSIX, per ultimo il nome 8.3
            int score = ns switch { 1 => 4, 3 => 3, 0 => 2, 2 => 0, _ => 1 };
            if (score <= bestScore) continue;

            bestScore = score;
            name = Encoding.Unicode.GetString(v, 0x42, nameLen * 2);
            parent = (long)(ByteUtils.U64LE(v, 0) & 0x0000FFFFFFFFFFFFUL);
            fnCreated = ByteUtils.NtfsTime(ByteUtils.U64LE(v, 0x08));
            fnModified = ByteUtils.NtfsTime(ByteUtils.U64LE(v, 0x10));
        }

        if (f.Mft == MFT_ROOT_DIR) { name ??= ""; parent = -1; }
        if (string.IsNullOrEmpty(name)) return null;          // record senza nome: non è una voce

        var node = new Node
        {
            Mft = f.Mft,
            ParentMft = parent,
            Name = name,
            IsDirectory = f.IsDirectory,
            Deleted = !f.InUse,
            ExtraNames = nameCount > 2            // oltre alla coppia Win32 + nome 8.3
        };

        var entry = new FsEntry
        {
            Name = name,
            IsDirectory = f.IsDirectory,
            IsDeleted = !f.InUse
        };

        // --- date da $STANDARD_INFORMATION ---
        var si = f.Attrs.Find(a => a.Type == AT_STANDARD_INFORMATION && !a.NonResident);
        var sv = si != null ? ResidentValue(si) : null;
        if (sv != null && sv.Length >= 0x20)
        {
            entry.Created = ByteUtils.NtfsTime(ByteUtils.U64LE(sv, 0x00));
            entry.Modified = ByteUtils.NtfsTime(ByteUtils.U64LE(sv, 0x08));
        }
        entry.Created ??= fnCreated;
        entry.Modified ??= fnModified;

        node.Reparse = f.Attrs.Exists(a => a.Type == AT_REPARSE_POINT);

        foreach (var a in f.Attrs)
            if (a.Type == AT_DATA && !string.IsNullOrEmpty(a.Name)) node.Ads++;

        var tag = new NtfsTag
        {
            MftNumber = f.Mft,
            Sequence = f.Sequence,
            HasAttributeList = f.HasAttributeList
        };

        if (!f.IsDirectory)
        {
            var ds = BuildStream(f.Attrs, AT_DATA, "");
            if (ds == null)
            {
                // capita sui record cancellati a cui è già stato tolto il $DATA
                entry.IsUncertain = true;
                Defer(node, node.Deleted
                    ? $"Il file cancellato \"{name}\" (record {f.Mft}) non ha più l'attributo $DATA: dati non recuperabili."
                    : $"Il file \"{name}\" (record {f.Mft}) non ha un attributo $DATA leggibile.");
            }
            else
            {
                ApplyStream(entry, tag, ds, name, node);
            }
        }

        if (f.ExtensionsMissing) entry.IsUncertain = true;
        if (node.Deleted && entry.Extents.Count == 0 && entry.InlineData == null && entry.Length > 0)
            entry.IsUncertain = true;

        tag.Fragments = entry.Extents.Count;
        entry.Tag = tag;
        node.Entry = entry;
        return node;
    }

    private static void Defer(Node node, string text)
    {
        if (node == null || string.IsNullOrEmpty(text)) return;
        (node.Pending ??= new List<string>()).Add(text);
    }

    private void ApplyStream(FsEntry entry, NtfsTag tag, DataStream ds, string name, Node node)
    {
        tag.Stream = ds;
        tag.Resident = ds.Resident;
        tag.Compressed = ds.Compressed;
        tag.Sparse = ds.Sparse;
        tag.Encrypted = ds.Encrypted;
        tag.CompressionUnitClusters = ds.CompressionUnitClusters;
        tag.RunlistDamaged = ds.Damaged;

        // Una dimensione fuori scala viene da un attributo corrotto: senza questo
        // limite l'estrazione proverebbe a scrivere terabyte di zeri.
        long limite = Math.Max(TotalBytes, Source.Length) * 2;
        if (ds.DataSize < 0 || (limite > 0 && ds.DataSize > limite))
        {
            long ripiego = ds.Runs != null ? ds.Runs.TotalClusters * (long)_clusterSize : 0;
            Defer(node, $"\"{name}\": dimensione dichiarata assurda ({ds.DataSize} byte), " +
                        $"ridotta a {ripiego} byte dalla runlist.");
            ds.DataSize = ripiego;
            ds.InitializedSize = Math.Min(Math.Max(ds.InitializedSize, 0), ripiego);
            ds.Damaged = true;
            entry.IsUncertain = true;
        }

        entry.Length = ds.DataSize;

        if (ds.Resident)
        {
            entry.InlineData = ds.ResidentData;
            entry.Length = ds.ResidentData?.Length ?? 0;
            return;
        }

        // Gli extent sono in byte assoluti rispetto alla sorgente del volume.
        // Per i file compressi servono solo alla diagnostica: l'estrazione passa da Extract.
        long covered = 0;
        foreach (var r in ds.Runs.Runs)
        {
            if (r.Sparse) { covered += r.Length * (long)_clusterSize; continue; }
            entry.Extents.Add(new FsExtent(r.Lcn * (long)_clusterSize, r.Length * (long)_clusterSize));
            covered += r.Length * (long)_clusterSize;
        }

        if (ds.Damaged) entry.IsUncertain = true;
        if (ds.Encrypted)
        {
            entry.IsUncertain = true;
            Defer(node, $"\"{name}\" è cifrato con EFS: viene estratto il testo cifrato, non decifrabile.");
        }
        if (!ds.Compressed && !ds.Sparse && covered < ds.DataSize)
        {
            entry.IsUncertain = true;
            Defer(node, $"\"{name}\": la runlist copre {covered} byte su {ds.DataSize} dichiarati.");
        }
    }

    // =====================================================================
    //  Indici di directory ($INDEX_ROOT / $INDEX_ALLOCATION)
    // =====================================================================

    private readonly struct IndexEntry
    {
        public readonly long Mft;
        public readonly string Name;
        public readonly bool IsDirectory;
        public readonly long Size;
        public IndexEntry(long mft, string name, bool dir, long size)
        { Mft = mft; Name = name; IsDirectory = dir; Size = size; }
    }

    /// <summary>
    /// Per ogni directory confronta i figli trovati con quelli elencati nell'indice:
    /// se l'indice cita un record MFT che non è stato possibile leggere, la voce viene
    /// comunque mostrata (senza dati) invece di sparire.
    /// </summary>
    private void RecoverFromIndexes(Dictionary<long, Node> nodes, CancellationToken ct)
    {
        int recovered = 0;

        foreach (var n in nodes.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!n.IsDirectory || n.Entry == null) continue;
            if (n.Mft < FIRST_USER_MFT && n.Mft != MFT_ROOT_DIR) continue;

            FsEntry container = n.Mft == MFT_ROOT_DIR ? Root : (n.Placed ? n.Entry : null);
            if (container == null) continue;

            List<IndexEntry> listed;
            try { listed = ReadDirectoryIndex(n.Mft); }
            catch (OperationCanceledException) { throw; }
            catch { listed = null; }
            if (listed == null || listed.Count == 0) continue;

            HashSet<string> presenti = null;

            foreach (var ie in listed)
            {
                if (ie.Mft <= 0 || nodes.ContainsKey(ie.Mft)) continue;       // già gestito dalla MFT

                presenti ??= new HashSet<string>(container.Children.Select(c => c.Name), StringComparer.Ordinal);
                if (!presenti.Add(ie.Name)) continue;

                // la voce è elencata in una directory viva: il file non è cancellato,
                // è il suo record MFT a non essere leggibile
                var stub = new FsEntry
                {
                    Name = ie.Name,
                    IsDirectory = ie.IsDirectory,
                    Length = ie.IsDirectory ? 0 : ie.Size,
                    IsUncertain = true,
                    Tag = new NtfsTag { MftNumber = ie.Mft }
                };
                AddChild(container, stub);
                recovered++;
            }
        }

        if (recovered > 0)
            AddNote($"{recovered} voci note solo dagli indici di directory (record MFT illeggibile): elencate senza dati.");
    }

    private List<IndexEntry> ReadDirectoryIndex(long mft)
    {
        var f = LoadFile(mft);
        if (f == null) return null;

        var result = new List<IndexEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // $INDEX_ROOT
        var root = f.Attrs.Find(a => a.Type == AT_INDEX_ROOT && a.Name == "$I30");
        if (root != null && !root.NonResident)
        {
            var v = ResidentValue(root);
            if (v != null && v.Length >= 0x20)
                CollectIndexEntries(v, 0x10, v.Length, result, seen);
        }

        // $INDEX_ALLOCATION: scorro tutti i blocchi INDX invece di seguire i puntatori,
        // così un nodo rotto non fa perdere tutto il resto dell'albero B+
        var alloc = BuildStream(f.Attrs, AT_INDEX_ALLOCATION, "$I30");
        if (alloc is { Resident: false, Runs: not null })
        {
            long total = alloc.DataSize > 0 ? alloc.DataSize : alloc.Runs.TotalClusters * (long)_clusterSize;
            if (total > 0 && total < 512L * 1024 * 1024)
            {
                var blk = new byte[_indexBlockSize];
                for (long off = 0; off + _indexBlockSize <= total; off += _indexBlockSize)
                {
                    Array.Clear(blk, 0, blk.Length);
                    ReadFromRuns(alloc.Runs, off, _indexBlockSize, blk, 0);
                    if (!ApplyIndexFixup(blk, 0, _indexBlockSize)) continue;
                    CollectIndexEntries(blk, 0x18, _indexBlockSize, result, seen);
                }
            }
        }

        return result;
    }

    private void CollectIndexEntries(byte[] buf, int hdrOff, int limit, List<IndexEntry> result, HashSet<string> seen)
    {
        if (hdrOff + 0x10 > buf.Length || hdrOff + 0x10 > limit) return;

        int entriesOff = (int)ByteUtils.U32LE(buf, hdrOff);
        int entriesLen = (int)ByteUtils.U32LE(buf, hdrOff + 4);

        int p = hdrOff + entriesOff;
        int end = hdrOff + entriesLen;
        if (end > limit) end = limit;
        if (end > buf.Length) end = buf.Length;
        if (p < hdrOff + 0x10 || p >= end) return;

        int guard = 0;
        while (p + 0x10 <= end && guard++ < 8192)
        {
            int entryLen = ByteUtils.U16LE(buf, p + 8);
            int keyLen = ByteUtils.U16LE(buf, p + 0x0A);
            ushort flags = ByteUtils.U16LE(buf, p + 0x0C);

            if (entryLen < 0x10 || p + entryLen > end) break;

            if ((flags & 0x0002) == 0 && keyLen >= 0x42 && p + 0x10 + keyLen <= end)
            {
                int k = p + 0x10;
                long mftRef = (long)(ByteUtils.U64LE(buf, p) & 0x0000FFFFFFFFFFFFUL);
                int nameLen = buf[k + 0x40];
                byte ns = buf[k + 0x41];

                if (nameLen > 0 && k + 0x42 + nameLen * 2 <= end && ns != 2)
                {
                    string name = Encoding.Unicode.GetString(buf, k + 0x42, nameLen * 2);
                    uint fileAttr = ByteUtils.U32LE(buf, k + 0x38);
                    long size = (long)ByteUtils.U64LE(buf, k + 0x30);

                    if (!string.IsNullOrEmpty(name) && name != "." && seen.Add(mftRef + "/" + name))
                        result.Add(new IndexEntry(mftRef, name, (fileAttr & 0x10000000) != 0, size));
                }
            }

            p += entryLen;
            if ((flags & 0x0002) != 0) break;
        }
    }

    // =====================================================================
    //  Estrazione
    // =====================================================================

    public override long Extract(FsEntry entry, Stream destination, CancellationToken ct)
    {
        if (entry == null || entry.IsDirectory || destination == null) return 0;

        var tag = entry.Tag as NtfsTag;
        var ds = tag?.Stream;
        if (ds == null) return base.Extract(entry, destination, ct);

        try
        {
            if (ds.Resident)
            {
                int n = ds.ResidentData?.Length ?? 0;
                if (n > 0) destination.Write(ds.ResidentData, 0, n);
                return n;
            }

            return ds.Compressed
                ? ExtractCompressed(ds, destination, ct)
                : ExtractPlain(ds, destination, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AddNote($"Estrazione di \"{entry.Name}\" interrotta: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Flusso non compresso: run normali dal disco, run sparsi come zeri.</summary>
    private long ExtractPlain(DataStream ds, Stream dest, CancellationToken ct)
    {
        long size = ds.DataSize;
        if (size <= 0) return 0;

        long init = ds.InitializedSize;
        if (init <= 0 || init > size) init = size;

        var buf = new byte[Math.Min(1 << 20, Math.Max(_clusterSize, 65536))];
        long written = 0;

        while (written < size)
        {
            ct.ThrowIfCancellationRequested();

            int chunk = (int)Math.Min(buf.Length, size - written);
            Array.Clear(buf, 0, chunk);

            if (written < init)
            {
                int fromDisk = (int)Math.Min(chunk, init - written);
                ReadFromRuns(ds.Runs, written, fromDisk, buf, 0);
            }

            dest.Write(buf, 0, chunk);
            written += chunk;
        }

        return written;
    }

    /// <summary>
    /// Flusso compresso: l'unità di compressione vale CompressionUnitClusters cluster.
    /// Se nessun cluster dell'unità è allocato l'unità è tutta zeri; se l'unità è
    /// allocata per intero i dati sono memorizzati così come sono; se ne è allocata
    /// solo una parte il contenuto è una sequenza di blocchi LZNT1 da decomprimere.
    /// Vale anche per l'ultima unità parziale: NTFS ci mette comunque i blocchi LZNT1
    /// (eventualmente tutti "non compressi"), non i byte grezzi.
    /// </summary>
    private long ExtractCompressed(DataStream ds, Stream dest, CancellationToken ct)
    {
        long size = ds.DataSize;
        if (size <= 0) return 0;

        int cuClusters = ds.CompressionUnitClusters > 0 ? ds.CompressionUnitClusters : 16;
        long cuBytesL = (long)cuClusters * _clusterSize;
        if (cuBytesL <= 0 || cuBytesL > 16 * 1024 * 1024)
        {
            AddNote($"Unità di compressione fuori scala ({cuBytesL} byte): estraggo i dati senza decomprimerli.");
            return ExtractPlain(ds, dest, ct);
        }

        int cuBytes = (int)cuBytesL;
        var raw = new byte[cuBytes];
        var outBuf = new byte[cuBytes];
        long written = 0;
        bool warned = false;

        for (long cuVcn = 0; written < size; cuVcn += cuClusters)
        {
            ct.ThrowIfCancellationRequested();

            long unitStart = cuVcn * (long)_clusterSize;
            long validInUnit = Math.Min(cuBytes, size - unitStart);
            if (validInUnit <= 0) break;

            int mapped = CountMappedClusters(ds.Runs, cuVcn, cuClusters);
            Array.Clear(outBuf, 0, cuBytes);

            if (mapped == 0)
            {
                // unità interamente sparsa: zeri
            }
            else if (mapped >= cuClusters)
            {
                // unità allocata per intero: i dati sono in chiaro
                ReadFromRuns(ds.Runs, unitStart, (int)validInUnit, outBuf, 0);
            }
            else
            {
                int compressedBytes = mapped * _clusterSize;
                Array.Clear(raw, 0, cuBytes);
                ReadCompactClusters(ds.Runs, cuVcn, cuClusters, raw);

                // rete di sicurezza: se non c'è una intestazione di blocco LZNT1
                // plausibile (firma 3 nei bit 12-14) i dati non sono compressi
                int header = raw[0] | (raw[1] << 8);
                if (header != 0 && (header & 0x7000) != 0x3000)
                {
                    if (!warned)
                    {
                        warned = true;
                        AddNote("Unità di compressione senza intestazione LZNT1 valida: copiata così com'è.");
                    }
                    Array.Copy(raw, 0, outBuf, 0, Math.Min(compressedBytes, (int)validInUnit));
                }
                else
                {
                    int produced = Lznt1Decompress(raw, 0, compressedBytes, outBuf, 0, cuBytes, out bool damaged);
                    if (damaged && !warned)
                    {
                        warned = true;
                        AddNote("Dati LZNT1 malformati durante la decompressione: il file esce parziale.");
                    }
                    if (produced < validInUnit && !warned)
                    {
                        warned = true;
                        AddNote("Unità di compressione più corta del previsto: parte del file risulta azzerata.");
                    }
                }
            }

            int n = (int)Math.Min(validInUnit, size - written);
            dest.Write(outBuf, 0, n);
            written += n;
        }

        return written;
    }

    private static int CountMappedClusters(Runlist rl, long vcn, int count)
    {
        if (rl == null) return 0;
        long end = vcn + count;
        int mapped = 0;

        foreach (var r in rl.Runs)
        {
            if (r.Sparse) continue;
            long a = Math.Max(r.Vcn, vcn);
            long b = Math.Min(r.Vcn + r.Length, end);
            if (b > a) mapped += (int)(b - a);
        }
        return mapped;
    }

    /// <summary>
    /// Copia in <paramref name="dest"/>, uno dopo l'altro, i soli cluster allocati
    /// dell'unità: è la forma in cui LZNT1 si aspetta i dati compressi.
    /// </summary>
    private void ReadCompactClusters(Runlist rl, long vcn, int count, byte[] dest)
    {
        long end = vcn + count;
        int outOff = 0;

        foreach (var r in rl.Runs)
        {
            if (r.Sparse) continue;
            long a = Math.Max(r.Vcn, vcn);
            long b = Math.Min(r.Vcn + r.Length, end);
            if (b <= a) continue;

            int n = (int)((b - a) * _clusterSize);
            if (outOff + n > dest.Length) n = dest.Length - outOff;
            if (n <= 0) break;

            long physical = (r.Lcn + (a - r.Vcn)) * (long)_clusterSize;
            Source.ReadBytes(physical, n, dest, outOff);
            outOff += n;
        }
    }

    // =====================================================================
    //  LZNT1
    // =====================================================================

    /// <summary>
    /// Decomprime una unità LZNT1 (sequenza di blocchi da 4 KB preceduti da
    /// un'intestazione a 16 bit). Un blocco con il bit 15 a zero non è compresso e va
    /// copiato tale e quale; l'intestazione nulla segna la fine dei dati utili.
    /// </summary>
    internal static int Lznt1Decompress(byte[] src, int srcOff, int srcLen,
                                        byte[] dst, int dstOff, int dstMax, out bool damaged)
    {
        damaged = false;
        if (src == null || dst == null || srcLen <= 0 || dstMax <= 0) return 0;

        int sp = srcOff;
        int sEnd = Math.Min(srcOff + srcLen, src.Length);
        int dp = dstOff;
        int dEnd = Math.Min(dstOff + dstMax, dst.Length);

        while (sp + 2 <= sEnd && dp < dEnd)
        {
            int header = src[sp] | (src[sp + 1] << 8);
            sp += 2;
            if (header == 0) break;                    // fine: il resto dell'unità è zero

            int size = (header & 0x0FFF) + 1;          // byte che seguono l'intestazione
            bool compressed = (header & 0x8000) != 0;

            if (sp + size > sEnd)
            {
                size = sEnd - sp;
                damaged = true;
                if (size <= 0) break;
            }

            if (!compressed)
            {
                int n = Math.Min(size, dEnd - dp);
                Array.Copy(src, sp, dst, dp, n);
                dp += n;
                sp += size;
                continue;
            }

            int blockEnd = sp + size;
            int chunkStart = dp;
            int chunkLimit = Math.Min(dEnd, chunkStart + LZNT1_CHUNK);

            while (sp < blockEnd && dp < chunkLimit)
            {
                byte flags = src[sp++];

                for (int bit = 0; bit < 8 && sp < blockEnd && dp < chunkLimit; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        dst[dp++] = src[sp++];
                        continue;
                    }

                    if (sp + 2 > blockEnd) { damaged = true; sp = blockEnd; break; }

                    int token = src[sp] | (src[sp + 1] << 8);
                    sp += 2;

                    // la ripartizione fra distanza e lunghezza dipende da quanto
                    // è già stato prodotto all'interno del blocco
                    int lg = 0;
                    for (int i = dp - chunkStart - 1; i >= 0x10; i >>= 1) lg++;

                    int delta = (token >> (12 - lg)) + 1;
                    int length = (token & (0x0FFF >> lg)) + 3;

                    int from = dp - delta;
                    if (from < chunkStart) { damaged = true; sp = blockEnd; break; }

                    for (int k = 0; k < length && dp < chunkLimit; k++)
                        dst[dp++] = dst[from++];
                }
            }

            sp = blockEnd;
        }

        return dp - dstOff;
    }

    // =====================================================================
    //  Note
    // =====================================================================

    private void AddNote(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Notes.Count >= 300) return;
        if (!_noteSeen.Add(text)) return;
        Notes.Add(text);
        if (Notes.Count == 300) Notes.Add("Altre anomalie omesse: elenco troppo lungo.");
    }

    public override void Dispose()
    {
        _mftWindow = null;
        base.Dispose();
    }
}
