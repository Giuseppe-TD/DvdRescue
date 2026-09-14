using System.Text;
using DVDRescue.Core;

namespace DVDRescue.FileSystems;

/// <summary>
/// Driver in sola lettura per la famiglia ext2/ext3/ext4.
/// Legge superblocco, tabella dei descrittori di gruppo (32 e 64 byte), inode,
/// entrambi gli schemi di mappatura dei blocchi (puntatori indiretti e albero degli extent),
/// directory lineari e con indice htree, inline data e symlink brevi.
/// Sa inoltre recuperare i file cancellati dagli inode orfani e dalle directory entry morte.
///
/// La sorgente passata al costruttore e' il VOLUME: offset 0 = primo byte del filesystem.
/// Gli extent prodotti sono in byte assoluti rispetto a quella sorgente.
/// </summary>
public sealed class ExtFileSystem : FileSystemBase
{
    // ---------------------------------------------------------------- costanti

    private const int SuperblockOffset = 1024;
    private const ushort Magic = 0xEF53;
    private const ushort ExtentMagic = 0xF30A;

    // feature_compat
    private const uint COMPAT_HAS_JOURNAL = 0x0004;

    // feature_incompat
    private const uint INCOMPAT_COMPRESSION = 0x0001;
    private const uint INCOMPAT_FILETYPE = 0x0002;
    private const uint INCOMPAT_RECOVER = 0x0004;
    private const uint INCOMPAT_JOURNAL_DEV = 0x0008;
    private const uint INCOMPAT_META_BG = 0x0010;
    private const uint INCOMPAT_EXTENTS = 0x0040;
    private const uint INCOMPAT_64BIT = 0x0080;
    private const uint INCOMPAT_MMP = 0x0100;
    private const uint INCOMPAT_FLEX_BG = 0x0200;
    private const uint INCOMPAT_EA_INODE = 0x0400;
    private const uint INCOMPAT_DIRDATA = 0x1000;
    private const uint INCOMPAT_CSUM_SEED = 0x2000;
    private const uint INCOMPAT_LARGEDIR = 0x4000;
    private const uint INCOMPAT_INLINE_DATA = 0x8000;
    private const uint INCOMPAT_ENCRYPT = 0x10000;
    private const uint INCOMPAT_CASEFOLD = 0x20000;

    // feature_ro_compat
    private const uint RO_SPARSE_SUPER = 0x0001;
    private const uint RO_LARGE_FILE = 0x0002;
    private const uint RO_BTREE_DIR = 0x0004;
    private const uint RO_HUGE_FILE = 0x0008;
    private const uint RO_GDT_CSUM = 0x0010;
    private const uint RO_DIR_NLINK = 0x0020;
    private const uint RO_EXTRA_ISIZE = 0x0040;
    private const uint RO_HAS_SNAPSHOT = 0x0080;
    private const uint RO_QUOTA = 0x0100;
    private const uint RO_BIGALLOC = 0x0200;
    private const uint RO_METADATA_CSUM = 0x0400;
    private const uint RO_REPLICA = 0x0800;
    private const uint RO_READONLY = 0x1000;
    private const uint RO_PROJECT = 0x2000;
    private const uint RO_VERITY = 0x8000;

    // i_flags
    private const uint FL_COMPR = 0x00000004;
    private const uint FL_ENCRYPT = 0x00000800;
    private const uint FL_INDEX = 0x00001000;   // directory con htree
    private const uint FL_EXTENTS = 0x00080000;
    private const uint FL_EA_INODE = 0x00200000;
    private const uint FL_INLINE_DATA = 0x10000000;
    private const uint FL_VERITY = 0x00100000;

    // i_mode, parte alta (gli altri valori - fifo 0x1000, chr 0x2000, blk 0x6000,
    // socket 0xC000 - non hanno contenuto da estrarre e vengono solo elencati)
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort S_IFREG = 0x8000;
    private const ushort S_IFLNK = 0xA000;

    private const int RootInode = 2;

    /// <summary>Offset convenzionale di un extent che rappresenta un buco (file sparso): produce zeri.</summary>
    public const long HoleOffset = -1;

    private const int MaxTreeDepth = 64;      // profondita' massima dell'albero di directory
    private const int MaxExtentDepth = 5;     // profondita' massima dell'albero degli extent
    private const int MaxEntriesPerDir = 2_000_000;

    // ---------------------------------------------------------------- stato

    private struct GroupDesc
    {
        public long BlockBitmap;
        public long InodeBitmap;
        public long InodeTable;
        public uint FreeInodes;
        public ushort Flags;
        public bool Valid;
    }

    private struct BlockRun
    {
        public long Logical;
        public long Physical;
        public long Count;
    }

    /// <summary>Dati grezzi dell'inode, allegati a <see cref="FsEntry.Tag"/>.</summary>
    public sealed class ExtInodeInfo
    {
        public uint Inode;
        public ushort Mode;
        public uint Flags;
        public ushort LinksCount;
        public uint Uid;
        public uint Gid;
        public DateTime? Accessed;
        public DateTime? Changed;
        public DateTime? Deleted;
        public bool UsesExtents;
        public bool InlineData;
        public bool FastSymlink;
        public string SymlinkTarget;
        public override string ToString() => $"inode {Inode}";
    }

    private int _blockSize = 1024;
    private int _inodeSize = 128;
    private uint _inodesPerGroup;
    private uint _blocksPerGroup;
    private uint _inodesCount;
    private long _blocksCount;
    private uint _firstDataBlock;
    private uint _firstInode = 11;
    private uint _featCompat, _featIncompat, _featRoCompat;
    private uint _revLevel;
    private int _descSize = 32;
    private uint _firstMetaBg;
    private bool _is64Bit;
    private bool _hasFileType;
    private GroupDesc[] _groups = Array.Empty<GroupDesc>();
    private string _typeName = "ext2";
    private bool _valid;
    private readonly int _noteIniziali;

    /// <summary>UUID del volume in formato canonico.</summary>
    public string Uuid { get; private set; } = "";

    /// <summary>Dimensione del blocco del filesystem (non del supporto).</summary>
    public int BlockSize => _blockSize;

    /// <summary>Numero di gruppi di blocchi.</summary>
    public int GroupCount => _groups.Length;

    public override string TypeName => _typeName;

    // ---------------------------------------------------------------- costruzione

    public ExtFileSystem(IBlockSource source) : base(source)
    {
        try { LeggiSuperblocco(); }
        catch (Exception ex)
        {
            Notes.Add("Superblocco illeggibile: " + ex.Message);
            _valid = false;
        }
        _noteIniziali = Notes.Count;
    }

    /// <summary>Riconosce un volume ext dal magic 0xEF53 a offset 1024+56.</summary>
    public static bool Detect(IBlockSource source)
    {
        if (source == null) return false;
        try
        {
            var sb = new byte[1024];
            if (source.ReadBytes(SuperblockOffset, 1024, sb, 0) < 128) return false;
            if (ByteUtils.U16LE(sb, 56) != Magic) return false;

            // controlli minimi di sanita': senza questi un blocco casuale puo' contenere 0xEF53
            uint logBlockSize = ByteUtils.U32LE(sb, 24);
            if (logBlockSize > 16) return false;
            uint inodesPerGroup = ByteUtils.U32LE(sb, 40);
            uint blocksPerGroup = ByteUtils.U32LE(sb, 32);
            if (inodesPerGroup == 0 || blocksPerGroup == 0) return false;
            if (ByteUtils.U32LE(sb, 0) == 0) return false;   // s_inodes_count
            return true;
        }
        catch { return false; }
    }

