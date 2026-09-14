using System.IO.Compression;
using System.Text;
using DVDRescue.Core;

namespace DVDRescue.FileSystems;

/// <summary>
/// Driver in sola lettura per HFS+ e HFSX (Mac OS 8.1 -> macOS).
/// Legge il volume header, i B-tree di catalogo, extent overflow e attributi estesi,
/// ricostruisce l'albero scorrendo tutti i nodi foglia del catalogo (piu' robusto
/// della navigazione per chiave su volumi danneggiati), espone i resource fork come
/// voci separate e riconosce la compressione decmpfs.
///
/// Riconosce anche il wrapper HFS ('BD' a offset 1024): se contiene un HFS+ annidato
/// si sposta su quello, altrimenti dichiara che l'HFS classico non e' supportato.
///
/// La sorgente passata al costruttore e' il VOLUME: offset 0 = primo byte del volume.
/// Gli extent prodotti sono in byte assoluti rispetto a quella sorgente (comprendono
/// quindi lo scostamento del volume annidato, quando c'e').
///
/// Tutti i campi su disco sono big endian.
/// </summary>
public sealed class HfsPlusFileSystem : FileSystemBase
{
    // ---------------------------------------------------------------- costanti

    private const int VhOffset = 1024;          // volume header / MDB: 2 settori da 512
    private const int VhSize = 512;

    private const ushort SigHfsPlus = 0x482B;   // 'H+'
    private const ushort SigHfsx = 0x4858;      // 'HX'
    private const ushort SigHfsWrapper = 0x4244; // 'BD' (MDB dell'HFS classico)

    // CNID riservati (TN1150)
    private const uint CnidRootParent = 1;
    private const uint CnidRoot = 2;
    private const uint CnidExtents = 3;
    private const uint CnidCatalog = 4;
    private const uint CnidBadBlocks = 5;
    private const uint CnidAllocation = 6;
    private const uint CnidStartup = 7;
    private const uint CnidAttributes = 8;

    // tipi di record del catalogo
    private const ushort RecFolder = 0x0001;
    private const ushort RecFile = 0x0002;
    private const ushort RecFolderThread = 0x0003;
    private const ushort RecFileThread = 0x0004;

    // tipi di nodo del B-tree
    private const sbyte NodeLeaf = -1;
    private const sbyte NodeIndex = 0;
    private const sbyte NodeHeader = 1;
    private const sbyte NodeMap = 2;

    // fork type nelle chiavi dell'extents overflow
    private const byte ForkTypeData = 0x00;
    private const byte ForkTypeResource = 0xFF;

    // flag del record file
    private const ushort FlagThreadExists = 0x0002;
    private const ushort FlagHasAttributes = 0x0004;

    // record inline dell'albero attributi
    private const uint AttrInlineData = 0x10;

    private const string DecmpfsName = "com.apple.decmpfs";
    private const uint DecmpfsMagic = 0x636D7066;   // 'fpmc' letto little endian
    private const int DecmpfsHeaderSize = 16;

    /// <summary>Tetto di sicurezza su quanto si accetta di decomprimere in memoria.</summary>
    private const long MaxDecompressedSize = 512L * 1024 * 1024;

    /// <summary>Il resource fork compresso decmpfs lavora a blocchi da 64 KiB.</summary>
    private const int DecmpfsBlockSize = 0x10000;

    // ------------------------------------------------------------------- stato

    private long _base;              // offset in byte del volume HFS+ dentro Source
    private long _volumeBytes;       // dimensione del volume HFS+
    private bool _valid;
    private bool _hfsOnly;           // wrapper HFS senza HFS+ annidato
    private bool _wrapped;           // HFS+ annidato in un wrapper HFS
    private string _wrapperLabel = "";

    private ushort _signature;
    private ushort _version;
    private uint _attributes;
    private uint _blockSize;
    private uint _totalBlocks;
    private uint _freeBlocks;
    private uint _fileCount;
    private uint _folderCount;
    private uint _nextCatalogId;
    private DateTime? _createDate;
    private DateTime? _modifyDate;
    private string _lastMountedVersion = "";

    private ForkInfo _allocationFork;
    private ForkInfo _extentsFork;
    private ForkInfo _catalogFork;
    private ForkInfo _attributesFork;

    /// <summary>Extent oltre gli 8 inline, presi dall'extents overflow: (fileID, forkType) -&gt; lista.</summary>
    private readonly Dictionary<long, List<BlockExtent>> _overflow = new();

    /// <summary>Payload dell'attributo com.apple.decmpfs per fileID.</summary>
    private readonly Dictionary<uint, byte[]> _decmpfs = new();

    private bool _scanned;
    private int _namesNormalized;

    // ---------------------------------------------------------------- interfaccia

    public override string TypeName => _signature == SigHfsx ? "HFSX" : "HFS+";

    public override long TotalBytes => _volumeBytes > 0 ? _volumeBytes : Source.Length;

    public HfsPlusFileSystem(IBlockSource source) : base(source)
    {
        try { Initialize(); }
        catch { _valid = false; }
    }

    /// <summary>
    /// Vero se la sorgente comincia con un volume HFS+/HFSX, anche annidato
    /// dentro un wrapper HFS. Il solo wrapper HFS senza HFS+ dentro non conta.
    /// </summary>
    public static bool Detect(IBlockSource source)
    {
        try { return Locate(source, out _, out _, out _, out _); }
        catch { return false; }
    }

    // ------------------------------------------------------------- inizializzazione

    private void Initialize()
    {
        if (!Locate(Source, out _base, out byte[] vh, out bool hfsOnly, out string wrapperLabel))
        {
            _hfsOnly = hfsOnly;
            _wrapperLabel = wrapperLabel ?? "";
            if (_hfsOnly) VolumeLabel = _wrapperLabel;
            return;
        }

        _wrapped = _base != 0;
        _wrapperLabel = wrapperLabel ?? "";

        _signature = ByteUtils.U16BE(vh, 0);
        _version = ByteUtils.U16BE(vh, 2);
        _attributes = ByteUtils.U32BE(vh, 4);
        _lastMountedVersion = ByteUtils.Ascii(vh, 8, 4);
        _createDate = HfsLocalDate(ByteUtils.U32BE(vh, 16));
        _modifyDate = ByteUtils.HfsTime(ByteUtils.U32BE(vh, 20));
        _fileCount = ByteUtils.U32BE(vh, 32);
        _folderCount = ByteUtils.U32BE(vh, 36);
        _blockSize = ByteUtils.U32BE(vh, 40);
        _totalBlocks = ByteUtils.U32BE(vh, 44);
        _freeBlocks = ByteUtils.U32BE(vh, 48);
        _nextCatalogId = ByteUtils.U32BE(vh, 64);

        _allocationFork = ReadForkData(vh, 112, CnidAllocation);
        _extentsFork = ReadForkData(vh, 192, CnidExtents);
        _catalogFork = ReadForkData(vh, 272, CnidCatalog);
        _attributesFork = ReadForkData(vh, 352, CnidAttributes);

        _volumeBytes = (long)_totalBlocks * _blockSize;
        _valid = true;
    }

