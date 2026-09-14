using DVDRescue.Core;
using DVDRescue.Native;

namespace DVDRescue.Recovery;

/// <summary>
/// Espone un lettore ottico come sorgente di blocchi.
///
/// La velocità qui dipende quasi tutta da come ci si comporta sugli errori. Un lettore che
/// riceve una richiesta su un'area non scritta ritenta per conto suo prima di rispondere:
/// con un timeout generoso e tre tentativi per settore, poche migliaia di settori vuoti
/// diventano minuti di attesa. Per questo il comportamento predefinito è "veloce":
/// un tentativo, timeout corto, e su un blocco illeggibile si rinuncia in fretta invece di
/// suddividerlo fino al singolo settore.
///
/// La modalità insistente serve solo ai dischi rovinati, dove ha senso spendere tempo per
/// strappare al lettore qualche settore in più.
/// </summary>
public sealed class OpticalBlockSource : BlockSourceBase
{
    private readonly OpticalDrive _drive;
    private readonly bool _ownsDrive;
    private readonly byte[] _probe = new byte[2048];
    private long _totalBlocks;
    private long _bad;

    public override string Name { get; }
    public override int BlockSize => 2048;
    public override long TotalBlocks => _totalBlocks;
    public override long BadBlockCount => _bad;

    /// <summary>Ritentativi e suddivisione del blocco: lento ma recupera di più.</summary>
    public bool ThoroughMode { get; set; }

    /// <summary>Timeout di una lettura normale, in secondi.</summary>
    public int ReadTimeout { get; set; } = 10;

    /// <summary>Timeout dei sondaggi usati per trovare il limite dell'area scritta.</summary>
    public int ProbeTimeout { get; set; } = 4;

    /// <summary>Settori illeggibili di fila oltre i quali si considera finita l'area scritta.</summary>
    public long ConsecutiveFailuresLimit { get; set; } = 8192;   // 16 MB

    /// <summary>Vero quando il limite qui sopra è stato superato: chi scandisce può fermarsi.</summary>
    public bool ReachedEndOfData { get; private set; }

    public long ConsecutiveFailures { get; private set; }
    public List<long> BadSectors { get; } = new();

    public OpticalBlockSource(OpticalDrive drive, long totalBlocks, bool ownsDrive = true)
    {
        _drive = drive;
        _totalBlocks = totalBlocks;
        _ownsDrive = ownsDrive;
        Name = $"Unità {drive.DriveLetter}:";
    }

    public void SetTotalBlocks(long blocks) => _totalBlocks = blocks;

    public void ResetEndOfData()
    {
        ReachedEndOfData = false;
        ConsecutiveFailures = 0;
    }

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
        => ReadBlocks(block, count, destination, destinationOffset, 0);

    private int ReadBlocks(long block, int count, byte[] destination, int destinationOffset, int depth)
    {
        if (count <= 0) return 0;

        if (_drive.ReadSectors(block, count, destination, destinationOffset, ReadTimeout).Success)
        {
            ConsecutiveFailures = 0;
            _readSomething = true;
            return count;
        }

        if (count == 1) return ReadSingle(block, destination, destinationOffset) ? 1 : 0;

        // In modalità veloce la suddivisione si ferma presto: un blocco da 512 settori
        // tutto illeggibile costerebbe altrimenti più di mille comandi al lettore.
        int maxDepth = ThoroughMode ? 12 : 2;

        if (depth >= maxDepth)
        {
            Array.Clear(destination, destinationOffset, count * 2048);
            RegisterFailure(block, count);
            return 0;
        }

        int half = count / 2;
        int ok = ReadBlocks(block, half, destination, destinationOffset, depth + 1);
        ok += ReadBlocks(block + half, count - half, destination, destinationOffset + half * 2048, depth + 1);
        return ok;
    }

    private bool ReadSingle(long block, byte[] destination, int destinationOffset)
    {
        int attempts = ThoroughMode ? 3 : 1;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (_drive.ReadSectors(block, 1, destination, destinationOffset, ReadTimeout).Success)
            {
                ConsecutiveFailures = 0;
                _readSomething = true;
                return true;
            }

            if (ThoroughMode &&
                _drive.ReadSectorsAlternate(block, 1, destination, destinationOffset, ReadTimeout).Success)
            {
                ConsecutiveFailures = 0;
                _readSomething = true;
                return true;
            }

            if (attempt + 1 < attempts) Thread.Sleep(20);
        }

        Array.Clear(destination, destinationOffset, 2048);
        RegisterFailure(block, 1);
        return false;
    }

    private bool _readSomething;

    private void RegisterFailure(long block, int count)
    {
        _bad += count;
        ConsecutiveFailures += count;

        if (BadSectors.Count < 50000) BadSectors.Add(block);

        // "fine dell'area scritta" ha senso solo dopo aver letto qualcosa: se il disco comincia
        // con una zona illeggibile, fermarsi lì vorrebbe dire non trovare mai niente
        if (_readSomething && ConsecutiveFailures >= ConsecutiveFailuresLimit) ReachedEndOfData = true;
    }

    /// <summary>Lettura secca, timeout corto, nessun ritentativo: serve solo a sondare.</summary>
    public bool ProbeSector(long block)
        => _drive.ReadSectors(block, 1, _probe, 0, ProbeTimeout).Success;

    public override void Dispose()
    {
        if (_ownsDrive) _drive?.Dispose();
        base.Dispose();
    }
}