    private void LeggiSuperblocco()
    {
        var sb = Read(SuperblockOffset, 1024);
        if (ByteUtils.U16LE(sb, 56) != Magic)
        {
            Notes.Add("Magic ext (0xEF53) non trovato a offset 1080: il volume non e' ext2/3/4.");
            _valid = false;
            return;
        }

        _inodesCount = ByteUtils.U32LE(sb, 0);
        long blocksLo = ByteUtils.U32LE(sb, 4);
        _firstDataBlock = ByteUtils.U32LE(sb, 20);
        uint logBlockSize = ByteUtils.U32LE(sb, 24);
        _blocksPerGroup = ByteUtils.U32LE(sb, 32);
        _inodesPerGroup = ByteUtils.U32LE(sb, 40);
        _revLevel = ByteUtils.U32LE(sb, 76);

        if (logBlockSize > 16) logBlockSize = 0;
        _blockSize = 1024 << (int)logBlockSize;

        if (_revLevel >= 1)
        {
            _firstInode = ByteUtils.U32LE(sb, 84);
            _inodeSize = ByteUtils.U16LE(sb, 88);
            _featCompat = ByteUtils.U32LE(sb, 92);
            _featIncompat = ByteUtils.U32LE(sb, 96);
            _featRoCompat = ByteUtils.U32LE(sb, 100);
        }
        else
        {
            _inodeSize = 128;
            _firstInode = 11;
        }

        if (_inodeSize < 128 || _inodeSize > _blockSize || (_inodeSize & (_inodeSize - 1)) != 0)
        {
            Notes.Add($"Dimensione inode anomala ({_inodeSize} byte): uso 128.");
            _inodeSize = 128;
        }

        Uuid = FormattaUuid(sb, 104);
        VolumeLabel = LeggiNome(sb, 120, 16);

        _is64Bit = (_featIncompat & INCOMPAT_64BIT) != 0;
        _hasFileType = (_featIncompat & INCOMPAT_FILETYPE) != 0;

        _descSize = _is64Bit ? ByteUtils.U16LE(sb, 254) : 32;
        if (_descSize < 32) _descSize = 32;
        if (_descSize > _blockSize) _descSize = 32;
        if (_is64Bit && _descSize < 64)
        {
            // il flag 64bit richiede descrittori estesi; se s_desc_size e' incoerente lo correggo
            Notes.Add("Feature 64bit attiva ma s_desc_size < 64: forzo descrittori da 64 byte.");
            _descSize = 64;
        }

        _firstMetaBg = ByteUtils.U32LE(sb, 260);

        long blocksHi = _is64Bit ? ByteUtils.U32LE(sb, 336) : 0;
        _blocksCount = blocksLo | (blocksHi << 32);
        if (_blocksCount <= 0) _blocksCount = Math.Max(1, Source.Length / _blockSize);

        if (_blocksPerGroup == 0 || _inodesPerGroup == 0)
        {
            Notes.Add("Superblocco incoerente (blocchi o inode per gruppo a zero): scansione impossibile.");
            _valid = false;
            return;
        }

        _typeName = DeterminaTipo();
        _valid = true;

        AnnotaFeature();
        LeggiDescrittoriDiGruppo();
    }

    private string DeterminaTipo()
    {
        // criterio usato anche da blkid: se compaiono feature che ext2/ext3 non conoscono, e' ext4
        const uint ext3Incompat = INCOMPAT_FILETYPE | INCOMPAT_RECOVER | INCOMPAT_META_BG;
        const uint ext3RoCompat = RO_SPARSE_SUPER | RO_LARGE_FILE | RO_BTREE_DIR;

        bool oltreExt3 = (_featIncompat & ~ext3Incompat) != 0 || (_featRoCompat & ~ext3RoCompat) != 0;
        if (oltreExt3) return "ext4";
        if ((_featCompat & COMPAT_HAS_JOURNAL) != 0) return "ext3";
        return "ext2";
    }

    private void AnnotaFeature()
    {
        if ((_featIncompat & INCOMPAT_JOURNAL_DEV) != 0)
            Notes.Add("Il volume e' un dispositivo di journal esterno, non contiene file.");
        if ((_featCompat & COMPAT_HAS_JOURNAL) != 0)
            Notes.Add("Journal presente: non viene riprodotto, i dati letti sono quelli su disco. " +
                      "Se il filesystem non e' stato smontato correttamente qualche metadato puo' essere arretrato.");
        if ((_featIncompat & INCOMPAT_RECOVER) != 0)
            Notes.Add("Il filesystem richiede il replay del journal (flag recover): possibili incoerenze.");
        if ((_featIncompat & INCOMPAT_COMPRESSION) != 0)
            Notes.Add("Feature di compressione attiva: i dati compressi non vengono decompressi.");
        if ((_featIncompat & INCOMPAT_ENCRYPT) != 0)
            Notes.Add("Cifratura ext4 (encrypt) attiva: nomi e contenuti dei file cifrati non sono leggibili.");
        if ((_featIncompat & INCOMPAT_CASEFOLD) != 0)
            Notes.Add("Feature casefold attiva: i nomi vengono letti cosi' come sono memorizzati.");
        if ((_featIncompat & INCOMPAT_MMP) != 0)
            Notes.Add("Protezione MMP attiva (multi-mount): ignorata, la lettura e' comunque passiva.");
        if ((_featIncompat & INCOMPAT_DIRDATA) != 0)
            Notes.Add("Feature dirdata (dati nelle directory entry) non gestita: possibili nomi troncati.");
        if ((_featIncompat & INCOMPAT_EA_INODE) != 0)
            Notes.Add("Attributi estesi in inode dedicati (ea_inode): non vengono letti.");
        if ((_featIncompat & INCOMPAT_CSUM_SEED) != 0)
            Notes.Add("Seed dei checksum personalizzato: i checksum non vengono comunque verificati.");
        if ((_featRoCompat & RO_HAS_SNAPSHOT) != 0)
            Notes.Add("Snapshot presenti (has_snapshot): vengono ignorati, si legge solo il volume corrente.");
        if ((_featRoCompat & RO_BIGALLOC) != 0)
            Notes.Add("Feature bigalloc attiva: l'allocazione a cluster non e' gestita, " +
                      "la mappatura dei blocchi puo' risultare errata.");
        if ((_featRoCompat & RO_VERITY) != 0)
            Notes.Add("Feature verity attiva: i dati di verifica in coda ai file vengono ignorati.");
        if ((_featRoCompat & RO_QUOTA) != 0)
            Notes.Add("Quote nei file di sistema: gli inode di quota non vengono elencati.");
        if ((_featRoCompat & RO_REPLICA) != 0)
            Notes.Add("Feature replica attiva: non gestita.");
        if ((_featIncompat & INCOMPAT_META_BG) != 0)
            Notes.Add("Layout meta_bg attivo: i descrittori di gruppo vengono cercati nei meta-gruppi.");

        uint incNoti = INCOMPAT_COMPRESSION | INCOMPAT_FILETYPE | INCOMPAT_RECOVER | INCOMPAT_JOURNAL_DEV |
                       INCOMPAT_META_BG | INCOMPAT_EXTENTS | INCOMPAT_64BIT | INCOMPAT_MMP | INCOMPAT_FLEX_BG |
                       INCOMPAT_EA_INODE | INCOMPAT_DIRDATA | INCOMPAT_CSUM_SEED | INCOMPAT_LARGEDIR |
                       INCOMPAT_INLINE_DATA | INCOMPAT_ENCRYPT | INCOMPAT_CASEFOLD;
        uint roNoti = RO_SPARSE_SUPER | RO_LARGE_FILE | RO_BTREE_DIR | RO_HUGE_FILE | RO_GDT_CSUM |
                      RO_DIR_NLINK | RO_EXTRA_ISIZE | RO_HAS_SNAPSHOT | RO_QUOTA | RO_BIGALLOC |
                      RO_METADATA_CSUM | RO_REPLICA | RO_READONLY | RO_PROJECT | RO_VERITY;

        uint incIgnoti = _featIncompat & ~incNoti;
        uint roIgnoti = _featRoCompat & ~roNoti;
        if (incIgnoti != 0) Notes.Add($"Feature incompat sconosciute (0x{incIgnoti:X}): lettura a rischio.");
        if (roIgnoti != 0) Notes.Add($"Feature ro_compat sconosciute (0x{roIgnoti:X}): ignorate.");
    }