    /// <summary>
    /// Cerca il volume header. Ritorna false se non c'e' nulla di utilizzabile;
    /// in quel caso <paramref name="hfsOnly"/> dice se almeno il wrapper HFS c'era.
    /// </summary>
    private static bool Locate(IBlockSource source, out long baseOffset, out byte[] header,
                              out bool hfsOnly, out string wrapperLabel)
    {
        baseOffset = 0;
        header = null;
        hfsOnly = false;
        wrapperLabel = "";

        var first = new byte[VhSize];
        if (source.ReadBytes(VhOffset, VhSize, first, 0) < 4) return false;

        ushort sig = ByteUtils.U16BE(first, 0);

        if (sig == SigHfsPlus || sig == SigHfsx)
        {
            if (!PlausibleHeader(first, source.Length, 0)) return false;
            header = first;
            return true;
        }

        if (sig != SigHfsWrapper) return false;

        // wrapper HFS: cerca l'HFS+ annidato descritto dall'embedded extent
        wrapperLabel = MacRomanPascal(first, 36, 28);
        uint allocBlockSize = ByteUtils.U32BE(first, 20);
        ushort firstAllocBlock = ByteUtils.U16BE(first, 28);   // in settori da 512
        ushort embedSig = ByteUtils.U16BE(first, 124);
        ushort embedStart = ByteUtils.U16BE(first, 126);
        ushort embedCount = ByteUtils.U16BE(first, 128);

        if (embedSig != SigHfsPlus && embedSig != SigHfsx) { hfsOnly = true; return false; }
        if (allocBlockSize == 0 || (allocBlockSize & 511) != 0 || embedCount == 0)
        {
            hfsOnly = true;
            return false;
        }

        long start = (long)firstAllocBlock * 512 + (long)embedStart * allocBlockSize;
        if (start <= 0 || start >= source.Length) { hfsOnly = true; return false; }

        var nested = new byte[VhSize];
        if (source.ReadBytes(start + VhOffset, VhSize, nested, 0) < 4) { hfsOnly = true; return false; }

        ushort nestedSig = ByteUtils.U16BE(nested, 0);
        if (nestedSig != SigHfsPlus && nestedSig != SigHfsx) { hfsOnly = true; return false; }
        if (!PlausibleHeader(nested, source.Length, start)) { hfsOnly = true; return false; }

        baseOffset = start;
        header = nested;
        return true;
    }

    /// <summary>Controlli minimi per non scambiare rumore per un volume header.</summary>
    private static bool PlausibleHeader(byte[] vh, long sourceLength, long baseOffset)
    {
        ushort version = ByteUtils.U16BE(vh, 2);
        if (version < 4 || version > 6) return false;

        uint blockSize = ByteUtils.U32BE(vh, 40);
        if (blockSize < 512 || blockSize > (1 << 20)) return false;
        if ((blockSize & (blockSize - 1)) != 0) return false;

        uint totalBlocks = ByteUtils.U32BE(vh, 44);
        if (totalBlocks == 0) return false;

        // il catalogo deve esistere, altrimenti non c'e' niente da leggere
        uint catalogBlocks = ByteUtils.U32BE(vh, 272 + 12);
        if (catalogBlocks == 0) return false;

        // tolleranza: alcune immagini sono troncate, quindi non si pretende
        // che il volume ci stia tutto, solo che non sia assurdamente grande
        if (sourceLength > 0)
        {
            long declared = (long)totalBlocks * blockSize;
            if (declared <= 0 || baseOffset + declared > sourceLength * 4 + (1L << 30)) return false;
        }

        return true;
    }

    // ------------------------------------------------------------------- scansione

