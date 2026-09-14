namespace DVDRescue.Core;

/// <summary>
/// Sorgente di blocchi: un lettore ottico, un disco fisico, una partizione, un file immagine.
/// La dimensione del blocco cambia col supporto (512 sui dischi, 2048 sugli ottici,
/// 4096 sui dischi Advanced Format), quindi i lettori di filesystem lavorano su
/// <see cref="ReadBytes"/> e non presumono nulla.
/// </summary>
public interface IBlockSource : IDisposable
{
    string Name { get; }

    int BlockSize { get; }

    long TotalBlocks { get; }

    /// <summary>Lunghezza in byte, se nota (altrimenti TotalBlocks * BlockSize).</summary>
    long Length { get; }

    /// <summary>
    /// Legge blocchi consecutivi. I blocchi illeggibili vengono azzerati.
    /// Ritorna il numero di blocchi recuperati davvero.
    /// </summary>
    int ReadBlocks(long block, int count, byte[] destination, int destinationOffset);

    /// <summary>Legge un singolo blocco; false se illeggibile (destinazione azzerata).</summary>
    bool TryReadBlock(long block, byte[] destination, int destinationOffset);

    /// <summary>
    /// Legge byte a un offset qualsiasi, anche non allineato ai blocchi.
    /// Ritorna i byte effettivamente letti (le zone illeggibili restano a zero).
    /// </summary>
    int ReadBytes(long byteOffset, int count, byte[] destination, int destinationOffset);

    /// <summary>Blocchi che non è stato possibile leggere durante la sessione.</summary>
    long BadBlockCount { get; }
}

/// <summary>Implementazione comune di <see cref="IBlockSource.ReadBytes"/> sopra i blocchi.</summary>
public abstract class BlockSourceBase : IBlockSource
{
    private byte[] _scratch;

    public abstract string Name { get; }
    public abstract int BlockSize { get; }
    public abstract long TotalBlocks { get; }
    public virtual long Length => TotalBlocks * BlockSize;
    public virtual long BadBlockCount => 0;

    public abstract int ReadBlocks(long block, int count, byte[] destination, int destinationOffset);

    public virtual bool TryReadBlock(long block, byte[] destination, int destinationOffset)
        => ReadBlocks(block, 1, destination, destinationOffset) == 1;

    public virtual int ReadBytes(long byteOffset, int count, byte[] destination, int destinationOffset)
    {
        if (count <= 0) return 0;
        if (byteOffset < 0) return 0;

        int bs = BlockSize;
        long firstBlock = byteOffset / bs;
        int offsetInBlock = (int)(byteOffset % bs);
        int blocksNeeded = (int)((offsetInBlock + count + bs - 1) / bs);

        int scratchSize = blocksNeeded * bs;
        if (_scratch == null || _scratch.Length < scratchSize)
            _scratch = new byte[Math.Max(scratchSize, 64 * 1024)];

        Array.Clear(_scratch, 0, scratchSize);

        long available = TotalBlocks - firstBlock;
        if (available <= 0) return 0;
        if (blocksNeeded > available) blocksNeeded = (int)available;

        ReadBlocks(firstBlock, blocksNeeded, _scratch, 0);

        int usable = Math.Min(count, blocksNeeded * bs - offsetInBlock);
        if (usable <= 0) return 0;

        Array.Copy(_scratch, offsetInBlock, destination, destinationOffset, usable);
        return usable;
    }

    /// <summary>Helper: legge esattamente <paramref name="count"/> byte in un array nuovo.</summary>
    public byte[] ReadBytes(long byteOffset, int count)
    {
        var buffer = new byte[count];
        ReadBytes(byteOffset, count, buffer, 0);
        return buffer;
    }

    public virtual void Dispose() { GC.SuppressFinalize(this); }
}

/// <summary>Sorgente su file immagine grezzo (.bin, .iso, .img, dd).</summary>
public sealed class FileBlockSource : BlockSourceBase
{
    private readonly FileStream _fs;
    private readonly int _blockSize;
    private long _short;

    public override string Name { get; }
    public override int BlockSize => _blockSize;
    public override long TotalBlocks => _fs.Length / _blockSize;
    public override long Length => _fs.Length;

    /// <summary>Blocchi non letti per intero: succede con un'immagine troncata.</summary>
    public override long BadBlockCount => _short;

    public FileBlockSource(string path, int blockSize = 2048)
    {
        _blockSize = blockSize;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        Name = Path.GetFileName(path);
    }

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        long offset = block * _blockSize;
        int want = count * _blockSize;
        Array.Clear(destination, destinationOffset, want);