    private void LeggiDescrittoriDiGruppo()
    {
        long spanBlocchi = _blocksCount - _firstDataBlock;
        if (spanBlocchi < 0) spanBlocchi = 0;
        long nGruppi = (spanBlocchi + _blocksPerGroup - 1) / _blocksPerGroup;
        if (nGruppi <= 0) nGruppi = 1;
        if (nGruppi > 1_000_000)
        {
            Notes.Add($"Numero di gruppi assurdo ({nGruppi}): limito a 1.000.000.");
            nGruppi = 1_000_000;
        }

        _groups = new GroupDesc[nGruppi];
        int descPerBlocco = Math.Max(1, _blockSize / _descSize);
        bool metaBg = (_featIncompat & INCOMPAT_META_BG) != 0;

        byte[] bloccoCorrente = null;
        long bloccoCorrenteNum = -1;
        int danneggiati = 0;

        for (long g = 0; g < nGruppi; g++)
        {
            int indiceBlocco = (int)(g / descPerBlocco);
            int dentroBlocco = (int)(g % descPerBlocco);
            long blocco = PosizioneBloccoDescrittori(indiceBlocco, metaBg);

            if (blocco < 0 || blocco >= _blocksCount)
            {
                danneggiati++;
                continue;
            }

            if (blocco != bloccoCorrenteNum)
            {
                bloccoCorrente = LeggiBlocco(blocco);
                bloccoCorrenteNum = blocco;
            }
            if (bloccoCorrente == null) { danneggiati++; continue; }

            int off = dentroBlocco * _descSize;
            if (off + 32 > bloccoCorrente.Length) { danneggiati++; continue; }

            var gd = new GroupDesc();
            long bbLo = ByteUtils.U32LE(bloccoCorrente, off + 0);
            long ibLo = ByteUtils.U32LE(bloccoCorrente, off + 4);
            long itLo = ByteUtils.U32LE(bloccoCorrente, off + 8);
            gd.FreeInodes = ByteUtils.U16LE(bloccoCorrente, off + 14);
            gd.Flags = ByteUtils.U16LE(bloccoCorrente, off + 18);

            if (_descSize >= 64 && off + 64 <= bloccoCorrente.Length)
            {
                gd.BlockBitmap = bbLo | ((long)ByteUtils.U32LE(bloccoCorrente, off + 32) << 32);
                gd.InodeBitmap = ibLo | ((long)ByteUtils.U32LE(bloccoCorrente, off + 36) << 32);
                gd.InodeTable = itLo | ((long)ByteUtils.U32LE(bloccoCorrente, off + 40) << 32);
                gd.FreeInodes |= (uint)ByteUtils.U16LE(bloccoCorrente, off + 46) << 16;
            }
            else
            {
                gd.BlockBitmap = bbLo;
                gd.InodeBitmap = ibLo;
                gd.InodeTable = itLo;
            }

            gd.Valid = gd.InodeTable > 0 && gd.InodeTable < _blocksCount;
            if (!gd.Valid) danneggiati++;
            _groups[g] = gd;
        }

        if (danneggiati > 0)
            Notes.Add($"{danneggiati} descrittori di gruppo su {nGruppi} risultano illeggibili o incoerenti: " +
                      "gli inode di quei gruppi non saranno raggiungibili.");
    }

    /// <summary>
    /// Posizione del blocco <paramref name="indice"/> della tabella dei descrittori.
    /// Con meta_bg i descrittori stanno dentro il meta-gruppo che descrivono.
    /// </summary>
    private long PosizioneBloccoDescrittori(int indice, bool metaBg)
    {
        if (!metaBg || indice < _firstMetaBg)
            return _firstDataBlock + 1 + indice;

        int descPerBlocco = Math.Max(1, _blockSize / _descSize);
        long gruppo = (long)descPerBlocco * indice;
        if (gruppo >= _groups.LongLength && _groups.LongLength > 0) return -1;

        long primoBlocco = _firstDataBlock + gruppo * _blocksPerGroup;
        return primoBlocco + (GruppoHaSuperblocco(gruppo) ? 1 : 0);
    }

    /// <summary>Copia di riserva del superblocco: con sparse_super solo 0, 1 e le potenze di 3, 5, 7.</summary>
    private bool GruppoHaSuperblocco(long gruppo)
    {
        if ((_featRoCompat & RO_SPARSE_SUPER) == 0) return true;
        if (gruppo <= 1) return true;
        if ((gruppo & 1) == 0) return false;
        return PotenzaDi(gruppo, 3) || PotenzaDi(gruppo, 5) || PotenzaDi(gruppo, 7);
    }

    private static bool PotenzaDi(long n, int b)
    {
        while (n > 1)
        {
            if (n % b != 0) return false;
            n /= b;
        }
        return n == 1;
    }

    // ---------------------------------------------------------------- scansione

    private readonly HashSet<uint> _inodeVivi = new();
    private readonly List<VoceMorta> _vociMorte = new();
    private bool _notaSparso, _notaBlocchiFuoriRange, _notaExtentRotto, _notaInlineParziale, _notaFlagInode;
    private int _contaSymlinkBrevi, _contaSpeciali;

    private sealed class VoceMorta
    {
        public uint Inode;
        public string Nome;
        public FsEntry Genitore;
        public byte FileType;
    }