    public override void Scan(bool includeDeleted, CancellationToken ct)
    {
        if (_scanned) return;
        _scanned = true;

        Root = new FsEntry { IsDirectory = true, Name = "", FullPath = "" };

        if (!_valid)
        {
            if (_hfsOnly)
            {
                VolumeLabel = _wrapperLabel;
                Notes.Add("Trovato un wrapper HFS classico ('BD') senza volume HFS+ annidato: " +
                          "l'HFS classico (Mac OS fino a 8.0) non e' supportato da questo driver, " +
                          "quindi l'albero risulta vuoto. Il volume non e' danneggiato, " +
                          "e' semplicemente un formato diverso.");
                if (!string.IsNullOrEmpty(_wrapperLabel))
                    Notes.Add($"Etichetta del volume HFS: \"{_wrapperLabel}\".");
            }
            else
            {
                Notes.Add("Nessun volume header HFS+/HFSX valido all'offset 1024: " +
                          "la sorgente non e' HFS+ oppure l'inizio del volume e' illeggibile.");
            }
            return;
        }

        if (_wrapped)
            Notes.Add($"Volume HFS+ annidato dentro un wrapper HFS classico, a partire dal byte {_base}.");

        if ((_attributes & (1u << 15)) != 0)
            Notes.Add("Il volume e' marcato in sola scrittura software (software lock).");
        if ((_attributes & (1u << 8)) == 0)
            Notes.Add("Il volume non risulta smontato correttamente: le strutture potrebbero " +
                      "non essere aggiornate. Se c'e' un journal, le modifiche piu' recenti " +
                      "non sono state riportate (il journal non viene riprodotto).");
        if ((_attributes & (1u << 13)) != 0)
            Notes.Add("Volume con journal: la lettura ignora il journal e usa le strutture su disco.");
        if ((_attributes & (1u << 11)) != 0)
            Notes.Add("Il volume e' marcato incoerente (bit 'boot volume inconsistent').");

        // 1) extents overflow: serve per risolvere i fork frammentati, catalogo compreso
        try { LoadExtentsOverflow(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Notes.Add("Extents overflow non leggibile: " + Short(ex)); }

        // ora che gli overflow ci sono, i fork di sistema si possono completare
        Complete(_catalogFork, CnidCatalog);
        Complete(_attributesFork, CnidAttributes);
        Complete(_allocationFork, CnidAllocation);

        // 2) attributi estesi: serve solo decmpfs
        try { LoadDecmpfsAttributes(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Notes.Add("Albero degli attributi estesi non leggibile: " + Short(ex)); }

        // 3) catalogo
        try { ScanCatalog(includeDeleted, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Notes.Add("Catalogo non leggibile: " + Short(ex)); }

        if (!includeDeleted)
            Notes.Add("HFS+ cancella davvero i record dal catalogo: le voci eliminate non " +
                      "compaiono. Con il recupero dei file cancellati attivo si tenta almeno " +
                      "la lettura dei record residui nei nodi liberi del B-tree.");

        long used = (long)(_totalBlocks - _freeBlocks) * _blockSize;
        Notes.Add($"Volume {TypeName} v{_version}, blocchi da {_blockSize} byte, " +
                  $"{_totalBlocks} totali, {_freeBlocks} liberi ({used / 1048576} MB occupati). " +
                  $"Header: {_fileCount} file e {_folderCount} cartelle" +
                  (string.IsNullOrEmpty(_lastMountedVersion) ? "" : $", ultimo montaggio \"{_lastMountedVersion}\"") +
                  $". Creato il {_createDate:dd/MM/yyyy HH:mm}.");

        if (_namesNormalized > 0)
            Notes.Add($"{_namesNormalized} nomi erano in forma decomposta (HFS+ scrive gli accenti " +
                      "separati dalla lettera) e sono stati ricomposti per la visualizzazione.");
    }

    // ---------------------------------------------------------------- fork

    private sealed class BlockExtent
    {
        public uint Start;
        public uint Count;
        /// <summary>Solo per gli extent dell'overflow: blocco di partenza dentro il file.</summary>
        public uint FileBlock;
        public BlockExtent(uint s, uint c) { Start = s; Count = c; }
    }

    private sealed class ForkInfo
    {
        public long LogicalSize;
        public uint TotalBlocks;
        public uint FileId;
        public byte ForkType;
        public List<BlockExtent> Extents = new();
        public bool Truncated;      // gli extent non coprono la dimensione dichiarata
        public bool Clipped;        // qualche extent usciva dal volume ed e' stato tagliato
    }

    /// <summary>Legge una HFSPlusForkData (80 byte): dimensione, clump e 8 extent.</summary>
    private ForkInfo ReadForkData(byte[] b, int off, uint fileId, byte forkType = ForkTypeData)
    {
        var f = new ForkInfo { FileId = fileId, ForkType = forkType };
        if (b == null || off < 0 || off + 80 > b.Length) return f;

        f.LogicalSize = (long)ByteUtils.U64BE(b, off);
        f.TotalBlocks = ByteUtils.U32BE(b, off + 12);

        for (int i = 0; i < 8; i++)
        {
            uint start = ByteUtils.U32BE(b, off + 16 + i * 8);
            uint count = ByteUtils.U32BE(b, off + 20 + i * 8);
            if (count == 0) continue;
            AddExtent(f, start, count);
        }

        if (f.LogicalSize < 0) f.LogicalSize = 0;
        return f;
    }

    private void AddExtent(ForkInfo f, uint start, uint count)
    {
        if (count == 0) return;

        // extent fuori dal volume: si taglia invece di leggere a caso
        if (_totalBlocks > 0)
        {
            if (start >= _totalBlocks) { f.Clipped = true; return; }
            if ((long)start + count > _totalBlocks)
            {
                count = _totalBlocks - start;
                f.Clipped = true;
            }
        }

        var last = f.Extents.Count > 0 ? f.Extents[^1] : null;
        if (last != null && last.Start + last.Count == start) last.Count += count;
        else f.Extents.Add(new BlockExtent(start, count));
    }

    /// <summary>Aggiunge al fork gli extent presi dall'extents overflow.</summary>
    private void Complete(ForkInfo f, uint fileId, byte forkType = ForkTypeData)
    {
        if (f == null) return;

        long allocated = 0;
        foreach (var e in f.Extents) allocated += e.Count;
        if (allocated >= f.TotalBlocks || f.TotalBlocks == 0)
        {
            CheckTruncation(f, allocated);
            return;
        }

        if (_overflow.TryGetValue(OverflowKey(fileId, forkType), out var extra))
        {
            foreach (var e in extra)
            {
                if (allocated >= f.TotalBlocks) break;
                uint count = e.Count;
                if (allocated + count > f.TotalBlocks) count = (uint)(f.TotalBlocks - allocated);
                AddExtent(f, e.Start, count);
                allocated += count;
            }
        }

        CheckTruncation(f, allocated);
    }

    private void CheckTruncation(ForkInfo f, long allocatedBlocks)
    {
        long covered = 0;
        foreach (var e in f.Extents) covered += (long)e.Count * _blockSize;
        if (covered < f.LogicalSize) f.Truncated = true;
    }

    private static long OverflowKey(uint fileId, byte forkType) => ((long)forkType << 32) | fileId;

    /// <summary>Legge byte da un fork, saltando fra i suoi extent.</summary>
    private int ReadFork(ForkInfo f, long offset, int count, byte[] dest, int destOffset)
    {
        if (f == null || count <= 0 || offset < 0) return 0;
        Array.Clear(dest, destOffset, count);

        int done = 0;
        long pos = 0;

        foreach (var e in f.Extents)
        {
            long extentBytes = (long)e.Count * _blockSize;
            if (offset >= pos + extentBytes) { pos += extentBytes; continue; }

            long insideExtent = offset + done - pos;
            if (insideExtent < 0) break;

            int chunk = (int)Math.Min(count - done, extentBytes - insideExtent);
            if (chunk <= 0) { pos += extentBytes; continue; }

            long absolute = _base + (long)e.Start * _blockSize + insideExtent;
            Source.ReadBytes(absolute, chunk, dest, destOffset + done);

            done += chunk;
            pos += extentBytes;
            if (done >= count) break;
        }

        return done;
    }

    // ---------------------------------------------------------------- B-tree

    private sealed class BTree
    {
        public ForkInfo Fork;
        public int NodeSize;
        public ushort Depth;
        public uint RootNode;
        public uint LeafRecords;
        public uint FirstLeaf;
        public uint LastLeaf;
        public uint TotalNodes;
        public uint FreeNodes;
        public ushort MaxKeyLength;
        public uint Attributes;
        public byte KeyCompareType;
        public bool[] InUse;        // mappa di allocazione dei nodi
    }

    /// <summary>Legge il nodo header (nodo 0) e la mappa di allocazione dei nodi.</summary>
    private BTree OpenBTree(ForkInfo fork, string label)
    {
        if (fork == null || fork.Extents.Count == 0) return null;

        // il nodeSize non e' noto prima di leggere: si parte da 512, il minimo
        var probe = new byte[512];
        if (ReadFork(fork, 0, 512, probe, 0) < 512) return null;

        if ((sbyte)probe[8] != NodeHeader) return null;

        int nodeSize = ByteUtils.U16BE(probe, 14 + 18);
        if (nodeSize < 512 || nodeSize > (1 << 16) || (nodeSize & (nodeSize - 1)) != 0)
        {
            Notes.Add($"B-tree {label}: nodeSize {nodeSize} non valido.");
            return null;
        }

        var t = new BTree
        {
            Fork = fork,
            NodeSize = nodeSize,
            Depth = ByteUtils.U16BE(probe, 14 + 0),
            RootNode = ByteUtils.U32BE(probe, 14 + 2),
            LeafRecords = ByteUtils.U32BE(probe, 14 + 6),
            FirstLeaf = ByteUtils.U32BE(probe, 14 + 10),
            LastLeaf = ByteUtils.U32BE(probe, 14 + 14),
            MaxKeyLength = ByteUtils.U16BE(probe, 14 + 20),
            TotalNodes = ByteUtils.U32BE(probe, 14 + 22),
            FreeNodes = ByteUtils.U32BE(probe, 14 + 26),
            KeyCompareType = probe[14 + 37],
            Attributes = ByteUtils.U32BE(probe, 14 + 38),
        };

        // se totalNodes e' assurdo lo si ricava dallo spazio davvero allocato
        long capacity = 0;
        foreach (var e in fork.Extents) capacity += (long)e.Count * _blockSize;
        long maxNodes = capacity / nodeSize;
        if (t.TotalNodes == 0 || t.TotalNodes > maxNodes)
        {
            if (t.TotalNodes != 0)
                Notes.Add($"B-tree {label}: totalNodes {t.TotalNodes} oltre lo spazio allocato, " +
                          $"ridotto a {maxNodes}.");
            t.TotalNodes = (uint)Math.Max(0, Math.Min(maxNodes, int.MaxValue));
        }

        t.InUse = ReadNodeBitmap(t, label);
        return t;
    }

    /// <summary>Mappa di allocazione: record 2 del nodo header, poi eventuali nodi mappa.</summary>
    private bool[] ReadNodeBitmap(BTree t, string label)
    {
        var map = new bool[t.TotalNodes];
        try
        {
            var node = new byte[t.NodeSize];
            if (ReadFork(t.Fork, 0, t.NodeSize, node, 0) < t.NodeSize) return null;

            int bit = 0;
            uint next = ByteUtils.U32BE(node, 0);       // fLink del nodo header -> primo nodo mappa
            var records = GetRecordSpans(node, t.NodeSize);
            if (records == null || records.Count < 3) return null;

            var (off, len) = records[2];
            bit = FillBits(map, bit, node, off, len);

            var seen = new HashSet<uint>();
            int guard = 0;
            while (next != 0 && bit < map.Length && guard++ < 4096 && seen.Add(next))
            {
                if (next >= t.TotalNodes) break;
                if (!TryReadNode(t, next, node)) break;
                if ((sbyte)node[8] != NodeMap) break;

                var mr = GetRecordSpans(node, t.NodeSize);
                if (mr == null || mr.Count < 1) break;
                bit = FillBits(map, bit, node, mr[0].off, mr[0].len);
                next = ByteUtils.U32BE(node, 0);
            }
        }
        catch
        {
            Notes.Add($"B-tree {label}: mappa di allocazione dei nodi illeggibile.");
            return null;
        }
        return map;
    }

    private static int FillBits(bool[] map, int bit, byte[] node, int off, int len)
    {
        for (int i = 0; i < len && bit < map.Length; i++)
        {
            byte v = node[off + i];
            for (int k = 7; k >= 0 && bit < map.Length; k--)
                map[bit++] = ((v >> k) & 1) != 0;
        }
        return bit;
    }

    private bool TryReadNode(BTree t, uint number, byte[] dest)
    {
        if (number >= t.TotalNodes) return false;
        long off = (long)number * t.NodeSize;
        return ReadFork(t.Fork, off, t.NodeSize, dest, 0) == t.NodeSize;
    }

    /// <summary>
    /// Estrae i record di un nodo dall'array di offset in fondo (interi a 16 bit,
    /// il primo negli ultimi 2 byte). Ritorna null se il nodo non e' coerente.
    /// </summary>
    private static List<(int off, int len)> GetRecordSpans(byte[] node, int nodeSize)
    {
        if (node == null || node.Length < nodeSize || nodeSize < 16) return null;

        int num = ByteUtils.U16BE(node, 10);
        if (num <= 0) return num == 0 ? new List<(int, int)>() : null;

        // ogni record occupa almeno 1 byte piu' i 2 dell'offset, piu' l'offset finale
        int maxRecords = (nodeSize - 14) / 3;
        if (num > maxRecords) return null;

        var offsets = new int[num + 1];
        for (int i = 0; i <= num; i++)
        {
            int p = nodeSize - 2 * (i + 1);
            if (p < 0) return null;
            offsets[i] = ByteUtils.U16BE(node, p);
        }

        int limit = nodeSize - 2 * (num + 1);
        var list = new List<(int, int)>(num);
        for (int i = 0; i < num; i++)
        {
            int start = offsets[i], end = offsets[i + 1];
            if (start < 14 || end > limit || end < start) return null;
            list.Add((start, end - start));
        }
        return list;
    }

    /// <summary>Offset del dato che segue la chiave; le chiavi sono allineate a 2 byte.</summary>
    private static int KeyDataOffset(byte[] node, int recordOffset, int recordLength, bool bigKeys)
    {
        int keyLength = bigKeys
            ? ByteUtils.U16BE(node, recordOffset)
            : node[recordOffset];
        int skip = (bigKeys ? 2 : 1) + keyLength;
        if ((skip & 1) != 0) skip++;                 // padding al confine dei 2 byte
        if (skip < 0 || skip > recordLength) return -1;
        return recordOffset + skip;
    }

    /// <summary>
    /// Scorre tutte le foglie: prima la catena firstLeaf -&gt; fLink, poi (per robustezza)
    /// i nodi marcati in uso che la catena non ha toccato.
    /// </summary>
    private void ForEachLeaf(BTree t, string label, bool includeFree,
                             Action<byte[], List<(int off, int len)>, bool> visit,
                             CancellationToken ct)
    {
        var node = new byte[t.NodeSize];
        var visited = new HashSet<uint>();
        int carved = 0;

        uint n = t.FirstLeaf;
        int guard = 0;
        while (n != 0 && guard++ <= t.TotalNodes + 1)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(n)) { Notes.Add($"B-tree {label}: catena delle foglie con un anello, interrotta."); break; }
            if (!TryReadNode(t, n, node)) { Notes.Add($"B-tree {label}: nodo {n} illeggibile."); break; }
            if ((sbyte)node[8] != NodeLeaf) break;

            var recs = GetRecordSpans(node, t.NodeSize);
            if (recs != null)
            {
                visit(node, recs, false);
                if (includeFree)
                {
                    var leftovers = CarveResidualRecords(node, t.NodeSize, recs);
                    if (leftovers.Count > 0) { carved += leftovers.Count; visit(node, leftovers, true); }
                }
            }
            n = ByteUtils.U32BE(node, 0);
        }

        // recupero: foglie in uso non raggiunte dalla catena (fLink rotto)
        int orphanLeaves = 0;
        for (uint i = 1; i < t.TotalNodes; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (visited.Contains(i)) continue;
            bool free = t.InUse != null && i < t.InUse.Length && !t.InUse[i];
            if (free) continue;
            if (!TryReadNode(t, i, node)) continue;
            if ((sbyte)node[8] != NodeLeaf) continue;

            var recs = GetRecordSpans(node, t.NodeSize);
            if (recs == null) continue;
            visited.Add(i);
            orphanLeaves++;
            visit(node, recs, false);
        }
        if (orphanLeaves > 0)
            Notes.Add($"B-tree {label}: {orphanLeaves} nodi foglia in uso non erano raggiungibili " +
                      "dalla catena dei fratelli (catena danneggiata), letti comunque.");

        if (!includeFree) return;

        // nodi liberi che contengono ancora record leggibili
        int freeLeaves = 0;
        for (uint i = 1; i < t.TotalNodes; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (visited.Contains(i)) continue;
            if (t.InUse != null && i < t.InUse.Length && t.InUse[i]) continue;
            if (!TryReadNode(t, i, node)) continue;
            if ((sbyte)node[8] != NodeLeaf) continue;

            var recs = GetRecordSpans(node, t.NodeSize);
            if (recs == null || recs.Count == 0) continue;
            freeLeaves++;
            visit(node, recs, true);
        }
        if (freeLeaves > 0)
            Notes.Add($"B-tree {label}: {freeLeaves} nodi liberi contenevano ancora record " +
                      "riconoscibili, letti come residui incerti.");
        if (carved > 0)
            Notes.Add($"B-tree {label}: {carved} record recuperati dallo spazio libero dentro i " +
                      "nodi in uso. Sono resti di cancellazioni, quindi vanno considerati incerti.");
    }

    /// <summary>
    /// Cerca record di catalogo riconoscibili nello spazio libero di un nodo foglia,
    /// cioe' fra la fine dell'ultimo record e l'inizio dell'array degli offset.
    /// HFS+ compatta il nodo quando cancella, quindi qui si trova solo la coda di
    /// quello che c'era prima: il risultato e' per forza parziale e incerto.
    /// </summary>
    private List<(int off, int len)> CarveResidualRecords(byte[] node, int nodeSize,
                                                          List<(int off, int len)> recs)
    {
        var found = new List<(int, int)>();

        int freeStart = 14;
        foreach (var (o, l) in recs) freeStart = Math.Max(freeStart, o + l);
        int freeEnd = nodeSize - 2 * (recs.Count + 1);
        if (freeEnd - freeStart < 100) return found;

        for (int p = (freeStart + 1) & ~1; p + 2 <= freeEnd - 90; p += 2)
        {
            int keyLength = ByteUtils.U16BE(node, p);
            if (keyLength < 6 || keyLength > 516 || (keyLength & 1) != 0) continue;

            int dataOff = p + 2 + keyLength;
            if (dataOff + 2 > freeEnd) continue;

            uint parentId = ByteUtils.U32BE(node, p + 2);
            if (!PlausibleCnid(parentId, true)) continue;

            int nameChars = ByteUtils.U16BE(node, p + 6);
            if (6 + nameChars * 2 != keyLength) continue;      // vincolo forte: chiave coerente

            ushort kind = ByteUtils.U16BE(node, dataOff);
            int need = kind == RecFolder ? 88 : kind == RecFile ? 248 : 0;
            if (need == 0 || dataOff + need > freeEnd) continue;

            uint cnid = ByteUtils.U32BE(node, dataOff + 8);
            if (!PlausibleCnid(cnid, true)) continue;

            found.Add((p, dataOff + need - p));
            p += dataOff + need - p - 2;                        // il for aggiunge 2
        }

        return found;
    }

    // ------------------------------------------------------- extents overflow

    private void LoadExtentsOverflow(CancellationToken ct)
    {
        // il file degli extent non puo' avere extent nell'overflow: i suoi 8 bastano
        var t = OpenBTree(_extentsFork, "extent overflow");
        if (t == null) return;
        if (t.LeafRecords == 0 && t.FirstLeaf == 0) return;

        int count = 0;
        ForEachLeaf(t, "extent overflow", false, (node, recs, residual) =>
        {
            foreach (var (off, len) in recs)
            {
                // HFSPlusExtentKey: keyLength(2) forkType(1) pad(1) fileID(4) startBlock(4)
                if (len < 12 + 64) continue;
                int keyLength = ByteUtils.U16BE(node, off);
                if (keyLength != 10) continue;

                byte forkType = node[off + 2];
                uint fileId = ByteUtils.U32BE(node, off + 4);
                if (fileId == 0) continue;

                int dataOff = off + 12;
                if (dataOff + 64 > off + len) continue;

                uint fileBlock = ByteUtils.U32BE(node, off + 8);

                var list = GetOrAdd(fileId, forkType);
                for (int i = 0; i < 8; i++)
                {
                    uint start = ByteUtils.U32BE(node, dataOff + i * 8);
                    uint blocks = ByteUtils.U32BE(node, dataOff + i * 8 + 4);
                    if (blocks == 0) continue;
                    list.Add(new BlockExtent(start, blocks) { FileBlock = fileBlock });
                    fileBlock += blocks;
                    count++;
                }
            }
        }, ct);

        // i nodi possono arrivare fuori ordine se la catena e' rotta: si riordina
        foreach (var list in _overflow.Values)
            list.Sort((a, b) => a.FileBlock.CompareTo(b.FileBlock));

        if (count > 0)
            Notes.Add($"Extents overflow: {count} extent aggiuntivi per i file con piu' di 8 frammenti.");
    }

    private List<BlockExtent> GetOrAdd(uint fileId, byte forkType)
    {
        long key = OverflowKey(fileId, forkType);
        if (!_overflow.TryGetValue(key, out var list))
        {
            list = new List<BlockExtent>();
            _overflow[key] = list;
        }
        return list;
    }

    // ------------------------------------------------------------- attributi

    private void LoadDecmpfsAttributes(CancellationToken ct)
    {
        if (_attributesFork == null || _attributesFork.Extents.Count == 0) return;

        var t = OpenBTree(_attributesFork, "attributi");
        if (t == null) return;
        if (t.LeafRecords == 0 && t.FirstLeaf == 0) return;

        ForEachLeaf(t, "attributi", false, (node, recs, residual) =>
        {
            foreach (var (off, len) in recs)
            {
                // HFSPlusAttrKey: keyLength(2) pad(2) fileID(4) startBlock(4) nameLen(2) name[]
                if (len < 14) continue;
                int keyLength = ByteUtils.U16BE(node, off);
                if (keyLength < 12 || 2 + keyLength > len) continue;

                uint fileId = ByteUtils.U32BE(node, off + 4);
                int nameChars = ByteUtils.U16BE(node, off + 12);
                if (nameChars < 0 || nameChars > 127) continue;
                if (14 + nameChars * 2 > 2 + keyLength) continue;

                string name = ByteUtils.Utf16BE(node, off + 14, nameChars * 2);
                if (!string.Equals(name, DecmpfsName, StringComparison.Ordinal)) continue;

                int dataOff = KeyDataOffset(node, off, len, true);
                if (dataOff < 0 || dataOff + 16 > off + len) continue;

                // HFSPlusAttrData: recordType(4) reserved(8) size(4) data[]
                uint recordType = ByteUtils.U32BE(node, dataOff);
                if (recordType != AttrInlineData) continue;

                int size = (int)ByteUtils.U32BE(node, dataOff + 12);
                int available = off + len - (dataOff + 16);
                if (size <= 0 || size > available) size = Math.Max(0, available);
                if (size < DecmpfsHeaderSize) continue;

                var payload = new byte[size];
                Array.Copy(node, dataOff + 16, payload, 0, size);
                _decmpfs[fileId] = payload;
            }
        }, ct);
    }

    // --------------------------------------------------------------- catalogo

    /// <summary>Dati specifici HFS+ agganciati a ogni FsEntry.</summary>
    public sealed class EntryInfo
    {
        public uint Cnid;
        public uint ParentId;
        /// <summary>Nome come sta su disco, prima di normalizzazione e sostituzioni.</summary>
        public string RawName = "";
        public bool IsResourceFork;
        public long ResourceForkSize;
        /// <summary>
        /// Solo sulle voci recuperate: i blocchi risultano di nuovo allocati nella
        /// bitmap, quindi il contenuto e' con ogni probabilita' gia' sovrascritto.
        /// </summary>
        public bool SpaceReused;
        /// <summary>Tipo decmpfs (0 = non compresso).</summary>
        public uint CompressionType;
        public byte[] DecmpfsPayload;
        /// <summary>Extent del resource fork, usati per la compressione di tipo pari.</summary>
        public List<FsExtent> ResourceExtents;
        public uint FinderType;
        public uint FinderCreator;
        public DateTime? Accessed;
        public DateTime? Backup;
    }

    private void ScanCatalog(bool includeDeleted, CancellationToken ct)
    {
        var t = OpenBTree(_catalogFork, "catalogo");
        if (t == null)
        {
            Notes.Add("Il B-tree del catalogo non ha un nodo header valido: nessun file recuperabile " +
                      "dalle strutture. Resta possibile solo il carving dei dati grezzi.");
            return;
        }

        Notes.Add($"Catalogo: nodi da {t.NodeSize} byte, {t.TotalNodes} totali ({t.FreeNodes} liberi), " +
                  $"profondita' {t.Depth}, {t.LeafRecords} record dichiarati" +
                  (t.KeyCompareType == 0xBC ? ", confronto binario (case sensitive)." : "."));

        var folders = new Dictionary<uint, FsEntry>();
        var byParent = new Dictionary<uint, List<FsEntry>>();
        var threads = new Dictionary<uint, (uint parent, string name)>();
        var seenCnid = new HashSet<uint>();

        int files = 0, dirs = 0, residuals = 0, skipped = 0;

        void Register(FsEntry e, uint parent)
        {
            if (!byParent.TryGetValue(parent, out var list))
            {
                list = new List<FsEntry>();
                byParent[parent] = list;
            }
            list.Add(e);
        }

        ForEachLeaf(t, "catalogo", includeDeleted, (node, recs, residual) =>
        {
            foreach (var (off, len) in recs)
            {
                ct.ThrowIfCancellationRequested();
                var parsed = ParseCatalogRecord(node, off, len, residual);
                if (parsed == null) { skipped++; continue; }

                var (entry, parentId, cnid, kind) = parsed.Value;

                if (kind == RecFolderThread || kind == RecFileThread)
                {
                    if (!residual && cnid != 0 && !threads.ContainsKey(cnid))
                        threads[cnid] = (parentId, entry?.Name ?? "");
                    continue;
                }

                if (entry == null) { skipped++; continue; }

                if (cnid != 0 && !seenCnid.Add(cnid))
                {
                    // un duplicato vero e' sospetto: lo si tiene solo come residuo
                    if (!residual) { skipped++; continue; }
                    entry.IsDeleted = true;
                    entry.IsUncertain = true;
                }

                if (residual) { entry.IsDeleted = true; entry.IsUncertain = true; residuals++; }

                if (entry.IsDirectory)
                {
                    if (!entry.IsDeleted && cnid != 0) folders[cnid] = entry;
                    // il record della radice sta sotto il CNID 1 e non e' una cartella in piu'
                    if (parentId != CnidRootParent) dirs++;
                }
                else files++;

                Register(entry, parentId);

                // il resource fork diventa una voce a parte, tranne quando contiene
                // solo i blocchi decmpfs gia' espansi nel file stesso (tipo 4)
                if (entry.Tag is EntryInfo info && info.ResourceForkSize > 0 &&
                    !info.IsResourceFork && info.CompressionType != 4)
                    Register(BuildResourceEntry(entry, info), parentId);
            }
        }, ct);

        // aggancio dei figli ai genitori
        var root = Root;
        folders[CnidRoot] = root;

        // etichetta del volume: chiave del record della radice (parent 1)
        if (threads.TryGetValue(CnidRoot, out var rootThread) && !string.IsNullOrEmpty(rootThread.name))
            VolumeLabel = rootThread.name;
        else if (byParent.TryGetValue(CnidRootParent, out var rootKeyed) && rootKeyed.Count > 0)
            VolumeLabel = rootKeyed[0].Name;
        if (string.IsNullOrEmpty(VolumeLabel)) VolumeLabel = _wrapperLabel;

        FsEntry orphanBin = null;
        int orphans = 0;

        foreach (var pair in byParent)
        {
            uint parentId = pair.Key;

            // la voce della radice sta sotto il CNID 1: il suo contenuto e' gia' Root
            if (parentId == CnidRootParent) continue;

            if (!folders.TryGetValue(parentId, out var parent))
            {
                if (orphanBin == null)
                {
                    orphanBin = new FsEntry { IsDirectory = true, Name = "[voci orfane]", IsUncertain = true };
                    AddChild(root, orphanBin);
                }
                parent = orphanBin;
                orphans += pair.Value.Count;
            }

            foreach (var e in pair.Value)
            {
                if (ReferenceEquals(e, parent)) continue;
                if (parent == orphanBin) e.IsUncertain = true;
                AddChild(parent, e);
            }
        }

        // le cartelle create e mai agganciate finiscono anch'esse fra gli orfani
        foreach (var f in folders.Values)
        {
            if (ReferenceEquals(f, root) || f.Parent != null) continue;
            if (orphanBin == null)
            {
                orphanBin = new FsEntry { IsDirectory = true, Name = "[voci orfane]", IsUncertain = true };
                AddChild(root, orphanBin);
            }
            f.IsUncertain = true;
            AddChild(orphanBin, f);
            orphans++;
        }

        if (residuals > 0) CheckDeletedAgainstBitmap(ct);

        FixPaths(root, ct);

        Notes.Add($"Dal catalogo: {files} file e {dirs} cartelle" +
                  (residuals > 0 ? $", piu' {residuals} record residui recuperati dai nodi liberi" : "") +
                  (skipped > 0 ? $"; {skipped} record scartati perche' incoerenti" : "") + ".");

        if (orphans > 0)
            Notes.Add($"{orphans} voci non avevano una cartella genitore nel catalogo: " +
                      "sono state raccolte in \"[voci orfane]\".");

        if (files != _fileCount && _fileCount > 0 && residuals == 0)
            Notes.Add($"Attenzione: l'header dichiara {_fileCount} file ma nel catalogo ne sono stati " +
                      $"letti {files}: il B-tree e' incompleto.");
    }

    /// <summary>
    /// Per le voci recuperate dai record residui controlla la bitmap di allocazione:
    /// se i blocchi risultano di nuovo occupati, lo spazio e' gia' stato riusato e i
    /// dati sono con ogni probabilita' sovrascritti. Se invece sono liberi il
    /// contenuto e' quasi sempre ancora integro.
    /// </summary>
    private void CheckDeletedAgainstBitmap(CancellationToken ct)
    {
        if (_allocationFork == null || _allocationFork.Extents.Count == 0) return;

        int reused = 0, intact = 0;
        var probe = new byte[512];

        foreach (var e in EnumerateAll())
        {
            ct.ThrowIfCancellationRequested();
            if (!e.IsDeleted || e.IsDirectory || e.Extents.Count == 0) continue;

            bool anyAllocated = false;
            foreach (var x in e.Extents)
            {
                long firstBlock = (x.Offset - _base) / _blockSize;
                long lastBlock = (x.Offset - _base + x.Length - 1) / _blockSize;
                if (firstBlock < 0) continue;

                for (long b = firstBlock; b <= lastBlock && !anyAllocated; b++)
                {
                    long byteIndex = b / 8;
                    long chunkStart = byteIndex & ~511L;
                    if (ReadFork(_allocationFork, chunkStart, probe.Length, probe, 0) <= 0) break;
                    int inChunk = (int)(byteIndex - chunkStart);
                    if (inChunk < 0 || inChunk >= probe.Length) break;
                    if (((probe[inChunk] >> (int)(7 - b % 8)) & 1) != 0) anyAllocated = true;
                }
                if (anyAllocated) break;
            }

            if (e.Tag is EntryInfo info) info.SpaceReused = anyAllocated;
            if (anyAllocated) reused++; else intact++;
        }

        if (reused > 0)
            Notes.Add($"{reused} voci recuperate occupano blocchi che risultano di nuovo " +
                      "allocati: quei dati sono quasi certamente gia' stati sovrascritti " +
                      $"({nameof(EntryInfo.SpaceReused)} nel Tag della voce).");
        if (intact > 0)
            Notes.Add($"{intact} voci recuperate puntano a blocchi ancora liberi: il contenuto " +
                      "ha buone probabilita' di essere ancora integro, ma non c'e' modo di " +
                      "verificarlo dalle strutture del filesystem.");
    }

    /// <summary>Ricalcola i percorsi completi ed evita gli anelli nelle parentele.</summary>
    private void FixPaths(FsEntry root, CancellationToken ct)
    {
        var stack = new Stack<FsEntry>();
        var seen = new HashSet<FsEntry>();
        stack.Push(root);
        seen.Add(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var e = stack.Pop();
            for (int i = e.Children.Count - 1; i >= 0; i--)
            {
                var c = e.Children[i];
                if (!seen.Add(c)) { e.Children.RemoveAt(i); continue; }
                c.Parent = e;
                c.FullPath = string.IsNullOrEmpty(e.FullPath) ? c.Name : e.FullPath + "/" + c.Name;
                stack.Push(c);
            }
        }
    }

    /// <summary>
    /// Decodifica un record del catalogo. Ritorna la voce (null per i thread),
    /// il parentID della chiave, il CNID e il tipo di record.
    /// </summary>
    private (FsEntry entry, uint parentId, uint cnid, ushort kind)? ParseCatalogRecord(
        byte[] node, int off, int len, bool residual)
    {
        if (len < 10) return null;

        int keyLength = ByteUtils.U16BE(node, off);
        if (keyLength < 6 || keyLength > 516 || 2 + keyLength > len) return null;

        uint parentId = ByteUtils.U32BE(node, off + 2);
        int nameChars = ByteUtils.U16BE(node, off + 6);
        if (nameChars < 0 || nameChars > 255) return null;
        if (6 + 2 + nameChars * 2 > keyLength + 2) return null;

        string rawName = nameChars > 0 ? ByteUtils.Utf16BE(node, off + 8, nameChars * 2) : "";

        int dataOff = KeyDataOffset(node, off, len, true);
        if (dataOff < 0 || dataOff + 2 > off + len) return null;

        ushort kind = ByteUtils.U16BE(node, dataOff);
        int dataLen = off + len - dataOff;

        switch (kind)
        {
            case RecFolderThread:
            case RecFileThread:
            {
                if (dataLen < 10) return null;
                uint threadParent = ByteUtils.U32BE(node, dataOff + 4);
                int tnChars = ByteUtils.U16BE(node, dataOff + 8);
                if (tnChars < 0 || tnChars > 255) return null;
                if (10 + tnChars * 2 > dataLen) return null;
                string tn = tnChars > 0 ? ByteUtils.Utf16BE(node, dataOff + 10, tnChars * 2) : "";
                var stub = new FsEntry { Name = CleanName(tn) };
                return (stub, threadParent, parentId, kind);
            }

            case RecFolder:
            {
                if (dataLen < 88) return null;
                uint valence = ByteUtils.U32BE(node, dataOff + 4);
                uint cnid = ByteUtils.U32BE(node, dataOff + 8);
                if (!PlausibleCnid(cnid, residual)) return null;
                if (residual && valence > 1_000_000) return null;

                var e = new FsEntry
                {
                    IsDirectory = true,
                    Name = CleanName(rawName),
                    Created = HfsLocalDate(ByteUtils.U32BE(node, dataOff + 12)),
                    Modified = ByteUtils.HfsTime(ByteUtils.U32BE(node, dataOff + 16)),
                    Tag = new EntryInfo
                    {
                        Cnid = cnid,
                        ParentId = parentId,
                        RawName = rawName,
                        Accessed = ByteUtils.HfsTime(ByteUtils.U32BE(node, dataOff + 24)),
                        Backup = ByteUtils.HfsTime(ByteUtils.U32BE(node, dataOff + 28)),
                    }
                };
                if (string.IsNullOrEmpty(e.Name)) e.Name = $"cartella_{cnid}";
                return (e, parentId, cnid, kind);
            }

            case RecFile:
            {
                if (dataLen < 248) return null;
                uint cnid = ByteUtils.U32BE(node, dataOff + 8);
                if (!PlausibleCnid(cnid, residual)) return null;

                var data = ReadForkData(node, dataOff + 88, cnid, ForkTypeData);
                var rsrc = ReadForkData(node, dataOff + 168, cnid, ForkTypeResource);
                Complete(data, cnid, ForkTypeData);
                Complete(rsrc, cnid, ForkTypeResource);

                var info = new EntryInfo
                {
                    Cnid = cnid,
                    ParentId = parentId,
                    RawName = rawName,
                    ResourceForkSize = rsrc.LogicalSize,
                    FinderType = ByteUtils.U32BE(node, dataOff + 48),
                    FinderCreator = ByteUtils.U32BE(node, dataOff + 52),
                    Accessed = ByteUtils.HfsTime(ByteUtils.U32BE(node, dataOff + 24)),
                    Backup = ByteUtils.HfsTime(ByteUtils.U32BE(node, dataOff + 28)),
                    ResourceExtents = ToByteExtents(rsrc),
                };

                var e = new FsEntry
                {
                    IsDirectory = false,
                    Name = CleanName(rawName),
                    Length = data.LogicalSize,
                    Created = HfsLocalDate(ByteUtils.U32BE(node, dataOff + 12)),
                    Modified = ByteUtils.HfsTime(ByteUtils.U32BE(node, dataOff + 16)),
                    Extents = ToByteExtents(data),
                    Tag = info,
                };
                if (string.IsNullOrEmpty(e.Name)) e.Name = $"file_{cnid}";

                if (data.Truncated || data.Clipped)
                    e.IsUncertain = true;

                ApplyCompression(e, info, data);
                return (e, parentId, cnid, kind);
            }

            default:
                return null;
        }
    }

    private bool PlausibleCnid(uint cnid, bool strict)
    {
        if (cnid == 0) return false;
        if (!strict) return true;
        // sui record residui si e' piu' esigenti, altrimenti si raccoglie rumore
        if (cnid < CnidRoot) return false;
        if (_nextCatalogId > 0 && cnid > _nextCatalogId + 1024) return false;
        return true;
    }

    private FsEntry BuildResourceEntry(FsEntry file, EntryInfo info)
    {
        var rinfo = new EntryInfo
        {
            Cnid = info.Cnid,
            ParentId = info.ParentId,
            RawName = info.RawName,
            IsResourceFork = true,
            ResourceForkSize = info.ResourceForkSize,
        };

        return new FsEntry
        {
            IsDirectory = false,
            Name = file.Name + " [resource fork]",
            Length = info.ResourceForkSize,
            Created = file.Created,
            Modified = file.Modified,
            Extents = info.ResourceExtents != null ? new List<FsExtent>(info.ResourceExtents) : new List<FsExtent>(),
            IsDeleted = file.IsDeleted,
            IsUncertain = file.IsUncertain,
            Tag = rinfo,
        };
    }

    private List<FsExtent> ToByteExtents(ForkInfo f)
    {
        var list = new List<FsExtent>();
        if (f == null || f.Extents.Count == 0) return list;

        long remaining = f.LogicalSize;
        bool clip = remaining > 0;

        foreach (var e in f.Extents)
        {
            long length = (long)e.Count * _blockSize;
            if (clip)
            {
                if (remaining <= 0) break;
                if (length > remaining) length = remaining;
                remaining -= length;
            }

            long offset = _base + (long)e.Start * _blockSize;

            if (list.Count > 0)
            {
                var last = list[^1];
                if (last.Offset + last.Length == offset)
                {
                    last.Length += length;
                    list[^1] = last;
                    continue;
                }
            }
            list.Add(new FsExtent(offset, length));
        }

        return list;
    }

    // ------------------------------------------------------------- compressione

    private void ApplyCompression(FsEntry e, EntryInfo info, ForkInfo data)
    {
        if (!_decmpfs.TryGetValue(info.Cnid, out var payload) || payload == null) return;
        if (payload.Length < DecmpfsHeaderSize) return;

        // l'intestazione decmpfs e' little endian (la scrive lo strato VFS, non HFS+)
        uint magic = ByteUtils.U32LE(payload, 0);
        if (magic != DecmpfsMagic) return;

        uint type = ByteUtils.U32LE(payload, 4);
        long size = (long)ByteUtils.U64LE(payload, 8);
        if (size < 0) return;

        info.CompressionType = type;
        info.DecmpfsPayload = payload;
        e.Length = size;
        e.Extents = new List<FsExtent>();     // i dati non stanno nel data fork

        switch (type)
        {
            case 1:   // dati non compressi, dentro l'attributo
            case 3:   // zlib dentro l'attributo
            case 4:   // zlib nel resource fork
                break;
            default:
                e.IsUncertain = true;
                NoteOnce($"Compressione HFS+ (decmpfs) di tipo {type} non supportata: i file " +
                         "interessati risultano di dimensione corretta ma il contenuto non e' " +
                         "decomprimibile da questo driver. Il tipo 3 (zlib nell'attributo) e il " +
                         "tipo 4 (zlib nel resource fork) invece si leggono.");
                return;
        }

        if (size > MaxDecompressedSize)
        {
            e.IsUncertain = true;
            NoteOnce($"Almeno un file compresso supera {MaxDecompressedSize / 1048576} MB una volta " +
                     "espanso: non viene decompresso.");
        }

        NoteOnce("Sul volume ci sono file compressi con decmpfs (com.apple.decmpfs): il data fork " +
                 "e' vuoto e il contenuto vero sta nell'attributo esteso o nel resource fork.");
    }

    public override long Extract(FsEntry entry, Stream destination, CancellationToken ct)
    {
        if (entry == null || entry.IsDirectory || destination == null) return 0;

        if (entry.Tag is EntryInfo info && info.CompressionType != 0 && !info.IsResourceFork)
        {
            try { return ExtractCompressed(entry, info, destination, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                NoteOnce("Decompressione decmpfs fallita su almeno un file: " + Short(ex));
                return 0;
            }
        }

        return base.Extract(entry, destination, ct);
    }

    private long ExtractCompressed(FsEntry entry, EntryInfo info, Stream destination, CancellationToken ct)
    {
        var payload = info.DecmpfsPayload;
        if (payload == null || payload.Length < DecmpfsHeaderSize) return 0;
        if (entry.Length > MaxDecompressedSize) return 0;

        switch (info.CompressionType)
        {
            case 1:
            {
                int n = payload.Length - DecmpfsHeaderSize;
                if (entry.Length > 0 && entry.Length < n) n = (int)entry.Length;
                if (n <= 0) return 0;
                destination.Write(payload, DecmpfsHeaderSize, n);
                return n;
            }

            case 3:
                return InflateInto(payload, DecmpfsHeaderSize, payload.Length - DecmpfsHeaderSize,
                                   destination, entry.Length, ct);

            case 4:
                return ExtractCompressedResource(entry, info, destination, ct);

            default:
                return 0;
        }
    }

    /// <summary>
    /// Espande un blocco decmpfs. Il primo byte 0x78 e' l'intestazione zlib;
    /// se i 4 bit bassi valgono 0xF il blocco e' gia' in chiaro (zlib l'avrebbe gonfiato).
    /// </summary>
    private static long InflateInto(byte[] src, int offset, int count, Stream destination,
                                    long expected, CancellationToken ct)
    {
        if (count <= 0) return 0;

        if ((src[offset] & 0x0F) == 0x0F)
        {
            int n = count - 1;
            if (expected > 0 && expected < n) n = (int)expected;
            if (n <= 0) return 0;
            destination.Write(src, offset + 1, n);
            return n;
        }

        long written = 0;
        using var input = new MemoryStream(src, offset, count, false);
        using var z = new ZLibStream(input, CompressionMode.Decompress, true);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = z.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            if (expected > 0 && written + read > expected) read = (int)(expected - written);
            if (read <= 0) break;
            destination.Write(buffer, 0, read);
            written += read;
        }
        return written;
    }

    /// <summary>
    /// decmpfs tipo 4: i blocchi compressi stanno nel resource fork, in una risorsa 'cmpf'.
    /// Struttura: intestazione resource fork (16 byte BE), a 0x100 la lunghezza dei dati (BE),
    /// a 0x104 il numero di blocchi (LE) seguito dalla tabella (offset, dimensione) LE,
    /// con offset relativi a 0x104. Ogni blocco espande a 64 KiB.
    /// Formato documentato ma non verificabile qui su un file prodotto da macOS.
    /// </summary>
    private long ExtractCompressedResource(FsEntry entry, EntryInfo info, Stream destination,
                                           CancellationToken ct)
    {
        var extents = info.ResourceExtents;
        if (extents == null || extents.Count == 0) return 0;

        long forkSize = info.ResourceForkSize;
        if (forkSize <= 0x108 || forkSize > 64L * 1024 * 1024) return 0;

        var fork = new byte[forkSize];
        ReadExtents(extents, fork);

        uint dataOffset = ByteUtils.U32BE(fork, 0);
        if (dataOffset + 8 > fork.Length) dataOffset = 0x100;
        long tableBase = dataOffset + 4;
        if (tableBase + 4 > fork.Length) return 0;

        uint blocks = ByteUtils.U32LE(fork, (int)tableBase);
        if (blocks == 0 || blocks > 1_000_000) return 0;
        if (tableBase + 4 + (long)blocks * 8 > fork.Length) return 0;

        long written = 0;
        for (uint i = 0; i < blocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            int p = (int)(tableBase + 4 + i * 8);
            long blockOffset = tableBase + ByteUtils.U32LE(fork, p);
            int blockSize = (int)ByteUtils.U32LE(fork, p + 4);
            if (blockOffset < 0 || blockSize <= 0 || blockOffset + blockSize > fork.Length) break;

            long want = entry.Length - written;
            if (want <= 0) break;
            if (want > DecmpfsBlockSize) want = DecmpfsBlockSize;

            written += InflateInto(fork, (int)blockOffset, blockSize, destination, want, ct);
        }

        return written;
    }

    private void ReadExtents(List<FsExtent> extents, byte[] dest)
    {
        int done = 0;
        foreach (var e in extents)
        {
            if (done >= dest.Length) break;
            int chunk = (int)Math.Min(e.Length, dest.Length - done);
            if (chunk <= 0) continue;
            Source.ReadBytes(e.Offset, chunk, dest, done);
            done += chunk;
        }
    }

    // ------------------------------------------------------------------ utilita'

    /// <summary>
    /// Nome da HFS+: si ricompongono gli accenti (su disco sono decomposti) e si
    /// riporta ':' a '/', che e' il verso usato da macOS fra strato Mac e strato POSIX.
    /// Il nome vero resta in <see cref="EntryInfo.RawName"/>; la ripulitura per
    /// Windows avviene solo in estrazione, con ByteUtils.SafeFileName.
    /// </summary>
    private string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        string s = raw;
        try
        {
            if (!s.IsNormalized(NormalizationForm.FormC))
            {
                s = s.Normalize(NormalizationForm.FormC);
                _namesNormalized++;
            }
        }
        catch { s = raw; }

        if (s.IndexOf(':') >= 0) s = s.Replace(':', '/');

        // i caratteri di controllo residui rendono il nome illeggibile
        if (s.IndexOf('\0') >= 0) s = s.Replace("\0", "");

        return s;
    }

