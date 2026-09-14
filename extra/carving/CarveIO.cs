using DVDRescue.Core;

namespace DVDRescue.Carving;

/// <summary>
/// Letture difensive e ricerche in avanti sopra una <see cref="IBlockSource"/>.
/// Nessuna funzione qui dentro può lanciare: i dati sono per definizione spazzatura.
/// </summary>
internal static class CarveIO
{
    /// <summary>Legge al massimo <paramref name="count"/> byte; la coda non letta resta azzerata.</summary>
    public static int Read(IBlockSource src, long offset, byte[] buffer, int count)
    {
        if (src == null || buffer == null || count <= 0 || offset < 0) return 0;
        if (count > buffer.Length) count = buffer.Length;

        try
        {
            long len = src.Length;
            if (len > 0)
            {
                if (offset >= len) { Array.Clear(buffer, 0, count); return 0; }
                if (offset + count > len) count = (int)(len - offset);
                if (count <= 0) return 0;
            }

            int got = src.ReadBytes(offset, count, buffer, 0);
            if (got < 0) got = 0;
            if (got < count) Array.Clear(buffer, got, count - got);
            return got;
        }
        catch
        {
            Array.Clear(buffer, 0, Math.Min(count, buffer.Length));
            return 0;
        }
    }

    public static byte[] Read(IBlockSource src, long offset, int count)
    {
        var b = new byte[Math.Max(count, 1)];
        Read(src, offset, b, count);
        return b;
    }

    /// <summary>Un solo byte, -1 se non leggibile.</summary>
    public static int ReadByte(IBlockSource src, long offset)
    {
        var b = new byte[1];
        return Read(src, offset, b, 1) == 1 ? b[0] : -1;
    }

    public static long SourceLength(IBlockSource src)
    {
        try
        {
            long l = src.Length;
            if (l <= 0) l = src.TotalBlocks * (long)src.BlockSize;
            return l < 0 ? 0 : l;
        }
        catch { return 0; }
    }

    // --- letture guardate da buffer (mai fuori dai bordi) ---

    public static uint U32LE(byte[] b, int o) =>
        o < 0 || o + 4 > b.Length ? 0u : ByteUtils.U32LE(b, o);

    public static uint U32BE(byte[] b, int o) =>
        o < 0 || o + 4 > b.Length ? 0u : ByteUtils.U32BE(b, o);

    public static ushort U16LE(byte[] b, int o) =>
        o < 0 || o + 2 > b.Length ? (ushort)0 : ByteUtils.U16LE(b, o);

    public static ushort U16BE(byte[] b, int o) =>
        o < 0 || o + 2 > b.Length ? (ushort)0 : ByteUtils.U16BE(b, o);

    public static ulong U64LE(byte[] b, int o) =>
        o < 0 || o + 8 > b.Length ? 0ul : ByteUtils.U64LE(b, o);

    public static ulong U64BE(byte[] b, int o) =>
        o < 0 || o + 8 > b.Length ? 0ul : ByteUtils.U64BE(b, o);

