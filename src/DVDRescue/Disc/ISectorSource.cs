using DVDRescue.Native;

namespace DVDRescue.Disc;

/// <summary>
/// Sorgente di settori da 2048 byte: può essere il lettore ottico o un'immagine grezza su disco.
/// </summary>
public interface ISectorSource : IDisposable
{
    string Name { get; }
    long TotalSectors { get; }

    /// <summary>
    /// Legge <paramref name="count"/> settori. I settori illeggibili vengono azzerati.
    /// Ritorna il numero di settori effettivamente recuperati.
    /// </summary>
    int Read(long lba, int count, byte[] destination, int destinationOffset);

    bool TryReadSector(long lba, byte[] destination, int destinationOffset);
}

/// <summary>Sorgente basata su un file immagine grezzo (.bin/.iso) creato da DVDRescue o da altri tool.</summary>
public sealed class ImageSectorSource : ISectorSource
{
    private readonly FileStream _fs;

    public string Name { get; }
    public long TotalSectors { get; }

    public ImageSectorSource(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        TotalSectors = _fs.Length / OpticalDrive.SectorSize;
        Name = Path.GetFileName(path);
    }

    public int Read(long lba, int count, byte[] destination, int destinationOffset)
    {
        long offset = lba * OpticalDrive.SectorSize;
        if (offset >= _fs.Length) { Array.Clear(destination, destinationOffset, count * OpticalDrive.SectorSize); return 0; }

        _fs.Position = offset;
        int want = count * OpticalDrive.SectorSize;
        int got = 0;
        while (got < want)
        {
            int n = _fs.Read(destination, destinationOffset + got, want - got);
            if (n <= 0) break;
            got += n;
        }
        if (got < want) Array.Clear(destination, destinationOffset + got, want - got);
        return got / OpticalDrive.SectorSize;
    }

    public bool TryReadSector(long lba, byte[] destination, int destinationOffset)
        => Read(lba, 1, destination, destinationOffset) == 1;

    public void Dispose() => _fs?.Dispose();
}

/// <summary>
/// Sorgente basata sul lettore ottico, con retry progressivo: blocco grande → blocchi piccoli →
/// settore singolo → azzeramento del settore irrecuperabile.
/// </summary>
public sealed class DriveSectorSource : ISectorSource
{
    private readonly OpticalDrive _drive;
    private readonly bool _ownsDrive;
    private readonly byte[] _one = new byte[OpticalDrive.SectorSize];

    public string Name { get; }
    public long TotalSectors { get; set; }

    /// <summary>Numero di tentativi per settore prima di rinunciare.</summary>
    public int RetriesPerSector { get; set; } = 3;

    /// <summary>Se true, dopo il primo fallimento prova anche con READ CD (0xBE).</summary>
    public bool UseAlternateRead { get; set; } = true;

    public long BadSectorCount { get; private set; }
    public List<long> BadSectors { get; } = new();

    public DriveSectorSource(OpticalDrive drive, long totalSectors, bool ownsDrive = true)
    {
        _drive = drive;
        _ownsDrive = ownsDrive;
        TotalSectors = totalSectors;
        Name = $"Unità {drive.DriveLetter}:";
    }

    public int Read(long lba, int count, byte[] destination, int destinationOffset)
    {
        var res = _drive.ReadSectors(lba, count, destination, destinationOffset);
        if (res.Success) return count;

        if (count == 1) return ReadSingleWithRetry(lba, destination, destinationOffset) ? 1 : 0;

        // il blocco è fallito: dividi a metà e riprova
        int half = count / 2;
        int ok = Read(lba, half, destination, destinationOffset);
        ok += Read(lba + half, count - half, destination, destinationOffset + half * OpticalDrive.SectorSize);
        return ok;
    }

    private bool ReadSingleWithRetry(long lba, byte[] destination, int destinationOffset)
    {
        for (int attempt = 0; attempt < RetriesPerSector; attempt++)
        {
            var r = _drive.ReadSectors(lba, 1, destination, destinationOffset);
            if (r.Success) return true;

            if (UseAlternateRead)
            {
                var r2 = _drive.ReadSectorsAlternate(lba, 1, destination, destinationOffset);
                if (r2.Success) return true;
            }

            Thread.Sleep(30);
        }

        Array.Clear(destination, destinationOffset, OpticalDrive.SectorSize);
        BadSectorCount++;
        if (BadSectors.Count < 100000) BadSectors.Add(lba);
        return false;
    }

    public bool TryReadSector(long lba, byte[] destination, int destinationOffset)
    {
        var r = _drive.ReadSectors(lba, 1, destination, destinationOffset);
        if (r.Success) return true;
        if (UseAlternateRead && _drive.ReadSectorsAlternate(lba, 1, destination, destinationOffset).Success) return true;
        Array.Clear(destination, destinationOffset, OpticalDrive.SectorSize);
        return false;
    }

    /// <summary>Lettura "secca" senza retry né azzeramento: serve alla ricerca binaria del limite scritto.</summary>
    public bool ProbeSector(long lba)
    {
        if (_drive.ReadSectors(lba, 1, _one, 0).Success) return true;
        if (UseAlternateRead && _drive.ReadSectorsAlternate(lba, 1, _one, 0).Success) return true;
        return false;
    }

    public void Dispose()
    {
        if (_ownsDrive) _drive?.Dispose();
    }
}