    /// <summary>Nome pronto da scrivere su disco Windows.</summary>
    public static string SafeNameFor(FsEntry entry)
        => ByteUtils.SafeFileName(entry?.Name, "senza_nome");

    /// <summary>
    /// Percorso relativo utilizzabile su Windows: ogni segmento passa da
    /// ByteUtils.SafeFileName. Da usare in estrazione, non per identificare la voce.
    /// </summary>
    public static string SafeRelativePath(FsEntry entry)
    {
        if (entry == null) return "senza_nome";

        var parts = new List<string>();
        for (var e = entry; e != null && !string.IsNullOrEmpty(e.Name); e = e.Parent)
            parts.Add(ByteUtils.SafeFileName(e.Name, "senza_nome"));
        parts.Reverse();
        return parts.Count == 0 ? "senza_nome" : string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>createDate del volume header e' in ora locale, non UTC (TN1150).</summary>
    private static DateTime? HfsLocalDate(uint value)
    {
        if (value == 0) return null;
        try { return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddSeconds(value); }
        catch { return null; }
    }

    private static string MacRomanPascal(byte[] b, int off, int max)
    {
        if (b == null || off < 0 || off + max > b.Length) return "";
        int len = b[off];
        if (len <= 0 || len > max - 1) return "";
        return ByteUtils.Latin1(b, off + 1, len);
    }

    private void NoteOnce(string note)
    {
        if (!Notes.Contains(note)) Notes.Add(note);
    }

    private static string Short(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}
