using System.Text;
using DVDRescue.Core;

namespace DVDRescue.FileSystems;

/// <summary>Dati exFAT di una voce: servono all'interfaccia e alla diagnostica.</summary>
public sealed class ExFatEntryInfo
{
    /// <summary>Primo cluster dei dati (0 = file vuoto o campo perso).</summary>
    public uint FirstCluster;

    /// <summary>Flag NoFatChain: i dati sono contigui, la FAT non va seguita.</summary>
    public bool NoFatChain;

    /// <summary>Byte effettivamente scritti; oltre questo limite il contenuto non è valido.</summary>
    public long ValidDataLength;

    /// <summary>Dimensione dichiarata del file.</summary>
    public long DataLength;

    public ushort Attributes;
    public ushort NameHashStored;
    public ushort NameHashComputed;

    /// <summary>Il name hash memorizzato corrisponde al nome ricostruito.</summary>
    public bool NameHashOk;

    /// <summary>Il SetChecksum corrisponde (per le voci cancellate il bit 7 viene ripristinato).</summary>
    public bool SetChecksumOk;

    /// <summary>La catena FAT si è interrotta prima del previsto.</summary>
    public bool ChainBroken;

    /// <summary>La catena è stata ricostruita presumendo dati contigui.</summary>
    public bool ContiguousGuess;

    /// <summary>Almeno un cluster risulta allocato nella bitmap (per un file cancellato = riassegnato).</summary>
    public bool ClustersAllocated;

    public DateTime? Accessed;
    public int ClustersUsed;
}

/// <summary>
/// Lettore exFAT in sola lettura. La sorgente è il volume: offset 0 = boot sector.
/// Oltre all'albero vivo recupera le voci cancellate (bit 7 del tipo azzerato).
/// </summary>
public sealed class ExFatFileSystem : FileSystemBase
{
    // ---- costanti di formato ----
    private const int EntrySize = 32;
    private const byte TypeEndOfDirectory = 0x00;
    private const byte TypeBitmap = 0x81;
    private const byte TypeUpcase = 0x82;
    private const byte TypeVolumeLabel = 0x83;
    private const byte TypeFile = 0x85;
    private const byte TypeVolumeGuid = 0xA0;
    private const byte TypeTexFatPadding = 0xA1;
    private const byte TypeWinCeAcl = 0xA2;
    private const byte TypeStreamExt = 0xC0;
    private const byte TypeFileName = 0xC1;
    private const byte TypeVendorExt = 0xC2;
    private const byte TypeVendorAlloc = 0xC3;

    private const int MaxSecondaryEntries = 18;   // 1 stream + 17 nomi = 255 caratteri
    private const int NameCharsPerEntry = 15;

    // ---- limiti difensivi (strutture corrotte non devono mai bloccare il programma) ----
    private const long MaxDirectoryBytes = 64L * 1024 * 1024;
    private const int MaxDirectoryDepth = 128;
    private const int MaxEntries = 2_000_000;
    private const long MaxClustersPerChain = 1L << 26;
    private const int MaxNotes = 300;

    private const int FatPageSize = 8192;         // multiplo di 4
    private const int BitmapPageSize = 8192;

    // ---- boot sector ----
    private ulong _partitionOffset;
    private ulong _volumeLength;                  // in settori
    private uint _fatOffsetSectors;
    private uint _fatLengthSectors;
    private uint _clusterHeapOffsetSectors;
    private uint _clusterCount;
    private uint _rootCluster;
    private uint _volumeSerial;
    private ushort _volumeFlags;
    private byte _bytesPerSectorShift;
    private byte _sectorsPerClusterShift;
    private byte _numberOfFats;
    private byte _revisionMajor, _revisionMinor;
    private byte _percentInUse;

    // ---- derivati ----
    private int _bytesPerSector;
    private int _sectorsPerCluster;
    private long _clusterSize;
    private long _clusterHeapOffsetBytes;
    private long _volumeBytes;
    private int _activeFat;

    private bool _valid;

    private PagedArea _fat;
    private PagedArea _bitmap;
    private long _bitmapBits;
    private char[] _upcase;
    private bool _upcaseIsFallback;

    private readonly HashSet<string> _noteSet = new(StringComparer.Ordinal);
    private int _entryBudget = MaxEntries;

    public override string TypeName => "exFAT";

    public override long TotalBytes => _valid && _volumeBytes > 0 ? _volumeBytes : Source.Length;

    /// <summary>Dimensione del cluster in byte (0 se il boot sector non è valido).</summary>
    public long ClusterSize => _clusterSize;

    /// <summary>Numero di cluster dell'heap dichiarato dal boot sector.</summary>
    public uint ClusterCount => _clusterCount;

    /// <summary>Numero di serie del volume.</summary>
    public uint VolumeSerialNumber => _volumeSerial;

    /// <summary>Revisione del formato, es. "1.0".</summary>
    public string Revision => $"{_revisionMajor}.{_revisionMinor}";

    /// <summary>Vero se la up-case table del volume non era leggibile e si usa quella di riserva.</summary>
    public bool UpcaseIsFallback => _upcaseIsFallback;

    // ---- campi del boot sector, utili all'interfaccia e alla diagnostica ----

    /// <summary>Settori tra l'inizio del supporto e l'inizio del volume (informativo:
    /// la sorgente è già il volume, quindi non entra nei calcoli).</summary>
    public long PartitionOffsetSectors => (long)_partitionOffset;

    /// <summary>Lunghezza del volume in settori.</summary>
    public long VolumeLengthSectors => (long)_volumeLength;

    public long FatOffsetSectors => _fatOffsetSectors;
    public long FatLengthSectors => _fatLengthSectors;
    public long ClusterHeapOffsetSectors => _clusterHeapOffsetSectors;
    public uint FirstClusterOfRootDirectory => _rootCluster;
    public int BytesPerSector => _bytesPerSector;
    public int SectorsPerCluster => _sectorsPerCluster;
    public int NumberOfFats => _numberOfFats;
    public ushort VolumeFlags => _volumeFlags;

    /// <summary>Indice della FAT in uso (bit ActiveFat di VolumeFlags).</summary>
    public int ActiveFat => _activeFat;

    /// <summary>Percentuale di spazio occupato dichiarata dal boot sector (0xFF = ignota).</summary>
    public int PercentInUse => _percentInUse;

    /// <summary>Vero se il boot sector è utilizzabile.</summary>
    public bool IsValid => _valid;