    /// <summary>Confronto di un pattern dentro un buffer, con controllo dei bordi.</summary>
    public static bool Match(byte[] b, int o, params byte[] pattern)
    {
        if (o < 0 || o + pattern.Length > b.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (b[o + i] != pattern[i]) return false;
        return true;
    }

    public static bool MatchAscii(byte[] b, int o, string s)
    {
        if (o < 0 || o + s.Length > b.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (b[o + i] != (byte)s[i]) return false;
        return true;
    }

    /// <summary>
    /// Cerca un pattern in avanti a partire da <paramref name="from"/>, entro
    /// <paramref name="maxBytes"/>. Ritorna l'offset assoluto o -1.
    /// Legge a chunk: non tiene mai in memoria più di 64 KB.
    /// </summary>
    public static long Find(IBlockSource src, long from, long maxBytes, byte[] pattern)
    {
        if (pattern == null || pattern.Length == 0 || maxBytes <= 0) return -1;

        long limit = SourceLength(src);
        if (limit > 0 && from + maxBytes > limit) maxBytes = limit - from;
        if (maxBytes < pattern.Length) return -1;

        const int Chunk = 64 * 1024;
        int overlap = pattern.Length - 1;
        var buf = new byte[Chunk + overlap];

        long pos = from;
        long stop = from + maxBytes;

        while (pos < stop)
        {
            int want = (int)Math.Min(Chunk + overlap, stop - pos);
            if (want < pattern.Length) break;

            int got = Read(src, pos, buf, want);
            if (got < pattern.Length) break;

            int last = got - pattern.Length;
            byte p0 = pattern[0];
            for (int i = 0; i <= last; i++)
            {
                if (buf[i] != p0) continue;
                int k = 1;
                while (k < pattern.Length && buf[i + k] == pattern[k]) k++;
                if (k == pattern.Length) return pos + i;
            }

            if (got < want) break;
            pos += Chunk;
        }
        return -1;
    }

    /// <summary>Ultima occorrenza di un pattern nell'intervallo indicato (per gli EOCD/EOF multipli).</summary>
    public static long FindLast(IBlockSource src, long from, long maxBytes, byte[] pattern)
    {
        long best = -1;
        long p = from;
        long stop = from + maxBytes;
        while (p < stop)
        {
            long hit = Find(src, p, stop - p, pattern);
            if (hit < 0) break;
            best = hit;
            p = hit + 1;
        }
        return best;
    }

    /// <summary>Assorbe un eventuale fine riga (CRLF, LF, CR) dopo un footer testuale.</summary>
    public static int TrailingEol(IBlockSource src, long offset)
    {
        var b = new byte[2];
        int got = Read(src, offset, b, 2);
        if (got >= 2 && b[0] == 0x0D && b[1] == 0x0A) return 2;
        if (got >= 1 && (b[0] == 0x0A || b[0] == 0x0D)) return 1;
        return 0;
    }

    private static readonly uint[] Crc32Table = BuildCrc32();

    private static uint[] BuildCrc32()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(byte[] data, int offset, int count, uint seed = 0)
    {
        uint c = seed ^ 0xFFFFFFFFu;
        for (int i = 0; i < count; i++) c = Crc32Table[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    public static uint Crc32Continue(uint running, byte[] data, int offset, int count)
    {
        uint c = running ^ 0xFFFFFFFFu;
        for (int i = 0; i < count; i++) c = Crc32Table[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// Finestra di cache sopra un'altra sorgente. Le funzioni di misura fanno molte letture
/// piccole e ravvicinate: senza cache ogni campo costerebbe una seek sul supporto.
/// Il tetto di memoria è fisso (una finestra sola).
/// </summary>
internal sealed class CachingSource : IBlockSource
{
    private readonly IBlockSource _inner;
    private readonly byte[] _win;
    private long _winStart = -1;
    private int _winLen;

    public CachingSource(IBlockSource inner, int windowSize = 1 << 20)
    {
        _inner = inner;
        if (windowSize < 64 * 1024) windowSize = 64 * 1024;
        _win = new byte[windowSize];
    }

    public string Name => _inner.Name;
    public int BlockSize => _inner.BlockSize;
    public long TotalBlocks => _inner.TotalBlocks;
    public long Length => _inner.Length;
    public long BadBlockCount => _inner.BadBlockCount;

    public int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
        => _inner.ReadBlocks(block, count, destination, destinationOffset);

    public bool TryReadBlock(long block, byte[] destination, int destinationOffset)
        => _inner.TryReadBlock(block, destination, destinationOffset);

    public int ReadBytes(long byteOffset, int count, byte[] destination, int destinationOffset)
    {
        if (count <= 0 || byteOffset < 0) return 0;

        // richieste grandi: inutile passare dalla cache
        if (count > _win.Length / 2)
            return _inner.ReadBytes(byteOffset, count, destination, destinationOffset);

        if (_winStart < 0 || byteOffset < _winStart || byteOffset + count > _winStart + _winLen)
        {
            _winStart = byteOffset;
            _winLen = _inner.ReadBytes(byteOffset, _win.Length, _win, 0);
            if (_winLen < 0) _winLen = 0;
            if (_winLen < _win.Length) Array.Clear(_win, _winLen, _win.Length - _winLen);
            // anche se la coda è corta teniamo la finestra: i byte mancanti sono zeri
            if (_winLen == 0) { _winStart = -1; return 0; }
        }

        int inWin = (int)(byteOffset - _winStart);
        int avail = _winLen - inWin;
        if (avail <= 0) return 0;
        int n = Math.Min(count, avail);
        Array.Copy(_win, inWin, destination, destinationOffset, n);
        return n;
    }

    public void Dispose() { /* la sorgente interna appartiene a chi l'ha creata */ }
}

/// <summary>
/// Adattatore Stream in sola lettura sopra una porzione di <see cref="IBlockSource"/>,
/// con contatore dei byte consegnati. Serve a far girare DeflateStream sui dati compressi
/// sapendo esattamente quanto input ha consumato.
/// </summary>
internal sealed class CountingBlockStream : Stream
{
    private readonly IBlockSource _src;
    private readonly long _start;
    private readonly long _max;
    private readonly int _grain;
    private long _pos;

    public long Consumed => _pos;

    public CountingBlockStream(IBlockSource src, long start, long max, int grain = 1)
    {
        _src = src;
        _start = start;
        _max = max;
        _grain = Math.Max(1, grain);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _max;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        long left = _max - _pos;
        if (left <= 0) return 0;

        // consegna a piccoli morsi: così l'inflater non si porta avanti byte che non gli servono
        int want = (int)Math.Min(Math.Min(count, _grain), left);
        var tmp = new byte[want];
        int got = CarveIO.Read(_src, _start + _pos, tmp, want);
        if (got <= 0) return 0;
        Array.Copy(tmp, 0, buffer, offset, got);
        _pos += got;
        return got;
    }
}
