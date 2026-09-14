namespace DVDRescue.Core;

/// <summary>
/// Cache con lettura anticipata davanti a una sorgente lenta.
///
/// Su un lettore ottico non conta quanti byte si chiedono, conta quante volte li si chiede:
/// ogni comando costa una ricerca della testina, fra i 10 e i 30 millisecondi. Leggere le
/// strutture di un disco — descrittori UDF, tabella dei file, IFO — significa centinaia di
/// letture da un settore sparse qua e là: un paio di secondi buttati in attese.
///
/// Qui ogni lettura piccola tira su un blocco intero di settori contigui e lo tiene da parte:
/// le strutture stanno vicine fra loro, quindi le richieste successive arrivano già servite.
/// Le letture grandi (la scansione, l'estrazione) passano dritte senza sporcare la cache.
/// </summary>
public sealed class CachingBlockSource : BlockSourceBase
{
    private readonly IBlockSource _inner;
    private readonly Dictionary<long, byte[]> _chunks = new();
    private readonly LinkedList<long> _order = new();
    private readonly object _lock = new();

    /// <summary>Settori letti in anticipo attorno a ogni richiesta piccola.</summary>
    public int ReadAheadBlocks { get; set; } = 64;      // 128 KB

    /// <summary>Numero massimo di blocchi tenuti in memoria.</summary>
    public int MaxChunks { get; set; } = 96;           // circa 12 MB

    /// <summary>Oltre questa dimensione la richiesta non passa dalla cache.</summary>
    public int BypassThreshold { get; set; } = 32;

    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }

    /// <summary>Letture effettivamente inoltrate al supporto.</summary>
    public long DeviceReads { get; private set; }

    public override string Name => _inner.Name;
    public override int BlockSize => _inner.BlockSize;
    public override long TotalBlocks => _inner.TotalBlocks;
    public override long Length => _inner.Length;
    public override long BadBlockCount => _inner.BadBlockCount;

    public CachingBlockSource(IBlockSource inner) => _inner = inner;

    public IBlockSource Inner => _inner;

    public void Clear()
    {
        lock (_lock)
        {
            _chunks.Clear();
            _order.Clear();
        }
    }

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        if (count > BypassThreshold || ReadAheadBlocks <= 1)
        {
            DeviceReads++;
            return _inner.ReadBlocks(block, count, destination, destinationOffset);
        }

        int written = 0;

        while (written < count)
        {
            long current = block + written;
            long chunkIndex = current / ReadAheadBlocks;
            byte[] chunk = GetChunk(chunkIndex);

            int offsetInChunk = (int)(current - chunkIndex * ReadAheadBlocks);
            int available = ReadAheadBlocks - offsetInChunk;
            int take = Math.Min(available, count - written);

            if (chunk == null)
            {
                Array.Clear(destination, destinationOffset + written * BlockSize, take * BlockSize);
            }
            else
            {
                Array.Copy(chunk, offsetInChunk * BlockSize,
                           destination, destinationOffset + written * BlockSize,
                           take * BlockSize);
            }

            written += take;
        }

        return count;
    }

    private byte[] GetChunk(long chunkIndex)
    {
        lock (_lock)
        {
            if (_chunks.TryGetValue(chunkIndex, out byte[] cached))
            {
                CacheHits++;
                _order.Remove(chunkIndex);
                _order.AddLast(chunkIndex);
                return cached;
            }
        }

        CacheMisses++;
        DeviceReads++;

        long first = chunkIndex * ReadAheadBlocks;
        long total = TotalBlocks;
        int count = ReadAheadBlocks;

        if (total > 0 && first + count > total)
            count = (int)Math.Max(0, total - first);

        if (count <= 0) return null;

        var buffer = new byte[ReadAheadBlocks * BlockSize];
        _inner.ReadBlocks(first, count, buffer, 0);

        lock (_lock)
        {
            _chunks[chunkIndex] = buffer;
            _order.AddLast(chunkIndex);

            while (_order.Count > MaxChunks)
            {
                long oldest = _order.First.Value;
                _order.RemoveFirst();
                _chunks.Remove(oldest);
            }
        }

        return buffer;
    }

    public override void Dispose()
    {
        Clear();
        base.Dispose();
    }
}