        if (offset >= _fs.Length) return 0;

        lock (_fs)
        {
            _fs.Position = offset;
            int got = 0;
            while (got < want)
            {
                int n = _fs.Read(destination, destinationOffset + got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < want) _short += (want - got) / _blockSize;
            return got / _blockSize;
        }
    }

    public override int ReadBytes(long byteOffset, int count, byte[] destination, int destinationOffset)
    {
        if (count <= 0 || byteOffset < 0 || byteOffset >= _fs.Length) return 0;

        lock (_fs)
        {
            _fs.Position = byteOffset;
            int got = 0;
            while (got < count)
            {
                int n = _fs.Read(destination, destinationOffset + got, count - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < count) Array.Clear(destination, destinationOffset + got, count - got);
            return got;
        }
    }

    public override void Dispose()
    {
        _fs?.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Finestra su una porzione di un'altra sorgente: una partizione dentro un disco,
/// una traccia dentro un'immagine, un file dentro un filesystem.
/// </summary>
public sealed class SubRangeSource : BlockSourceBase
{
    private readonly IBlockSource _inner;
    private readonly long _startByte;
    private readonly long _lengthBytes;
    private readonly int _blockSize;

    public override string Name { get; }
    public override int BlockSize => _blockSize;
    public override long TotalBlocks => _lengthBytes / _blockSize;
    public override long Length => _lengthBytes;
    public override long BadBlockCount => _inner.BadBlockCount;

    public SubRangeSource(IBlockSource inner, long startByte, long lengthBytes, int blockSize, string name = null)
    {
        _inner = inner;
        _startByte = startByte;
        _lengthBytes = lengthBytes;
        _blockSize = blockSize > 0 ? blockSize : inner.BlockSize;
        Name = name ?? inner.Name;
    }

    public static SubRangeSource FromBlocks(IBlockSource inner, long startBlock, long blockCount,
                                            int blockSize = 0, string name = null)
    {
        int bs = blockSize > 0 ? blockSize : inner.BlockSize;
        return new SubRangeSource(inner, startBlock * bs, blockCount * bs, bs, name);
    }

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        long offset = block * _blockSize;
        int want = count * _blockSize;
        if (offset + want > _lengthBytes) want = (int)Math.Max(0, _lengthBytes - offset);
        if (want <= 0) { Array.Clear(destination, destinationOffset, count * _blockSize); return 0; }

        int got = _inner.ReadBytes(_startByte + offset, want, destination, destinationOffset);
        if (got < count * _blockSize)
            Array.Clear(destination, destinationOffset + got, count * _blockSize - got);
        return got / _blockSize;
    }

    public override int ReadBytes(long byteOffset, int count, byte[] destination, int destinationOffset)
    {
        if (byteOffset >= _lengthBytes) return 0;
        if (byteOffset + count > _lengthBytes) count = (int)(_lengthBytes - byteOffset);
        if (count <= 0) return 0;
        return _inner.ReadBytes(_startByte + byteOffset, count, destination, destinationOffset);
    }

    public override void Dispose() { /* la sorgente interna appartiene a chi l'ha creata */ }
}

/// <summary>
/// Vista che rimappa i blocchi tramite una tabella: serve per l'UDF con Virtual Allocation
/// Table (packet writing) e con la sparing table (DVD+RW/DVD-RAM rimappano i settori difettosi).
/// </summary>
public sealed class RemappedSource : BlockSourceBase
{
    private readonly IBlockSource _inner;
    private readonly Func<long, long> _map;
    private readonly long _totalBlocks;

    public override string Name => _inner.Name;
    public override int BlockSize => _inner.BlockSize;
    public override long TotalBlocks => _totalBlocks;
    public override long BadBlockCount => _inner.BadBlockCount;

    public RemappedSource(IBlockSource inner, Func<long, long> map, long totalBlocks)
    {
        _inner = inner;
        _map = map;
        _totalBlocks = totalBlocks;
    }

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        int ok = 0;
        for (int i = 0; i < count; i++)
        {
            long physical = _map(block + i);
            if (physical < 0)
            {
                Array.Clear(destination, destinationOffset + i * BlockSize, BlockSize);
                continue;
            }
            if (_inner.TryReadBlock(physical, destination, destinationOffset + i * BlockSize)) ok++;
        }
        return ok;
    }

    public override void Dispose() { }
}