    public ExFatFileSystem(IBlockSource source) : base(source)
    {
        try { ParseBootSector(); }
        catch (Exception ex)
        {
            _valid = false;
            Note("Boot sector exFAT illeggibile: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Riconoscimento
    // ------------------------------------------------------------------

    /// <summary>Vero se la sorgente comincia con un boot sector exFAT plausibile.</summary>
    public static bool Detect(IBlockSource source)
    {
        if (source == null) return false;

        try
        {
            var b = new byte[512];
            if (source.ReadBytes(0, 512, b, 0) < 512) return false;

            // firma "EXFAT   " a offset 3
            if (b[3] != 'E' || b[4] != 'X' || b[5] != 'F' || b[6] != 'A' || b[7] != 'T' ||
                b[8] != ' ' || b[9] != ' ' || b[10] != ' ') return false;

            // MustBeZero: 53 byte a zero, distingue exFAT da un BPB FAT normale
            for (int i = 11; i < 64; i++) if (b[i] != 0) return false;

            if (ByteUtils.U16LE(b, 510) != 0xAA55) return false;

            byte bpsShift = b[108];
            byte spcShift = b[109];
            if (bpsShift < 9 || bpsShift > 12) return false;
            if (spcShift > 25 - bpsShift) return false;

            byte fats = b[110];
            if (fats != 1 && fats != 2) return false;

            ulong volumeLength = ByteUtils.U64LE(b, 72);
            uint fatOffset = ByteUtils.U32LE(b, 80);
            uint fatLength = ByteUtils.U32LE(b, 84);
            uint heapOffset = ByteUtils.U32LE(b, 88);
            uint clusterCount = ByteUtils.U32LE(b, 92);
            uint rootCluster = ByteUtils.U32LE(b, 96);

            if (fatOffset < 24 || fatLength < 1) return false;
            if (heapOffset < (ulong)fatOffset + (ulong)fatLength * fats) return false;
            if (clusterCount < 1 || clusterCount > 0xFFFFFFF5) return false;
            if (rootCluster < 2 || rootCluster > clusterCount + 1) return false;

            // l'heap deve stare dentro il volume
            ulong heapEnd = heapOffset + ((ulong)clusterCount << spcShift);
            if (volumeLength < heapEnd) return false;

            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    // Boot sector
    // ------------------------------------------------------------------

    private void ParseBootSector()
    {
        var b = new byte[512];
        if (Source.ReadBytes(0, 512, b, 0) < 512)
        {
            Note("Boot sector troppo corto: volume illeggibile.");
            return;
        }

        if (ByteUtils.Ascii(b, 3, 8) != "EXFAT")
        {
            Note("Firma \"EXFAT\" assente a offset 3: il volume non è exFAT.");
            return;
        }

        _partitionOffset = ByteUtils.U64LE(b, 64);
        _volumeLength = ByteUtils.U64LE(b, 72);
        _fatOffsetSectors = ByteUtils.U32LE(b, 80);
        _fatLengthSectors = ByteUtils.U32LE(b, 84);
        _clusterHeapOffsetSectors = ByteUtils.U32LE(b, 88);
        _clusterCount = ByteUtils.U32LE(b, 92);
        _rootCluster = ByteUtils.U32LE(b, 96);
        _volumeSerial = ByteUtils.U32LE(b, 100);
        _revisionMinor = b[104];
        _revisionMajor = b[105];
        _volumeFlags = ByteUtils.U16LE(b, 106);
        _bytesPerSectorShift = b[108];
        _sectorsPerClusterShift = b[109];
        _numberOfFats = b[110];
        _percentInUse = b[112];

        if (_bytesPerSectorShift < 9 || _bytesPerSectorShift > 12)
        {
            Note($"BytesPerSectorShift fuori norma ({_bytesPerSectorShift}): assumo 512 byte per settore.");
            _bytesPerSectorShift = 9;
        }
        if (_sectorsPerClusterShift > 25 - _bytesPerSectorShift)
        {
            Note($"SectorsPerClusterShift fuori norma ({_sectorsPerClusterShift}): assumo 1 settore per cluster.");
            _sectorsPerClusterShift = 0;
        }

        _bytesPerSector = 1 << _bytesPerSectorShift;
        _sectorsPerCluster = 1 << _sectorsPerClusterShift;
        _clusterSize = (long)_bytesPerSector * _sectorsPerCluster;
        _clusterHeapOffsetBytes = (long)_clusterHeapOffsetSectors * _bytesPerSector;
        _volumeBytes = (long)_volumeLength * _bytesPerSector;

        if (_revisionMajor != 1)
            Note($"Revisione exFAT {_revisionMajor}.{_revisionMinor} non prevista: lettura comunque tentata.");

        if (_numberOfFats == 2)
            Note("Il volume dichiara 2 FAT (TexFAT): supportata solo la FAT attiva, le transazioni in sospeso vengono ignorate.");
        else if (_numberOfFats != 1)
        {
            Note($"NumberOfFats anomalo ({_numberOfFats}): assumo una sola FAT.");
            _numberOfFats = 1;
        }

        _activeFat = _volumeFlags & 0x01;
        if (_activeFat != 0 && _numberOfFats < 2)
        {
            Note("VolumeFlags indica la seconda FAT ma ne esiste una sola: uso la prima.");
            _activeFat = 0;
        }
        else if (_activeFat != 0)
        {
            Note("In uso la FAT secondaria (VolumeFlags bit ActiveFat = 1).");
        }

        if ((_volumeFlags & 0x02) != 0)
            Note("Volume marcato \"sporco\" (VolumeDirty): smontato male, le strutture potrebbero essere incoerenti.");
        if ((_volumeFlags & 0x04) != 0)
            Note("Il volume segnala errori di supporto (MediaFailure): alcuni cluster sono già noti come illeggibili.");

        if (_clusterCount < 1 || _clusterCount > 0xFFFFFFF5)
        {
            Note($"ClusterCount non plausibile ({_clusterCount}): volume non leggibile.");
            return;
        }
        if (_rootCluster < 2 || _rootCluster > _clusterCount + 1)
        {
            Note($"Cluster di root fuori intervallo ({_rootCluster}): volume non leggibile.");
            return;
        }
        if (_fatLengthSectors < 1 || _fatOffsetSectors < 24)
        {
            Note("Posizione o lunghezza della FAT non plausibili: volume non leggibile.");
            return;
        }

        long heapEndBytes = _clusterHeapOffsetBytes + (long)_clusterCount * _clusterSize;
        if (_volumeBytes > 0 && heapEndBytes > _volumeBytes)
            Note("L'heap dei cluster sfora la lunghezza dichiarata del volume: struttura incoerente.");
        if (Source.Length > 0 && heapEndBytes > Source.Length)
            Note("L'heap dei cluster sfora la sorgente: l'immagine è troncata, i cluster finali non sono leggibili.");

        // la FAT attiva, come area paginata
        long fatStart = (long)_fatOffsetSectors * _bytesPerSector +
                        (long)_activeFat * _fatLengthSectors * _bytesPerSector;
        long fatBytes = (long)_fatLengthSectors * _bytesPerSector;
        _fat = new PagedArea(Source, new List<FsExtent> { new(fatStart, fatBytes) }, fatBytes, FatPageSize, 64);

        _valid = true;
    }

    // ------------------------------------------------------------------
    // Scansione
    // ------------------------------------------------------------------

    public override void Scan(bool includeDeleted, CancellationToken ct)
    {
        Root = new FsEntry { IsDirectory = true, Name = "", FullPath = "" };
        _entryBudget = MaxEntries;

        if (!_valid)
        {
            Note("Boot sector exFAT non valido: nessuna scansione possibile.");
            return;
        }

        // La root usa sempre la catena FAT (nessun NoFatChain) e la lunghezza non è dichiarata da
        // nessuna parte: si segue la catena fino al marcatore di fine.
        var rootExtents = BuildChain(_rootCluster, false, 0, MaxDirectoryBytes / _clusterSize,
                                     out bool rootBroken, out _, out _);
        if (rootExtents.Count == 0)
        {
            Note($"Directory radice illeggibile a partire dal cluster {_rootCluster}.");
            return;
        }
        if (rootBroken)
            Note("La catena della directory radice si interrompe prima del previsto: elenco parziale.");

        byte[] rootData = ReadExtents(rootExtents, MaxDirectoryBytes);

        // Primo giro sulla root: etichetta, bitmap di allocazione, up-case table.
        LoadVolumeMetadata(rootData, ct);

        // Secondo giro: albero vero e proprio, in ampiezza per non ricorrere sullo stack.
        var queue = new Queue<PendingDir>();
        var visited = new HashSet<uint>();
        if (_rootCluster >= 2) visited.Add(_rootCluster);

        ParseDirectory(Root, rootData, includeDeleted, false, false, 0, queue, visited, ct);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var pending = queue.Dequeue();
            if (_entryBudget <= 0) break;

            var extents = BuildChain(pending.FirstCluster, pending.NoFatChain, pending.DataLength,
                                     out bool broken, out int allocated, out _);

            // Cartella cancellata: la catena FAT viene azzerata alla cancellazione, quindi
            // l'unica ricostruzione possibile è presumere cluster contigui.
            if (broken && pending.Deleted && !pending.NoFatChain)
            {
                var guess = BuildChain(pending.FirstCluster, true, pending.DataLength,
                                       out bool guessBroken, out int guessAlloc, out _);
                // confronto i byte coperti, non il numero di extent: i cluster contigui si uniscono
                if (TotalLength(guess) > TotalLength(extents))
                {
                    extents = guess;
                    broken = guessBroken;
                    allocated = guessAlloc;
                    pending.Uncertain = true;
                    Note($"Catena FAT azzerata per la cartella cancellata \"{pending.Entry.FullPath}\": " +
                         "contenuto ricostruito presumendo cluster contigui.");
                }
            }

            if (extents.Count == 0)
            {
                Note($"Contenuto della cartella \"{pending.Entry.FullPath}\" non raggiungibile (cluster {pending.FirstCluster}).");
                pending.Entry.IsUncertain = true;
                continue;
            }
            if (broken && !pending.Deleted)
                Note($"Catena interrotta nella cartella \"{pending.Entry.FullPath}\": elenco parziale.");

            if (pending.Deleted && allocated > 0)
            {
                Note($"I cluster della cartella cancellata \"{pending.Entry.FullPath}\" risultano di nuovo allocati: " +
                     "il contenuto elencato potrebbe non essere il suo.");
                pending.Uncertain = true;
                pending.Entry.IsUncertain = true;
            }

            byte[] data = ReadExtents(extents, MaxDirectoryBytes);
            if (pending.Deleted && !LooksLikeDirectory(data))
            {
                Note($"I cluster della cartella cancellata \"{pending.Entry.FullPath}\" non contengono più voci di directory: contenuto perso.");
                pending.Entry.IsUncertain = true;
                continue;
            }

            ParseDirectory(pending.Entry, data, includeDeleted, pending.Deleted, pending.Uncertain,
                           pending.Depth, queue, visited, ct);
        }

        if (_entryBudget <= 0)
            Note($"Raggiunto il limite di {MaxEntries} voci: l'elenco è troncato (struttura probabilmente corrotta).");
    }

    private sealed class PendingDir
    {
        public FsEntry Entry;
        public uint FirstCluster;
        public bool NoFatChain;
        public long DataLength;
        public int Depth;
        public bool Deleted;

        /// <summary>Il contenuto della cartella è stato ricostruito a intuito: vale per i figli.</summary>
        public bool Uncertain;
    }

    /// <summary>Etichetta di volume, bitmap di allocazione e up-case table, tutte nella root.</summary>
    private void LoadVolumeMetadata(byte[] rootData, CancellationToken ct)
    {
        uint bitmapCluster = 0; long bitmapLength = 0; bool bitmapFound = false;
        uint upcaseCluster = 0; long upcaseLength = 0; uint upcaseChecksum = 0; bool upcaseFound = false;

        for (int i = 0; i + EntrySize <= rootData.Length; i += EntrySize)
        {
            byte raw = rootData[i];
            if (raw == TypeEndOfDirectory) break;

            bool inUse = (raw & 0x80) != 0;
            byte type = (byte)(raw | 0x80);

            if (!inUse) continue;   // i metadati cancellati non servono

            switch (type)
            {
                case TypeVolumeLabel:
                {
                    int chars = rootData[i + 1];
                    if (chars > 11) chars = 11;
                    if (chars > 0)
                        VolumeLabel = Encoding.Unicode.GetString(rootData, i + 2, chars * 2).TrimEnd('\0');
                    break;
                }

                case TypeBitmap:
                {
                    // con TexFAT ci sono due bitmap: prendo quella della FAT attiva
                    int which = rootData[i + 1] & 0x01;
                    if (bitmapFound && which != _activeFat) break;
                    if (!bitmapFound || which == _activeFat)
                    {
                        bitmapCluster = ByteUtils.U32LE(rootData, i + 20);
                        bitmapLength = (long)ByteUtils.U64LE(rootData, i + 24);
                        bitmapFound = true;
                    }
                    break;
                }

                case TypeUpcase:
                {
                    upcaseChecksum = ByteUtils.U32LE(rootData, i + 4);
                    upcaseCluster = ByteUtils.U32LE(rootData, i + 20);
                    upcaseLength = (long)ByteUtils.U64LE(rootData, i + 24);
                    upcaseFound = true;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(VolumeLabel))
            Note("Etichetta di volume assente o vuota.");

        // --- bitmap di allocazione ---
        if (bitmapFound && bitmapCluster >= 2 && bitmapLength > 0)
        {
            long needed = ((long)_clusterCount + 7) / 8;
            if (bitmapLength < needed)
                Note($"Bitmap di allocazione più corta del previsto ({bitmapLength} byte invece di {needed}).");

            var ext = BuildChain(bitmapCluster, false, bitmapLength, out bool broken, out _, out _);
            if (ext.Count == 0)
                Note("Bitmap di allocazione irraggiungibile: impossibile sapere quali cluster sono liberi.");
            else
            {
                if (broken) Note("Catena della bitmap di allocazione interrotta: verifica dei cluster parziale.");
                _bitmap = new PagedArea(Source, ext, bitmapLength, BitmapPageSize, 64);
                _bitmapBits = Math.Min((long)_clusterCount, bitmapLength * 8);
            }
        }
        else
        {
            Note("Voce 0x81 (bitmap di allocazione) non trovata nella radice: niente controllo di riuso dei cluster.");
        }

        // --- up-case table ---
        if (upcaseFound && upcaseCluster >= 2 && upcaseLength >= 2)
        {
            if (upcaseLength > 65536L * 2)
            {
                Note($"Up-case table di dimensione anomala ({upcaseLength} byte): troncata a 128 KB.");
                upcaseLength = 65536L * 2;
            }

            var ext = BuildChain(upcaseCluster, false, upcaseLength, out bool broken, out _, out _);
            if (ext.Count == 0 || broken)
            {
                Note("Up-case table illeggibile: uso la tabella maiuscole standard di sistema.");
                _upcase = BuildFallbackUpcase();
                _upcaseIsFallback = true;
            }
            else
            {
                byte[] raw = ReadExtents(ext, upcaseLength);
                uint calc = UpcaseChecksum(raw, raw.Length);
                if (calc != upcaseChecksum)
                    Note($"Checksum della up-case table non corrispondente (atteso 0x{upcaseChecksum:X8}, calcolato 0x{calc:X8}): tabella usata comunque.");
                _upcase = DecodeUpcase(raw);
            }
        }
        else
        {
            Note("Voce 0x82 (up-case table) non trovata nella radice: uso la tabella maiuscole standard di sistema.");
            _upcase = BuildFallbackUpcase();
            _upcaseIsFallback = true;
        }

        ct.ThrowIfCancellationRequested();
    }

    // ------------------------------------------------------------------
    // Parsing di una directory
    // ------------------------------------------------------------------

    private void ParseDirectory(FsEntry parent, byte[] data, bool includeDeleted, bool parentDeleted,
                                bool parentUncertain, int depth, Queue<PendingDir> queue,
                                HashSet<uint> visited, CancellationToken ct)
    {
        bool afterEnd = false;
        int orphanSecondaries = 0;

        int i = 0;
        while (i + EntrySize <= data.Length)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (_entryBudget <= 0) return;

            byte raw = data[i];

            if (raw == TypeEndOfDirectory)
            {
                // Fine dell'elenco vivo. Oltre questo punto restano solo residui:
                // li esamino solo se mi hanno chiesto i cancellati.
                if (!includeDeleted) break;
                afterEnd = true;
                i += EntrySize;
                continue;
            }

            bool inUse = (raw & 0x80) != 0;
            byte type = (byte)(raw | 0x80);

            // dopo il marcatore di fine accetto solo set di file cancellati
            if (afterEnd && (inUse || type != TypeFile))
            {
                i += EntrySize;
                continue;
            }

            if (type == TypeFile)
            {
                // il set cancellato va saltato per intero, altrimenti le sue 0xC0/0xC1
                // sembrerebbero voci secondarie orfane
                if (!inUse && !includeDeleted) { i += SetLength(data, i) * EntrySize; continue; }

                int consumed = ParseFileSet(data, i, inUse, parentDeleted || !inUse, parentUncertain,
                                            parent, depth, queue, visited, afterEnd);
                i += consumed * EntrySize;
                continue;
            }

            switch (type)
            {
                case TypeBitmap:
                case TypeUpcase:
                case TypeVolumeLabel:
                    break;   // già gestite in LoadVolumeMetadata (solo nella root)

                case TypeVolumeGuid:
                    break;   // benigna, nessun dato utile per il recupero

                case TypeTexFatPadding:
                    Note("Presenti voci di riempimento TexFAT (0xA1): volume con supporto transazionale, gestito come exFAT normale.");
                    break;

                case TypeWinCeAcl:
                    Note("Presente una tabella ACL Windows CE (0xA2): ignorata, non influisce sui dati.");
                    break;

                case TypeVendorExt:
                case TypeVendorAlloc:
                    Note($"Presenti estensioni di produttore (0x{type:X2}): ignorate.");
                    break;

                case TypeStreamExt:
                case TypeFileName:
                    // le secondarie orfane cancellate sono normali residui, quelle vive no
                    if (inUse) orphanSecondaries++;
                    break;

                default:
                    if ((type & 0x20) == 0)
                        Note($"Voce di directory critica sconosciuta 0x{type:X2} in \"{DisplayPath(parent)}\": ignorata, potrebbero mancare dei file.");
                    else
                        Note($"Voce di directory secondaria/benigna sconosciuta 0x{type:X2}: ignorata.");
                    break;
            }

            i += EntrySize;
        }

        if (orphanSecondaries > 0)
            Note($"{orphanSecondaries} voci secondarie orfane (0xC0/0xC1 senza 0x85) in \"{DisplayPath(parent)}\": set di directory danneggiati.");
    }

    /// <summary>
    /// Legge un set 0x85 + 0xC0 + 0xC1... e aggiunge la voce. Ritorna quante voci da 32 byte consumare.
    /// </summary>
    private int ParseFileSet(byte[] data, int start, bool inUse, bool deleted, bool parentUncertain,
                             FsEntry parent, int depth, Queue<PendingDir> queue,
                             HashSet<uint> visited, bool afterEnd)
    {
        int secondary = CountSecondaries(data, start, out bool countSuspect);

        if (secondary < 1)
        {
            Note($"Set di directory senza estensione di stream in \"{DisplayPath(parent)}\": voce saltata.");
            return 1;
        }

        int streamOff = start + EntrySize;
        if ((byte)(data[streamOff] | 0x80) != TypeStreamExt)
        {
            Note($"Voce 0x85 non seguita da 0xC0 in \"{DisplayPath(parent)}\": set saltato.");
            return 1;
        }

        // ---- voce file (0x85) ----
        ushort setChecksum = ByteUtils.U16LE(data, start + 2);
        ushort attributes = ByteUtils.U16LE(data, start + 4);
        uint tCreate = ByteUtils.U32LE(data, start + 8);
        uint tModify = ByteUtils.U32LE(data, start + 12);
        uint tAccess = ByteUtils.U32LE(data, start + 16);
        byte create10 = data[start + 20];
        byte modify10 = data[start + 21];
        byte createUtc = data[start + 22];
        byte modifyUtc = data[start + 23];
        byte accessUtc = data[start + 24];

        // ---- estensione di stream (0xC0) ----
        byte secFlags = data[streamOff + 1];
        bool noFatChain = (secFlags & 0x02) != 0;
        bool allocPossible = (secFlags & 0x01) != 0;
        int nameLength = data[streamOff + 3];
        ushort nameHashStored = ByteUtils.U16LE(data, streamOff + 4);
        long validDataLength = unchecked((long)ByteUtils.U64LE(data, streamOff + 8));
        uint firstCluster = ByteUtils.U32LE(data, streamOff + 20);
        long dataLength = unchecked((long)ByteUtils.U64LE(data, streamOff + 24));

        bool isDirectory = (attributes & 0x10) != 0;
        bool uncertain = countSuspect || parentUncertain;

        // ---- nome (0xC1) ----
        var sb = new StringBuilder(nameLength > 0 && nameLength <= 255 ? nameLength : 32);
        int nameEntries = 0;
        for (int k = 2; k <= secondary; k++)
        {
            int o = start + k * EntrySize;
            if (o + EntrySize > data.Length) break;
            if ((byte)(data[o] | 0x80) != TypeFileName) break;
            sb.Append(Encoding.Unicode.GetString(data, o + 2, NameCharsPerEntry * 2));
            nameEntries++;
        }

        string name = sb.ToString();
        if (nameLength > 0 && nameLength <= name.Length)
        {
            name = name.Substring(0, nameLength);
        }
        else
        {
            // NameLength incoerente con le voci presenti: taglio al primo NUL
            int z = name.IndexOf('\0');
            if (z >= 0) name = name.Substring(0, z);
            if (nameLength != name.Length) uncertain = true;
        }

        int expectedNameEntries = nameLength > 0 ? (nameLength + NameCharsPerEntry - 1) / NameCharsPerEntry : 0;
        if (expectedNameEntries > nameEntries)
        {
            Note($"Nome incompleto in \"{DisplayPath(parent)}\": mancano voci 0xC1 (attese {expectedNameEntries}, trovate {nameEntries}).");
            uncertain = true;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = firstCluster != 0 ? $"senza_nome_{firstCluster}" : $"senza_nome_{start / EntrySize}";
            uncertain = true;
        }

        int consumed = 1 + secondary;

        // ---- verifiche di integrità ----
        // Per un set cancellato il bit 7 dei byte di tipo è stato azzerato: rimettendolo si
        // ricostruiscono i byte originali e il SetChecksum torna a quadrare. È la prova
        // migliore che il nome e i cluster recuperati siano quelli veri.
        ushort calcChecksum = EntrySetChecksum(data, start, consumed, forceInUse: true);
        bool checksumOk = calcChecksum == setChecksum && !countSuspect;
        if (!checksumOk) uncertain = true;

        // Il name hash va calcolato sul nome così com'e' sul disco, prima di qualunque pulizia.
        ushort nameHashCalc = ComputeNameHash(name);
        bool hashOk = nameHashCalc == nameHashStored;
        if (!hashOk)
        {
            uncertain = true;
            Note($"Name hash non corrispondente per \"{Printable(name)}\" in \"{DisplayPath(parent)}\" " +
                 $"(memorizzato 0x{nameHashStored:X4}, calcolato 0x{nameHashCalc:X4}): nome poco affidabile.");
        }

        // in exFAT i codici sotto 0x20 non sono ammessi: se ci sono, il nome è un residuo
        if (HasControlChars(name))
        {
            name = Printable(name);
            uncertain = true;
            Note($"Nome con caratteri di controllo in \"{DisplayPath(parent)}\": ripulito, probabilmente corrotto.");
        }

        if (!inUse && !checksumOk)
            Note($"Checksum del set non valido per la voce cancellata \"{name}\": metadati parzialmente sovrascritti.");

        // ---- dimensioni ----
        if (dataLength < 0 || (_volumeBytes > 0 && dataLength > _volumeBytes))
        {
            Note($"Dimensione non plausibile per \"{name}\" ({dataLength} byte): azzerata.");
            dataLength = 0;
            uncertain = true;
        }
        if (validDataLength < 0 || validDataLength > dataLength)
        {
            if (dataLength > 0)
                Note($"\"{name}\": ValidDataLength ({validDataLength}) incoerente con DataLength ({dataLength}).");
            validDataLength = dataLength;
        }
        else if (validDataLength < dataLength && !isDirectory)
        {
            Note($"\"{name}\": solo {validDataLength} byte su {dataLength} sono stati scritti davvero; " +
                 "la coda del file contiene dati non validi.");
        }

        if (!allocPossible && dataLength > 0)
        {
            Note($"\"{name}\": flag AllocationPossible a 0 ma dimensione non nulla, struttura incoerente.");
            uncertain = true;
        }

        // ---- voce ----
        var entry = new FsEntry
        {
            Name = name,
            IsDirectory = isDirectory,
            IsDeleted = deleted || !inUse,
            Length = isDirectory ? 0 : dataLength,
            Created = ExFatTime(tCreate, create10, createUtc),
            Modified = ExFatTime(tModify, modify10, modifyUtc),
        };

        var info = new ExFatEntryInfo
        {
            FirstCluster = firstCluster,
            NoFatChain = noFatChain,
            ValidDataLength = validDataLength,
            DataLength = dataLength,
            Attributes = attributes,
            NameHashStored = nameHashStored,
            NameHashComputed = nameHashCalc,
            NameHashOk = hashOk,
            SetChecksumOk = checksumOk,
            Accessed = ExFatTime(tAccess, 0, accessUtc),
        };
        entry.Tag = info;

        // ---- catena dei cluster ----
        if (!isDirectory && dataLength > 0)
        {
            bool chainBroken;
            bool contiguousGuess = false;

            var extents = BuildChain(firstCluster, noFatChain, dataLength, out chainBroken, out int allocatedSeen, out int used);

            if (chainBroken && !noFatChain && entry.IsDeleted)
            {
                // Alla cancellazione la catena FAT viene azzerata: l'unica ricostruzione
                // possibile è presumere che il file fosse contiguo.
                var guess = BuildChain(firstCluster, true, dataLength,
                                       out bool guessBroken, out int guessAlloc, out int guessUsed);
                if (TotalLength(guess) > TotalLength(extents))
                {
                    extents = guess;
                    contiguousGuess = true;
                    chainBroken = guessBroken;
                    allocatedSeen = guessAlloc;
                    used = guessUsed;
                    Note($"Catena FAT azzerata per la voce cancellata \"{name}\": dati ricostruiti presumendo cluster contigui.");
                }
            }

            if (extents.Count == 0)
            {
                Note(firstCluster != 0
                     ? $"Cluster iniziale non valido ({firstCluster}) per \"{name}\": dati non recuperabili."
                     : $"\"{name}\" dichiara {dataLength} byte ma non ha un cluster iniziale: dati non recuperabili.");
                uncertain = true;
            }

            if (chainBroken)
            {
                Note($"Catena dei cluster interrotta o ciclica per \"{name}\": recuperati {used} cluster su " +
                     $"{(dataLength + _clusterSize - 1) / _clusterSize} attesi.");
                uncertain = true;
            }

            info.ChainBroken = chainBroken;
            info.ContiguousGuess = contiguousGuess;
            info.ClustersUsed = used;
            info.ClustersAllocated = allocatedSeen > 0;

            if (entry.IsDeleted && allocatedSeen > 0)
            {
                Note($"I cluster di \"{name}\" risultano di nuovo allocati nella bitmap: i dati potrebbero essere già stati sovrascritti.");
                uncertain = true;
            }
            else if (!entry.IsDeleted && _bitmap != null && used > 0 && allocatedSeen < used)
            {
                Note($"Alcuni cluster di \"{name}\" risultano liberi nella bitmap: bitmap e FAT non concordano.");
                uncertain = true;
            }

            entry.Extents = extents;
        }

        if (afterEnd)
            Note($"Voce \"{name}\" recuperata oltre il marcatore di fine di \"{DisplayPath(parent)}\": " +
                 "residuo di una cartella che prima conteneva più file.");

        entry.IsUncertain = uncertain;
        AddChild(parent, entry);
        _entryBudget--;

        // ---- ricorsione nelle cartelle ----
        if (isDirectory && depth < MaxDirectoryDepth && firstCluster >= 2 && firstCluster < 2 + _clusterCount)
        {
            if (visited.Add(firstCluster))
            {
                long dirLength = dataLength;
                if (dirLength <= 0 && entry.IsDeleted) dirLength = _clusterSize;   // almeno un cluster
                queue.Enqueue(new PendingDir
                {
                    Entry = entry,
                    FirstCluster = firstCluster,
                    NoFatChain = noFatChain,
                    DataLength = dirLength,
                    Depth = depth + 1,
                    Deleted = entry.IsDeleted,
                    Uncertain = uncertain
                });
            }
            else
            {
                Note($"Cartella \"{entry.FullPath}\" punta a un cluster già visitato ({firstCluster}): ciclo interrotto.");
                entry.IsUncertain = true;
            }
        }
        else if (isDirectory && depth >= MaxDirectoryDepth)
        {
            Note($"Profondità massima ({MaxDirectoryDepth}) raggiunta a \"{entry.FullPath}\": discesa interrotta.");
            entry.IsUncertain = true;
        }

        return consumed;
    }

    /// <summary>
    /// Numero di voci secondarie di un set 0x85. Se SecondaryCount non è credibile lo ricostruisce
    /// guardando i tipi delle voci che seguono e segnala il sospetto.
    /// </summary>
    private static int CountSecondaries(byte[] data, int start, out bool suspect)
    {
        suspect = false;
        int avail = (data.Length - start) / EntrySize - 1;
        if (avail < 1) return 0;

        int secondary = data[start + 1];
        if (secondary >= 1 && secondary <= MaxSecondaryEntries && secondary <= avail)
            return secondary;

        suspect = true;
        int j = 1;
        if ((byte)(data[start + EntrySize] | 0x80) == TypeStreamExt)
        {
            j++;
            while (j <= avail && j <= MaxSecondaryEntries &&
                   (byte)(data[start + j * EntrySize] | 0x80) == TypeFileName) j++;
        }
        return j - 1;
    }

    /// <summary>Voci da saltare per oltrepassare un set di directory, minimo una.</summary>
    private static int SetLength(byte[] data, int start)
    {
        int secondary = CountSecondaries(data, start, out _);
        return secondary > 0 ? 1 + secondary : 1;
    }

    /// <summary>Controllo grossolano: i primi byte assomigliano a voci di directory?</summary>
    private static bool LooksLikeDirectory(byte[] data)
    {
        if (data == null || data.Length < EntrySize) return false;

        int plausible = 0, examined = 0;
        for (int i = 0; i + EntrySize <= data.Length && examined < 16; i += EntrySize, examined++)
        {
            byte raw = data[i];
            if (raw == 0) { plausible++; continue; }
            byte type = (byte)(raw | 0x80);
            if (type is TypeFile or TypeStreamExt or TypeFileName or TypeBitmap or TypeUpcase
                     or TypeVolumeLabel or TypeVolumeGuid or TypeTexFatPadding or TypeWinCeAcl
                     or TypeVendorExt or TypeVendorAlloc)
                plausible++;
        }
        return examined > 0 && plausible * 2 > examined;
    }

    private static string DisplayPath(FsEntry e) => string.IsNullOrEmpty(e.FullPath) ? "/" : e.FullPath;

    private static bool HasControlChars(string s)
    {
        foreach (char c in s) if (c < 0x20) return true;
        return false;
    }

    /// <summary>Sostituisce i caratteri di controllo: servono solo a non sporcare log e interfaccia.</summary>
    private static string Printable(string s)
    {
        if (!HasControlChars(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < 0x20 ? '_' : c);
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Cluster e FAT
    // ------------------------------------------------------------------

    private long ClusterToOffset(uint cluster) =>
        _clusterHeapOffsetBytes + (long)(cluster - 2) * _clusterSize;

    private bool IsValidCluster(uint c) => c >= 2 && c < 2 + (ulong)_clusterCount;

    private uint ReadFat(uint cluster)
    {
        if (_fat == null) return 0xFFFFFFFF;
        long off = (long)cluster * 4;
        if (off + 4 > _fat.Length) return 0xFFFFFFFF;
        var page = _fat.Page(off / FatPageSize);
        return ByteUtils.U32LE(page, (int)(off % FatPageSize));
    }

    /// <summary>Vero se il cluster risulta occupato nella bitmap di allocazione.</summary>
    private bool IsAllocated(uint cluster)
    {
        if (_bitmap == null) return false;
        long bit = (long)cluster - 2;
        if (bit < 0 || bit >= _bitmapBits) return false;
        long byteOff = bit / 8;
        var page = _bitmap.Page(byteOff / BitmapPageSize);
        return (page[(int)(byteOff % BitmapPageSize)] & (1 << (int)(bit & 7))) != 0;
    }

    private List<FsExtent> BuildChain(uint firstCluster, bool noFatChain, long dataLength,
                                      out bool broken, out int allocatedCount, out int clustersUsed)
        => BuildChain(firstCluster, noFatChain, dataLength, MaxClustersPerChain,
                      out broken, out allocatedCount, out clustersUsed);

    /// <summary>
    /// Extent in byte assoluti per una catena. Con <paramref name="noFatChain"/> i cluster sono
    /// contigui a partire da <paramref name="firstCluster"/>; altrimenti si segue la FAT a 32 bit.
    /// Con <paramref name="dataLength"/> a 0 la lunghezza non è nota e si segue la catena fino al
    /// suo marcatore di fine, al massimo per <paramref name="maxClusters"/> cluster (serve per la root).
    /// Le catene rotte o cicliche vengono interrotte senza eccezioni, con <paramref name="broken"/> a true.
    /// </summary>
    private List<FsExtent> BuildChain(uint firstCluster, bool noFatChain, long dataLength, long maxClusters,
                                      out bool broken, out int allocatedCount, out int clustersUsed)
    {
        var result = new List<FsExtent>();
        broken = false;
        allocatedCount = 0;
        clustersUsed = 0;

        if (!IsValidCluster(firstCluster))
        {
            broken = firstCluster != 0;
            return result;
        }

        bool unknownLength = dataLength <= 0;
        long needed;
        if (unknownLength)
        {
            needed = Math.Min(Math.Max(1, maxClusters), MaxClustersPerChain);
        }
        else
        {
            needed = (dataLength + _clusterSize - 1) / _clusterSize;
            if (needed > MaxClustersPerChain) { needed = MaxClustersPerChain; broken = true; }
        }

        long remaining = unknownLength ? long.MaxValue : dataLength;
        uint current = firstCluster;
        bool reachedEnd = false;

        // rilevamento cicli: un insieme dei cluster già visti, con tetto sulla memoria
        HashSet<uint> seen = noFatChain ? null : new HashSet<uint>();

        long guard = 0;

        while (clustersUsed < needed && guard++ <= needed)
        {
            if (!IsValidCluster(current)) { broken = true; break; }

            long off = ClusterToOffset(current);
            long len = _clusterSize;
            if (remaining != long.MaxValue && len > remaining) len = remaining;
            if (len <= 0) break;

            // unisco gli extent adiacenti
            if (result.Count > 0)
            {
                var last = result[^1];
                if (last.Offset + last.Length == off)
                {
                    last.Length += len;
                    result[^1] = last;
                }
                else result.Add(new FsExtent(off, len));
            }
            else result.Add(new FsExtent(off, len));

            if (IsAllocated(current)) allocatedCount++;
            clustersUsed++;
            if (remaining != long.MaxValue) remaining -= len;
            if (remaining == 0) break;

            uint next;
            if (noFatChain)
            {
                next = current + 1;
            }
            else
            {
                next = ReadFat(current);
                if (next == 0xFFFFFFFF) { reachedEnd = true; break; }  // fine catena
                if (next == 0 || next == 1 || next == current) { broken = true; break; }
                if (next >= 0xFFFFFFF7) { broken = true; break; }      // cluster segnato danneggiato
                // oltre il tetto di memoria il ciclo non si rileva più, ma il numero di
                // cluster attesi limita comunque il giro
                if (seen != null && seen.Count < (1 << 20) && !seen.Add(current)) { broken = true; break; }
            }

            current = next;
        }

        // lunghezza nota: mancano dei byte. Lunghezza ignota: la catena non è finita col suo marcatore.
        if (!unknownLength && remaining > 0) broken = true;
        if (unknownLength && !reachedEnd) broken = true;

        return result;
    }

    private static long TotalLength(List<FsExtent> extents)
    {
        long total = 0;
        foreach (var e in extents) total += Math.Max(0, e.Length);
        return total;
    }

    /// <summary>Copia il contenuto degli extent in un buffer unico (usato per le directory e le tabelle).</summary>
    private byte[] ReadExtents(List<FsExtent> extents, long maxBytes)
    {
        long total = 0;
        foreach (var e in extents) total += Math.Max(0, e.Length);
        if (total > maxBytes) total = maxBytes;
        if (total <= 0) return Array.Empty<byte>();

        var buffer = new byte[total];
        long pos = 0;
        foreach (var e in extents)
        {
            if (pos >= total) break;
            long take = Math.Min(e.Length, total - pos);
            long done = 0;
            while (done < take)
            {
                int chunk = (int)Math.Min(1 << 20, take - done);
                Source.ReadBytes(e.Offset + done, chunk, buffer, (int)(pos + done));
                done += chunk;
            }
            pos += take;
        }
        return buffer;
    }

    // ------------------------------------------------------------------
    // Up-case table, hash, checksum, date
    // ------------------------------------------------------------------

    /// <summary>Espande la up-case table (formato compresso: 0xFFFF + N = N caratteri identici).</summary>
    private static char[] DecodeUpcase(byte[] raw)
    {
        var table = new char[65536];
        for (int i = 0; i < 65536; i++) table[i] = (char)i;

        int index = 0;
        bool skip = false;
        for (int i = 0; i + 1 < raw.Length && index < 65536; i += 2)
        {
            ushort v = ByteUtils.U16LE(raw, i);
            if (skip)
            {
                index += v;          // v caratteri mappati su se stessi
                skip = false;
            }
            else if (v == 0xFFFF)
            {
                skip = true;
            }
            else
            {
                table[index++] = (char)v;
            }
        }
        return table;
    }

    /// <summary>Tabella di riserva quando la 0x82 non è leggibile: maiuscole invarianti.</summary>
    private static char[] BuildFallbackUpcase()
    {
        var table = new char[65536];
        for (int i = 0; i < 65536; i++)
        {
            char c = (char)i;
            // le surrogate restano intatte, altrimenti si perde la coppia
            table[i] = char.IsSurrogate(c) ? c : char.ToUpperInvariant(c);
        }
        return table;
    }

    private char UpCase(char c)
    {
        var t = _upcase;
        return t != null ? t[c] : char.ToUpperInvariant(c);
    }

    /// <summary>Hash del nome come lo calcola exFAT: sul nome maiuscolo, byte per byte in little endian.</summary>
    private ushort ComputeNameHash(string name)
    {
        ushort hash = 0;
        foreach (char ch in name)
        {
            char u = UpCase(ch);
            byte lo = (byte)(u & 0xFF);
            byte hi = (byte)(u >> 8);
            hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + lo);
            hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + hi);
        }
        return hash;
    }

    /// <summary>
    /// Checksum di un set di voci: si saltano i byte 2-3 della prima (dove il checksum è scritto).
    /// Con <paramref name="forceInUse"/> il bit 7 dei byte di tipo viene rimesso, così il calcolo
    /// funziona anche sui set cancellati.
    /// </summary>
    private static ushort EntrySetChecksum(byte[] data, int start, int entryCount, bool forceInUse)
    {
        ushort sum = 0;
        int bytes = entryCount * EntrySize;
        if (start + bytes > data.Length) bytes = data.Length - start;

        for (int i = 0; i < bytes; i++)
        {
            if (i == 2 || i == 3) continue;
            byte v = data[start + i];
            if (forceInUse && (i % EntrySize) == 0) v |= 0x80;
            sum = (ushort)(((sum & 1) != 0 ? 0x8000 : 0) + (sum >> 1) + v);
        }
        return sum;
    }

    private static uint UpcaseChecksum(byte[] data, int length)
    {
        uint sum = 0;
        for (int i = 0; i < length && i < data.Length; i++)
            sum = ((sum & 1) != 0 ? 0x80000000u : 0u) + (sum >> 1) + data[i];
        return sum;
    }

    /// <summary>
    /// Timestamp exFAT: campo a 32 bit in formato DOS più il campo da 10 ms e lo scostamento UTC.
    /// </summary>
    private static DateTime? ExFatTime(uint value, byte tenMs, byte utcOffset)
    {
        if (value == 0) return null;

        int seconds = (int)(value & 0x1F) * 2;
        int minute = (int)((value >> 5) & 0x3F);
        int hour = (int)((value >> 11) & 0x1F);
        int day = (int)((value >> 16) & 0x1F);
        int month = (int)((value >> 21) & 0x0F);
        int year = 1980 + (int)((value >> 25) & 0x7F);

        if (month < 1 || month > 12 || day < 1 || day > 31 ||
            hour > 23 || minute > 59 || seconds > 58) return null;

        try
        {
            if (day > DateTime.DaysInMonth(year, month)) return null;
            var dt = new DateTime(year, month, day, hour, minute, seconds, DateTimeKind.Unspecified);

            if (tenMs > 0 && tenMs < 200) dt = dt.AddMilliseconds(tenMs * 10);

            // bit 7 = scostamento valido, bit 0-6 = valore con segno in quarti d'ora
            if ((utcOffset & 0x80) != 0)
            {
                int quarters = utcOffset & 0x7F;
                if (quarters >= 0x40) quarters -= 0x80;
                var utc = DateTime.SpecifyKind(dt.AddMinutes(-quarters * 15), DateTimeKind.Utc);
                return utc.ToLocalTime();
            }

            return dt;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // Note
    // ------------------------------------------------------------------

    private void Note(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Notes.Count >= MaxNotes) return;
        if (!_noteSet.Add(text)) return;
        Notes.Add(text);
        if (Notes.Count == MaxNotes) Notes.Add("... altre osservazioni omesse.");
    }

    // ------------------------------------------------------------------
    // Area paginata su extent (FAT e bitmap, che possono essere enormi)
    // ------------------------------------------------------------------

    private sealed class PagedArea
    {
        private readonly IBlockSource _source;
        private readonly List<FsExtent> _extents;
        private readonly int _pageSize;
        private readonly int _maxPages;
        private readonly Dictionary<long, byte[]> _cache = new();
        private readonly Queue<long> _order = new();

        public long Length { get; }

        public PagedArea(IBlockSource source, List<FsExtent> extents, long length, int pageSize, int maxPages)
        {
            _source = source;
            _extents = extents ?? new List<FsExtent>();
            _pageSize = pageSize;
            _maxPages = Math.Max(4, maxPages);
            Length = length;
        }

        public byte[] Page(long index)
        {
            if (_cache.TryGetValue(index, out var cached)) return cached;

            var buffer = new byte[_pageSize];
            ReadLogical(index * _pageSize, buffer, _pageSize);

            if (_cache.Count >= _maxPages)
            {
                long oldest = _order.Dequeue();
                _cache.Remove(oldest);
            }
            _cache[index] = buffer;
            _order.Enqueue(index);
            return buffer;
        }

        /// <summary>Legge dall'area logica (concatenazione degli extent) nel buffer.</summary>
        private void ReadLogical(long offset, byte[] destination, int count)
        {
            long pos = 0;
            int written = 0;

            foreach (var e in _extents)
            {
                if (written >= count) break;
                long extentLength = Math.Max(0, e.Length);
                if (offset >= pos + extentLength) { pos += extentLength; continue; }

                long insideExtent = Math.Max(0, offset - pos);
                long available = extentLength - insideExtent;
                int take = (int)Math.Min(available, count - written);
                if (take > 0)
                {
                    _source.ReadBytes(e.Offset + insideExtent, take, destination, written);
                    written += take;
                    offset += take;
                }
                pos += extentLength;
            }
        }
    }
}