    public override void Scan(bool includeDeleted, CancellationToken ct)
    {
        Root = new FsEntry { IsDirectory = true, Name = "", FullPath = "" };
        // una seconda scansione non deve accodare di nuovo le stesse osservazioni
        if (Notes.Count > _noteIniziali) Notes.RemoveRange(_noteIniziali, Notes.Count - _noteIniziali);
        _inodeVivi.Clear();
        _vociMorte.Clear();
        _notaSparso = _notaBlocchiFuoriRange = _notaExtentRotto = _notaInlineParziale = false;
        _notaFlagInode = false;
        _contaSymlinkBrevi = _contaSpeciali = 0;

        if (!_valid)
        {
            Notes.Add("Superblocco non valido: nessuna scansione eseguita.");
            return;
        }
        if ((_featIncompat & INCOMPAT_JOURNAL_DEV) != 0) return;

        var radice = LeggiInodeGrezzo(RootInode);
        if (radice == null)
        {
            Notes.Add("Inode radice (2) illeggibile: provo comunque il recupero degli inode orfani.");
        }
        else
        {
            ushort mode = ByteUtils.U16LE(radice, 0);
            if ((mode & S_IFMT) != S_IFDIR)
                Notes.Add("L'inode radice non risulta una directory: struttura probabilmente danneggiata.");

            Root.Tag = CostruisciInfo(RootInode, radice);
            Root.Modified = ByteUtils.UnixTime(ByteUtils.U32LE(radice, 16));
            Root.Created = LeggiCrtime(radice);

            _inodeVivi.Add(RootInode);

            try
            {
                EsploraDirectory(Root, RootInode, radice, includeDeleted, 0, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Notes.Add("Errore durante la visita dell'albero: " + ex.Message);
            }
        }

        if (includeDeleted)
        {
            try { RecuperaCancellati(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Notes.Add("Errore durante il recupero dei cancellati: " + ex.Message); }
        }

        if (_contaSpeciali > 0)
            Notes.Add($"{_contaSpeciali} voci speciali (fifo, socket, dispositivi a caratteri o a blocchi): " +
                      "elencate ma prive di contenuto.");
        if (_contaSymlinkBrevi > 0)
            Notes.Add($"{_contaSymlinkBrevi} collegamenti simbolici brevi: il percorso di destinazione e' " +
                      "memorizzato dentro l'inode e viene restituito come contenuto del file " +
                      "(non il file puntato).");
        if (_notaSparso)
            Notes.Add("Trovati file sparsi: i buchi vengono restituiti come zeri " +
                      "(extent con offset -1 nella lista).");
        if (_notaBlocchiFuoriRange)
            Notes.Add("Alcuni puntatori a blocco puntano fuori dal volume: trattati come buchi.");
        if (_notaExtentRotto)
            Notes.Add("Alcuni nodi dell'albero degli extent hanno magic errato: rami saltati, file incompleti.");
    }

    private void EsploraDirectory(FsEntry nodo, uint ino, byte[] inode, bool includeDeleted, int livello,
                                  CancellationToken ct)
    {
        if (livello > MaxTreeDepth)
        {
            Notes.Add($"Profondita' massima ({MaxTreeDepth}) raggiunta in {nodo.FullPath}: ramo troncato.");
            return;
        }
        ct.ThrowIfCancellationRequested();

        var figli = LeggiVociDirectory(ino, inode, includeDeleted, nodo, ct);
        if (figli == null) return;

        foreach (var (nomeFiglio, inoFiglio, _) in figli)
        {
            ct.ThrowIfCancellationRequested();

            if (nomeFiglio == "." || nomeFiglio == "..") continue;
            if (inoFiglio == 0 || inoFiglio > _inodesCount) continue;

            var raw = LeggiInodeGrezzo(inoFiglio);
            if (raw == null) continue;

            ushort mode = ByteUtils.U16LE(raw, 0);
            if (mode == 0) continue;

            bool dir = (mode & S_IFMT) == S_IFDIR;

            if (_inodeVivi.Contains(inoFiglio))
            {
                // hard link (o ciclo su directory danneggiate): aggiungo la voce ma non riscendo
                if (dir) continue;
                var alias = CostruisciVoce(inoFiglio, raw, nomeFiglio, false);
                if (alias != null) AddChild(nodo, alias);
                continue;
            }

            _inodeVivi.Add(inoFiglio);

            var voce = CostruisciVoce(inoFiglio, raw, nomeFiglio, false);
            if (voce == null) continue;
            AddChild(nodo, voce);

            if (dir) EsploraDirectory(voce, inoFiglio, raw, includeDeleted, livello + 1, ct);
        }
    }

    // ---------------------------------------------------------------- inode -> FsEntry

    private FsEntry CostruisciVoce(uint ino, byte[] raw, string nome, bool cancellato)
    {
        ushort mode = ByteUtils.U16LE(raw, 0);
        uint flags = ByteUtils.U32LE(raw, 32);
        int tipo = mode & S_IFMT;

        var voce = new FsEntry
        {
            Name = string.IsNullOrEmpty(nome) ? $"inode_{ino}" : nome,
            IsDirectory = tipo == S_IFDIR,
            IsDeleted = cancellato,
            Modified = ByteUtils.UnixTime(ByteUtils.U32LE(raw, 16)),
            Created = LeggiCrtime(raw),
            Tag = CostruisciInfo(ino, raw)
        };

        var info = (ExtInodeInfo)voce.Tag;
        long size = DimensioneInode(raw, tipo == S_IFREG);
        voce.Length = size;

        if (tipo == S_IFDIR)
        {
            // per le directory la dimensione non interessa a chi estrae
            return voce;
        }

        if (tipo == S_IFLNK)
        {
            string target = LeggiSymlink(raw, size, info);
            if (target != null)
            {
                info.SymlinkTarget = target;
                voce.InlineData = Encoding.UTF8.GetBytes(target);
                voce.Length = voce.InlineData.Length;
                voce.Extents.Clear();
                return voce;
            }
            // symlink lungo: il percorso sta in un blocco dati, lo mappo come un file normale
        }

        if (tipo != S_IFREG && tipo != S_IFLNK)
        {
            // fifo, socket, dispositivi a caratteri o a blocchi: nessun contenuto da estrarre
            voce.Length = 0;
            _contaSpeciali++;
            return voce;
        }

        if ((flags & (FL_COMPR | FL_VERITY)) != 0)
        {
            voce.IsUncertain = true;
            if (!_notaFlagInode)
            {
                _notaFlagInode = true;
                Notes.Add("Alcuni inode hanno il flag di compressione o di verity: " +
                          "i dati vengono estratti cosi' come sono, senza decomprimere ne' verificare.");
            }
        }

        if ((flags & FL_INLINE_DATA) != 0)
        {
            voce.InlineData = LeggiInlineData(raw, size);
            if (voce.InlineData != null && voce.InlineData.Length < size)
            {
                voce.IsUncertain = true;
                voce.Length = voce.InlineData.Length;
            }
            return voce;
        }

        if ((flags & FL_ENCRYPT) != 0)
            voce.IsUncertain = true;

        voce.Extents = MappaFile(raw, size, cancellato, voce);
        if (voce.Extents.Count == 0 && size > 0)
            voce.IsUncertain = true;

        return voce;
    }

    private ExtInodeInfo CostruisciInfo(uint ino, byte[] raw)
    {
        uint flags = ByteUtils.U32LE(raw, 32);
        return new ExtInodeInfo
        {
            Inode = ino,
            Mode = ByteUtils.U16LE(raw, 0),
            Flags = flags,
            LinksCount = ByteUtils.U16LE(raw, 26),
            Uid = (uint)ByteUtils.U16LE(raw, 2) | ((uint)ByteUtils.U16LE(raw, 120) << 16),
            Gid = (uint)ByteUtils.U16LE(raw, 24) | ((uint)ByteUtils.U16LE(raw, 122) << 16),
            Accessed = ByteUtils.UnixTime(ByteUtils.U32LE(raw, 8)),
            Changed = ByteUtils.UnixTime(ByteUtils.U32LE(raw, 12)),
            Deleted = ByteUtils.UnixTime(ByteUtils.U32LE(raw, 20)),
            UsesExtents = (flags & FL_EXTENTS) != 0,
            InlineData = (flags & FL_INLINE_DATA) != 0
        };
    }

    private long DimensioneInode(byte[] raw, bool fileRegolare)
    {
        long lo = ByteUtils.U32LE(raw, 4);
        long hi = ByteUtils.U32LE(raw, 108);
        // i_size_high vale solo per i file regolari (per le directory e' i_dir_acl)
        long size = fileRegolare ? (lo | (hi << 32)) : lo;
        if (size < 0) size = 0;
        return size;
    }

    private DateTime? LeggiCrtime(byte[] raw)
    {
        if (_inodeSize <= 128 || raw.Length < 148) return null;
        int extra = ByteUtils.U16LE(raw, 128);
        // i_crtime sta a 0x90; e' presente solo se i_extra_isize lo copre
        if (extra < 0x90 - 128 + 4) return null;
        return ByteUtils.UnixTime(ByteUtils.U32LE(raw, 0x90));
    }

    private string LeggiSymlink(byte[] raw, long size, ExtInodeInfo info)
    {
        // symlink breve: il percorso sta nei 60 byte di i_block
        if (size <= 0 || size >= 60) return null;
        if ((info.Flags & FL_EXTENTS) != 0) return null;
        if ((info.Flags & FL_INLINE_DATA) != 0)
        {
            var dati = LeggiInlineData(raw, size);
            if (dati == null) return null;
            info.FastSymlink = true;
            return DecodificaNome(dati, 0, dati.Length);
        }
        info.FastSymlink = true;
        _contaSymlinkBrevi++;
        return DecodificaNome(raw, 40, (int)size);
    }

    // ---------------------------------------------------------------- inline data

    /// <summary>
    /// Contenuto inline: primi 60 byte in i_block, il resto nell'attributo esteso "system.data"
    /// memorizzato nello spazio libero dell'inode.
    /// </summary>
    private byte[] LeggiInlineData(byte[] raw, long size)
    {
        if (size < 0) return Array.Empty<byte>();
        int inBlock = (int)Math.Min(size, 60);
        var testa = new byte[inBlock];
        Array.Copy(raw, 40, testa, 0, inBlock);

        if (size <= 60) return testa;

        var coda = LeggiXattrInode(raw, 7, "data");
        if (coda == null)
        {
            if (!_notaInlineParziale)
            {
                _notaInlineParziale = true;
                Notes.Add("Contenuto inline parziale: manca l'attributo esteso system.data, " +
                          "recuperati solo i primi 60 byte.");
            }
            return testa;
        }

        int extra = (int)Math.Min(coda.Length, size - 60);
        var tutto = new byte[inBlock + extra];
        Array.Copy(testa, tutto, inBlock);
        Array.Copy(coda, 0, tutto, inBlock, extra);
        return tutto;
    }

    /// <summary>Cerca un attributo esteso nello spazio extra dell'inode (ibody xattr).</summary>
    private byte[] LeggiXattrInode(byte[] raw, byte nameIndex, string nome)
    {
        if (_inodeSize <= 128 || raw.Length < 132) return null;
        int extra = ByteUtils.U16LE(raw, 128);
        if (extra < 4 || 128 + extra + 4 > raw.Length) return null;

        int hdr = 128 + extra;
        if (ByteUtils.U32LE(raw, hdr) != 0xEA020000) return null;

        int first = hdr + 4;
        var nomeBytes = Encoding.ASCII.GetBytes(nome);
        int p = first;

        while (p + 16 <= raw.Length)
        {
            int nameLen = raw[p];
            int idx = raw[p + 1];
            int valOff = ByteUtils.U16LE(raw, p + 2);
            uint valInum = ByteUtils.U32LE(raw, p + 4);
            int valSize = (int)ByteUtils.U32LE(raw, p + 8);

            if (nameLen == 0 && idx == 0 && valOff == 0 && valInum == 0) break;
            if (p + 16 + nameLen > raw.Length) break;

            if (idx == nameIndex && nameLen == nomeBytes.Length && valInum == 0 &&
                valSize >= 0 && first + valOff + valSize <= raw.Length)
            {
                bool uguale = true;
                for (int i = 0; i < nameLen; i++)
                    if (raw[p + 16 + i] != nomeBytes[i]) { uguale = false; break; }

                if (uguale)
                {
                    var v = new byte[valSize];
                    Array.Copy(raw, first + valOff, v, 0, valSize);
                    return v;
                }
            }

            int passo = 16 + ((nameLen + 3) & ~3);
            p += passo;
        }
        return null;
    }

    // ---------------------------------------------------------------- mappatura dei blocchi

    private List<FsExtent> MappaFile(byte[] raw, long size, bool cancellato, FsEntry voce)
    {
        var runs = new List<BlockRun>();
        uint flags = ByteUtils.U32LE(raw, 32);
        long necessari = size <= 0 ? 0 : (size + _blockSize - 1) / _blockSize;
        if (necessari == 0) return new List<FsExtent>();

        try
        {
            if ((flags & FL_EXTENTS) != 0)
            {
                var visitati = new HashSet<long>();
                PercorriExtent(raw, 40, 60, runs, visitati, 0);
                runs.Sort(static (a, b) => a.Logical.CompareTo(b.Logical));
            }
            else
            {
                MappaIndiretti(raw, necessari, runs);
            }
        }
        catch (Exception ex)
        {
            Notes.Add("Mappatura dei blocchi interrotta: " + ex.Message);
            voce.IsUncertain = true;
        }

        if (runs.Count == 0 && cancellato)
        {
            // ext3/ext4 azzerano i puntatori quando cancellano: resta solo la dimensione
            voce.IsUncertain = true;
            return new List<FsExtent>();
        }

        return CostruisciExtent(runs, size, voce);
    }

    /// <summary>Visita ricorsiva dell'albero degli extent (ext4).</summary>
    private void PercorriExtent(byte[] nodo, int off, int lunghezza, List<BlockRun> runs,
                                HashSet<long> visitati, int livello)
    {
        if (livello > MaxExtentDepth) return;
        if (off + 12 > nodo.Length || lunghezza < 12) return;

        if (ByteUtils.U16LE(nodo, off) != ExtentMagic)
        {
            _notaExtentRotto = true;
            return;
        }

        int entries = ByteUtils.U16LE(nodo, off + 2);
        int profondita = ByteUtils.U16LE(nodo, off + 6);
        int massimo = (lunghezza - 12) / 12;
        if (entries > massimo) entries = massimo;
        if (entries < 0) entries = 0;
        if (profondita > MaxExtentDepth) { _notaExtentRotto = true; return; }

        for (int i = 0; i < entries; i++)
        {
            int e = off + 12 + i * 12;
            if (e + 12 > nodo.Length) break;

            if (profondita == 0)
            {
                long logico = ByteUtils.U32LE(nodo, e);
                int len = ByteUtils.U16LE(nodo, e + 4);
                bool nonInizializzato = len > 32768;
                if (nonInizializzato) len -= 32768;
                if (len <= 0) continue;

                long fisico = ByteUtils.U32LE(nodo, e + 8) | ((long)ByteUtils.U16LE(nodo, e + 6) << 32);
                if (fisico <= 0 || fisico >= _blocksCount) { _notaBlocchiFuoriRange = true; continue; }
                if (fisico + len > _blocksCount) len = (int)(_blocksCount - fisico);
                if (len <= 0) continue;

                runs.Add(new BlockRun { Logical = logico, Physical = fisico, Count = len });
            }
            else
            {
                long figlio = ByteUtils.U32LE(nodo, e + 4) | ((long)ByteUtils.U16LE(nodo, e + 8) << 32);
                if (figlio <= 0 || figlio >= _blocksCount) { _notaBlocchiFuoriRange = true; continue; }
                if (!visitati.Add(figlio)) continue;        // protezione dai cicli

                var blocco = LeggiBlocco(figlio);
                if (blocco == null) { _notaExtentRotto = true; continue; }
                PercorriExtent(blocco, 0, _blockSize, runs, visitati, livello + 1);
            }
        }
    }

    /// <summary>Puntatori diretti, indiretti, doppi e tripli (ext2/ext3).</summary>
    private void MappaIndiretti(byte[] raw, long necessari, List<BlockRun> runs)
    {
        long logico = 0;
        for (int i = 0; i < 12 && logico < necessari; i++, logico++)
            AggiungiPuntatore(runs, logico, ByteUtils.U32LE(raw, 40 + i * 4));

        var visitati = new HashSet<long>();
        logico = PercorriIndiretto(ByteUtils.U32LE(raw, 40 + 12 * 4), 1, logico, necessari, runs, visitati);
        logico = PercorriIndiretto(ByteUtils.U32LE(raw, 40 + 13 * 4), 2, logico, necessari, runs, visitati);
        _ = PercorriIndiretto(ByteUtils.U32LE(raw, 40 + 14 * 4), 3, logico, necessari, runs, visitati);
    }

    private long PercorriIndiretto(long blocco, int livello, long logico, long necessari,
                                   List<BlockRun> runs, HashSet<long> visitati)
    {
        if (logico >= necessari) return logico;

        long puntatori = _blockSize / 4;
        long copertura = 1;
        for (int i = 0; i < livello; i++) copertura *= puntatori;

        if (blocco <= 0 || blocco >= _blocksCount)
        {
            if (blocco != 0) _notaBlocchiFuoriRange = true;
            return logico + copertura;                       // buco (o ramo perso)
        }
        if (!visitati.Add(blocco)) return logico + copertura; // ciclo su struttura corrotta

        var buf = LeggiBlocco(blocco);
        if (buf == null) return logico + copertura;

        for (int i = 0; i < puntatori && logico < necessari; i++)
        {
            long p = ByteUtils.U32LE(buf, i * 4);
            if (livello == 1)
            {
                AggiungiPuntatore(runs, logico, p);
                logico++;
            }
            else
            {
                logico = PercorriIndiretto(p, livello - 1, logico, necessari, runs, visitati);
            }
        }
        return logico;
    }

    private void AggiungiPuntatore(List<BlockRun> runs, long logico, long fisico)
    {
        if (fisico <= 0) return;                              // 0 = buco
        if (fisico >= _blocksCount) { _notaBlocchiFuoriRange = true; return; }

        if (runs.Count > 0)
        {
            var ultimo = runs[^1];
            if (ultimo.Logical + ultimo.Count == logico && ultimo.Physical + ultimo.Count == fisico)
            {
                ultimo.Count++;
                runs[^1] = ultimo;
                return;
            }
        }
        runs.Add(new BlockRun { Logical = logico, Physical = fisico, Count = 1 });
    }

    /// <summary>Converte le sequenze di blocchi in extent di byte, fondendo gli adiacenti.</summary>
    private List<FsExtent> CostruisciExtent(List<BlockRun> runs, long size, FsEntry voce)
    {
        var risultato = new List<FsExtent>();
        if (size <= 0) return risultato;

        long necessari = (size + _blockSize - 1) / _blockSize;
        long atteso = 0;

        foreach (var r in runs)
        {
            if (atteso >= necessari) break;

            long logico = r.Logical;
            long conta = r.Count;
            long fisico = r.Physical;

            if (logico < atteso)
            {
                // sovrapposizione: taglio la parte gia' coperta (struttura incoerente)
                long salto = atteso - logico;
                if (salto >= conta) continue;
                logico += salto;
                fisico += salto;
                conta -= salto;
                voce.IsUncertain = true;
            }

            if (logico > atteso)
            {
                long buco = logico - atteso;
                if (atteso + buco > necessari) buco = necessari - atteso;
                if (buco > 0)
                {
                    _notaSparso = true;
                    Aggiungi(risultato, HoleOffset, buco * (long)_blockSize);
                    atteso += buco;
                }
            }

            if (atteso + conta > necessari) conta = necessari - atteso;
            if (conta <= 0) continue;

            Aggiungi(risultato, fisico * (long)_blockSize, conta * (long)_blockSize);
            atteso += conta;
        }

        if (atteso < necessari)
        {
            long mancanti = necessari - atteso;
            if (runs.Count > 0)
            {
                _notaSparso = true;
                Aggiungi(risultato, HoleOffset, mancanti * (long)_blockSize);
            }
        }

        // l'ultimo extent viene troncato alla dimensione reale del file
        long totale = 0;
        for (int i = 0; i < risultato.Count; i++)
        {
            if (totale + risultato[i].Length > size)
            {
                var e = risultato[i];
                e.Length = size - totale;
                risultato[i] = e;
                if (e.Length <= 0) { risultato.RemoveRange(i, risultato.Count - i); break; }
                risultato.RemoveRange(i + 1, risultato.Count - i - 1);
                break;
            }
            totale += risultato[i].Length;
        }

        return risultato;

        static void Aggiungi(List<FsExtent> lista, long offset, long lunghezza)
        {
            if (lunghezza <= 0) return;
            if (lista.Count > 0)
            {
                var u = lista[^1];
                bool buco = u.Offset == HoleOffset && offset == HoleOffset;
                if (buco || (u.Offset >= 0 && offset >= 0 && u.Offset + u.Length == offset))
                {
                    u.Length += lunghezza;
                    lista[^1] = u;
                    return;
                }
            }
            lista.Add(new FsExtent(offset, lunghezza));
        }
    }

    /// <summary>Blocchi fisici in ordine logico (0 = buco). Usato per leggere le directory.</summary>
    private List<long> BlocchiDati(byte[] raw, long size)
    {
        var lista = new List<long>();
        long necessari = size <= 0 ? 0 : (size + _blockSize - 1) / _blockSize;
        if (necessari == 0) return lista;
        if (necessari > 1 << 22) necessari = 1 << 22;   // guardia su directory assurde

        var runs = new List<BlockRun>();
        uint flags = ByteUtils.U32LE(raw, 32);

        if ((flags & FL_EXTENTS) != 0)
        {
            PercorriExtent(raw, 40, 60, runs, new HashSet<long>(), 0);
            runs.Sort(static (a, b) => a.Logical.CompareTo(b.Logical));
        }
        else
        {
            MappaIndiretti(raw, necessari, runs);
        }

        long atteso = 0;
        foreach (var r in runs)
        {
            if (atteso >= necessari) break;
            long logico = r.Logical, conta = r.Count, fisico = r.Physical;
            if (logico < atteso)
            {
                long salto = atteso - logico;
                if (salto >= conta) continue;
                logico += salto; fisico += salto; conta -= salto;
            }
            while (atteso < logico && atteso < necessari) { lista.Add(0); atteso++; }
            for (long i = 0; i < conta && atteso < necessari; i++, atteso++) lista.Add(fisico + i);
        }
        while (atteso < necessari) { lista.Add(0); atteso++; }
        return lista;
    }

    // ---------------------------------------------------------------- directory

    /// <summary>
    /// Legge le voci di una directory. Gestisce il formato classico e quello con indice htree:
    /// i nodi dell'indice si presentano come una voce fittizia con inode 0 lunga tutto il blocco,
    /// quindi la lettura sequenziale li salta da sola.
    /// </summary>
    private List<(string nome, uint inode, byte tipo)> LeggiVociDirectory(uint ino, byte[] raw,
        bool cercaMorte, FsEntry nodo, CancellationToken ct)
    {
        uint flags = ByteUtils.U32LE(raw, 32);
        long size = DimensioneInode(raw, false);

        if ((flags & FL_INLINE_DATA) != 0)
            return LeggiDirectoryInline(raw, size, nodo, cercaMorte);

        var blocchi = BlocchiDati(raw, size);
        if (blocchi.Count == 0) return null;

        bool htree = (flags & FL_INDEX) != 0;
        var risultato = new List<(string, uint, byte)>();
        var visti = new HashSet<string>(StringComparer.Ordinal);

        foreach (long b in blocchi)
        {
            ct.ThrowIfCancellationRequested();
            if (risultato.Count > MaxEntriesPerDir) break;
            if (b <= 0) continue;

            var buf = LeggiBlocco(b);
            if (buf == null) continue;

            AnalizzaBloccoDirectory(buf, risultato, visti, nodo, cercaMorte, htree);
        }

        return risultato;
    }

    private void AnalizzaBloccoDirectory(byte[] buf, List<(string, uint, byte)> risultato,
                                         HashSet<string> visti, FsEntry nodo, bool cercaMorte,
                                         bool htree)
    {
        int pos = 0;
        int fine = Math.Min(buf.Length, _blockSize);

        while (pos + 8 <= fine)
        {
            uint inode = ByteUtils.U32LE(buf, pos);
            int recLen = ByteUtils.U16LE(buf, pos + 4);
            int nameLen;
            byte tipo;

            if (_hasFileType)
            {
                nameLen = buf[pos + 6];
                tipo = buf[pos + 7];
            }
            else
            {
                nameLen = ByteUtils.U16LE(buf, pos + 6);
                tipo = 0;
            }

            if (recLen < 8 || (recLen & 3) != 0 || pos + recLen > fine)
                break;                                        // blocco corrotto: mi fermo qui

            int usato = 8;
            if (inode != 0 && nameLen > 0 && 8 + nameLen <= recLen)
            {
                string nome = DecodificaNome(buf, pos + 8, nameLen);
                if (nome.Length > 0 && nome != "." && nome != "..")
                {
                    if (visti.Add(nome)) risultato.Add((nome, inode, tipo));
                }
                else if (nome == "." || nome == "..")
                {
                    risultato.Add((nome, inode, tipo));
                }
                usato = (8 + nameLen + 3) & ~3;
            }

            // in una directory htree la voce fittizia che copre il resto del blocco contiene
            // la tabella hash/blocco dell'indice: setacciarla produrrebbe solo falsi positivi
            bool nodoIndice = htree && inode == 0 && pos + recLen == fine && (pos == 0 || pos == 24);

            if (cercaMorte && !nodoIndice && recLen > usato + 8)
                CercaVociMorte(buf, pos + usato, pos + recLen, nodo);

            pos += recLen;
        }
    }

    /// <summary>
    /// Setaccia lo spazio fra la fine di una voce e il suo rec_len: quando un file viene
    /// cancellato la voce precedente ne assorbe il record, ma il nome resta scritto.
    /// E' l'unico modo per dare un nome agli inode orfani.
    /// </summary>
    private void CercaVociMorte(byte[] buf, int da, int a, FsEntry genitore)
    {
        int p = (da + 3) & ~3;
        while (p + 12 <= a)
        {
            uint inode = ByteUtils.U32LE(buf, p);
            int recLen = ByteUtils.U16LE(buf, p + 4);
            int nameLen = _hasFileType ? buf[p + 6] : ByteUtils.U16LE(buf, p + 6);
            byte tipo = _hasFileType ? buf[p + 7] : (byte)0;

            bool plausibile =
                inode != 0 && inode <= _inodesCount &&
                nameLen > 0 && nameLen <= 255 &&
                recLen >= 8 + nameLen && (recLen & 3) == 0 &&
                p + 8 + nameLen <= a &&
                tipo <= 7;

            if (plausibile)
            {
                string nome = DecodificaNome(buf, p + 8, nameLen);
                if (NomePlausibile(nome))
                {
                    _vociMorte.Add(new VoceMorta
                    {
                        Inode = inode,
                        Nome = nome,
                        Genitore = genitore,
                        FileType = tipo
                    });
                    int passo = (8 + nameLen + 3) & ~3;
                    p += Math.Max(4, passo);
                    continue;
                }
            }
            p += 4;
        }
    }

    private static bool NomePlausibile(string nome)
    {
        if (string.IsNullOrEmpty(nome) || nome.Length > 255) return false;
        if (nome == "." || nome == "..") return false;
        foreach (char c in nome)
            if (c < 0x20 || c == '/' || c == '�') return false;
        return true;
    }

    /// <summary>Directory con contenuto inline: i primi 4 byte sono l'inode del genitore.</summary>
    private List<(string nome, uint inode, byte tipo)> LeggiDirectoryInline(byte[] raw, long size,
                                                                           FsEntry nodo, bool cercaMorte)
    {
        var risultato = new List<(string, uint, byte)>();
        var visti = new HashSet<string>(StringComparer.Ordinal);

        var dati = LeggiInlineData(raw, size);
        if (dati == null || dati.Length <= 8) return risultato;

        // i dirent veri iniziano dopo gli 8 byte riservati a "." e ".."
        AnalizzaAreaDirent(dati, 8, Math.Min(dati.Length, 60), risultato, visti, nodo, cercaMorte);
        if (dati.Length > 60)
            AnalizzaAreaDirent(dati, 60, dati.Length, risultato, visti, nodo, cercaMorte);

        return risultato;
    }

    private void AnalizzaAreaDirent(byte[] buf, int da, int a, List<(string, uint, byte)> risultato,
                                    HashSet<string> visti, FsEntry nodo, bool cercaMorte)
    {
        int pos = da;
        while (pos + 8 <= a)
        {
            uint inode = ByteUtils.U32LE(buf, pos);
            int recLen = ByteUtils.U16LE(buf, pos + 4);
            int nameLen = _hasFileType ? buf[pos + 6] : ByteUtils.U16LE(buf, pos + 6);
            byte tipo = _hasFileType ? buf[pos + 7] : (byte)0;

            if (recLen < 8 || (recLen & 3) != 0 || pos + recLen > a) break;

            int usato = 8;
            if (inode != 0 && nameLen > 0 && 8 + nameLen <= recLen)
            {
                string nome = DecodificaNome(buf, pos + 8, nameLen);
                if (NomePlausibile(nome) && visti.Add(nome)) risultato.Add((nome, inode, tipo));
                usato = (8 + nameLen + 3) & ~3;
            }

            if (cercaMorte && recLen > usato + 8)
                CercaVociMorte(buf, pos + usato, pos + recLen, nodo);

            pos += recLen;
        }
    }

    // ---------------------------------------------------------------- recupero dei cancellati

    private void RecuperaCancellati(CancellationToken ct)
    {
        var cartellaOrfani = (FsEntry)null;
        var gestiti = new HashSet<uint>();

        // 1) inode con un nome recuperato da una directory entry morta
        foreach (var vm in _vociMorte)
        {
            ct.ThrowIfCancellationRequested();
            if (vm.Inode == 0 || vm.Inode > _inodesCount) continue;
            if (_inodeVivi.Contains(vm.Inode)) continue;       // inode gia' riutilizzato da un file vivo
            if (!gestiti.Add(vm.Inode)) continue;

            var raw = LeggiInodeGrezzo(vm.Inode);
            FsEntry voce;

            if (raw != null && SembraCancellato(raw))
            {
                voce = CostruisciVoce(vm.Inode, raw, vm.Nome, true);
                if (voce == null) continue;
            }
            else
            {
                // inode ripulito del tutto: resta solo il nome
                voce = new FsEntry
                {
                    Name = vm.Nome,
                    IsDeleted = true,
                    IsUncertain = true,
                    IsDirectory = vm.FileType == 2,
                    Length = 0
                };
                Notes.Add($"'{vm.Nome}': inode {vm.Inode} azzerato, recuperato solo il nome.");
            }

            voce.IsDeleted = true;
            var genitore = vm.Genitore ?? Root;
            AddChild(genitore, voce);
        }

        // 2) inode orfani senza nome: li raccolgo in una cartella dedicata
        long orfani = 0;
        foreach (uint ino in EnumeraInodeCancellati(ct))
        {
            if (_inodeVivi.Contains(ino) || gestiti.Contains(ino)) continue;

            var raw = LeggiInodeGrezzo(ino);
            if (raw == null) continue;

            var voce = CostruisciVoce(ino, raw, null, true);
            if (voce == null) continue;
            voce.IsDeleted = true;
            voce.IsUncertain = true;

            cartellaOrfani ??= CreaCartellaOrfani();
            AddChild(cartellaOrfani, voce);
            orfani++;
            if (orfani > 200_000) { Notes.Add("Troppi inode orfani: elenco troncato."); break; }
        }

        if (orfani > 0)
            Notes.Add($"{orfani} inode cancellati senza nome recuperabile raccolti in '[inode cancellati]'.");
    }

    private FsEntry CreaCartellaOrfani()
    {
        var c = new FsEntry { Name = "[inode cancellati]", IsDirectory = true, IsDeleted = true };
        AddChild(Root, c);
        return c;
    }

    private static bool SembraCancellato(byte[] raw)
    {
        ushort mode = ByteUtils.U16LE(raw, 0);
        ushort links = ByteUtils.U16LE(raw, 26);
        uint dtime = ByteUtils.U32LE(raw, 20);
        return mode != 0 && links == 0 && dtime != 0;
    }

    /// <summary>Scorre tutte le tabelle inode cercando quelle con link 0 e dtime valorizzato.</summary>
    private IEnumerable<uint> EnumeraInodeCancellati(CancellationToken ct)
    {
        for (long g = 0; g < _groups.LongLength; g++)
        {
            ct.ThrowIfCancellationRequested();
            var gd = _groups[g];
            if (!gd.Valid) continue;

            long baseIno = g * _inodesPerGroup + 1;
            long tabella = gd.InodeTable * (long)_blockSize;

            // leggo la tabella a pezzi per non fare una richiesta per inode
            const int inodePerLettura = 256;
            for (uint i = 0; i < _inodesPerGroup; i += inodePerLettura)
            {
                ct.ThrowIfCancellationRequested();
                int quanti = (int)Math.Min(inodePerLettura, _inodesPerGroup - i);
                long off = tabella + (long)i * _inodeSize;
                if (off < 0 || off >= Source.Length) break;

                byte[] buf;
                try { buf = Read(off, quanti * _inodeSize); }
                catch { break; }

                for (int k = 0; k < quanti; k++)
                {
                    long ino = baseIno + i + k;
                    if (ino > _inodesCount) yield break;
                    if (ino < _firstInode && ino != RootInode) continue;

                    int p = k * _inodeSize;
                    if (p + 32 > buf.Length) break;

                    ushort mode = ByteUtils.U16LE(buf, p);
                    ushort links = ByteUtils.U16LE(buf, p + 26);
                    uint dtime = ByteUtils.U32LE(buf, p + 20);
                    if (mode == 0 || links != 0 || dtime == 0) continue;

                    // gli inode che contengono solo attributi estesi non sono file dell'utente
                    if ((ByteUtils.U32LE(buf, p + 32) & FL_EA_INODE) != 0) continue;

                    int tipo = mode & S_IFMT;
                    if (tipo != S_IFREG && tipo != S_IFDIR && tipo != S_IFLNK) continue;

                    yield return (uint)ino;
                }
            }
        }
    }

    // ---------------------------------------------------------------- estrazione

    /// <summary>Come la base, ma gli extent con offset <see cref="HoleOffset"/> producono zeri.</summary>
    public override long Extract(FsEntry entry, Stream destination, CancellationToken ct)
    {
        if (entry == null || entry.IsDirectory || destination == null) return 0;

        if (entry.InlineData != null)
        {
            int n = (int)Math.Min(entry.InlineData.Length,
                                  entry.Length > 0 ? entry.Length : entry.InlineData.Length);
            destination.Write(entry.InlineData, 0, n);
            return n;
        }

        bool haBuchi = false;
        foreach (var e in entry.Extents) if (e.Offset == HoleOffset) { haBuchi = true; break; }
        if (!haBuchi) return base.Extract(entry, destination, ct);

        long rimanenti = entry.Length > 0 ? entry.Length : long.MaxValue;
        long scritti = 0;
        var buffer = new byte[1 << 20];
        var zeri = new byte[1 << 20];

        foreach (var extent in entry.Extents)
        {
            if (rimanenti <= 0) break;
            long posizione = extent.Offset;
            long resto = extent.Length;

            while (resto > 0 && rimanenti > 0)
            {
                ct.ThrowIfCancellationRequested();
                int pezzo = (int)Math.Min(buffer.Length, Math.Min(resto, rimanenti));

                if (extent.Offset == HoleOffset)
                {
                    destination.Write(zeri, 0, pezzo);
                }
                else
                {
                    Array.Clear(buffer, 0, pezzo);
                    Source.ReadBytes(posizione, pezzo, buffer, 0);
                    destination.Write(buffer, 0, pezzo);
                    posizione += pezzo;
                }

                resto -= pezzo;
                rimanenti -= pezzo;
                scritti += pezzo;
            }
        }
        return scritti;
    }

    // ---------------------------------------------------------------- utilita'

    private byte[] LeggiInodeGrezzo(uint ino)
    {
        if (!_valid || ino == 0 || ino > _inodesCount) return null;

        long gruppo = (ino - 1) / _inodesPerGroup;
        long indice = (ino - 1) % _inodesPerGroup;
        if (gruppo >= _groups.LongLength) return null;

        var gd = _groups[gruppo];
        if (!gd.Valid) return null;

        long off = gd.InodeTable * (long)_blockSize + indice * _inodeSize;
        if (off < 0 || off >= Source.Length) return null;

        var buf = Read(off, _inodeSize);
        return buf;
    }

    private byte[] LeggiBlocco(long blocco)
    {
        if (blocco < 0 || blocco >= _blocksCount) return null;
        long off = blocco * (long)_blockSize;
        if (off < 0 || off >= Source.Length) return null;
        return Read(off, _blockSize);
    }

    private static string DecodificaNome(byte[] b, int off, int len)
    {
        if (len <= 0 || off < 0 || off + len > b.Length) return "";
        try { return new UTF8Encoding(false, true).GetString(b, off, len); }
        catch { return Encoding.Latin1.GetString(b, off, len); }
    }

    private static string LeggiNome(byte[] b, int off, int len)
    {
        int n = 0;
        while (n < len && off + n < b.Length && b[off + n] != 0) n++;
        return DecodificaNome(b, off, n);
    }

    private static string FormattaUuid(byte[] b, int off)
    {
        if (off + 16 > b.Length) return "";
        bool tuttoZero = true;
        for (int i = 0; i < 16; i++) if (b[off + i] != 0) { tuttoZero = false; break; }
        if (tuttoZero) return "";

        var sb = new StringBuilder(36);
        for (int i = 0; i < 16; i++)
        {
            if (i == 4 || i == 6 || i == 8 || i == 10) sb.Append('-');
            sb.Append(b[off + i].ToString("x2"));
        }
        return sb.ToString();
    }
}
