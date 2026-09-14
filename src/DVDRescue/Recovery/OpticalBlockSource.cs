using DVDRescue.Core;
using DVDRescue.Native;

namespace DVDRescue.Recovery;

/// <summary>
/// Espone un lettore ottico come sorgente di blocchi, con ritentativi a scalare:
/// blocco grande → blocchi piccoli → settore singolo → settore azzerato e contato.
/// Così un graffio non interrompe il recupero di tutto il resto.
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

    public int RetriesPerSector { get; set; } = 3;
    public bool UseAlternateRead { get; set; } = true;
    public List<long> BadSectors { get; } = new();

    public OpticalBlockSource(OpticalDrive drive, long totalBlocks, bool ownsDrive = true)
    {
        _drive = drive;
        _totalBlocks = totalBlocks;
        _ownsDrive = ownsDrive;
        Name = $"Unità {drive.DriveLetter}:";
    }

    public void SetTotalBlocks(long blocks) => _totalBlocks = blocks;

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        if (count <= 0) return 0;

        var result = _drive.ReadSectors(block, count, destination, destinationOffset);
        if (result.Success) return count;

        if (count == 1) return ReadOneWithRetry(block, destination, destinationOffset) ? 1 : 0;

        int half = count / 2;
        int ok = ReadBlocks(block, half, destination, destinationOffset);
        ok += ReadBlocks(block + half, count - half, destination, destinationOffset + half * 2048);
        return ok;
    }

    private bool ReadOneWithRetry(long block, byte[] destination, int destinationOffset)
    {
        for (int attempt = 0; attempt < RetriesPerSector; attempt++)
        {
            if (_drive.ReadSectors(block, 1, destination, destinationOffset).Success) return true;
            if (UseAlternateRead && _drive.ReadSectorsAlternate(block, 1, destination, destinationOffset).Success) return true;
            Thread.Sleep(25);
        }

        Array.Clear(destination, destinationOffset, 2048);
        _bad++;
        if (BadSectors.Count < 200000) BadSectors.Add(block);
        return false;
    }

    /// <summary>Lettura secca senza ritentativi: serve a cercare il limite dell'area scritta.</summary>
    public bool ProbeSector(long block)
    {
        if (_drive.ReadSectors(block, 1, _probe, 0).Success) return true;
        return UseAlternateRead && _drive.ReadSectorsAlternate(block, 1, _probe, 0).Success;
    }

    public override void Dispose()
    {
        if (_ownsDrive) _drive?.Dispose();
        base.Dispose();
    }
}
