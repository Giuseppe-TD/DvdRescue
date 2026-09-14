using System.IO.Compression;
using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>
/// Lettore di immagini PowerISO DAA (solo file NON cifrati).
///
/// Struttura:
///   0x00  16  firma, comincia con "DAA"
///   0x10   4  offset della tabella delle dimensioni dei chunk
///   0x14   4  formato/versione (0x100 = DAA classico)
///   0x18   4  offset del primo chunk compresso
///   0x24   4  dimensione decompressa di ogni chunk
///   0x28   8  dimensione dell'immagine ISO contenuta
///   0x30   8  dimensione del file DAA
/// Ogni chunk è uno stream deflate grezzo; le dimensioni compresse stanno nella tabella,
/// impacchettate a un numero fisso di bit per voce.
///
/// La larghezza in bit non è dichiarata: viene determinata provando i valori possibili e
/// tenendo quello per cui la somma delle dimensioni ricade esattamente sulla fine del file.
/// Se nessuna larghezza torna, o se il formato non è quello atteso, l'immagine viene
/// rifiutata: i DAA cifrati e quelli divisi in più parti non sono supportati.
/// </summary>
public static class DaaImage
{
    private const int HeaderSize = 0x4C;
    private const int MinTableOffset = 0x48;

    /// <summary>Controllo rapido della firma.</summary>
    public static bool LooksLikeDaa(string path)
    {
        try
        {
            var head = DiscImage.ReadHead(path, 16);
            return head.Length >= 16 && head[0] == (byte)'D' && head[1] == (byte)'A' && head[2] == (byte)'A';
        }
        catch { return false; }
    }

    public static bool TryOpen(string path, out DiscImage image) => TryOpen(path, out image, out _);

    /// <summary>
    /// Come <see cref="TryOpen(string, out DiscImage)"/>, ma quando rifiuta spiega perché:
    /// il motivo serve a chi mostra l'errore all'utente, visto che le Note dell'immagine
    /// scartata andrebbero perse.
    /// </summary>
    public static bool TryOpen(string path, out DiscImage image, out string refusal)
    {
        image = null;
        refusal = null;
        DiscImage result = null;
        DaaBlockSource source = null;
        try
        {
            if (!File.Exists(path)) return false;
            long fileLength = new FileInfo(path).Length;
            if (fileLength < HeaderSize + 16)
                return Refuse(null, out refusal, "il file è troppo corto per contenere un'intestazione DAA.");

            var head = DiscImage.ReadHead(path, HeaderSize);
            if (head.Length < HeaderSize) return false;
            if (head[0] != 'D' || head[1] != 'A' || head[2] != 'A') return false;

            long tableOffset = ByteUtils.U32LE(head, 0x10);
            uint format = ByteUtils.U32LE(head, 0x14);
            long dataOffset = ByteUtils.U32LE(head, 0x18);
            long chunkSize = ByteUtils.U32LE(head, 0x24);
            ulong isoSizeRaw = ByteUtils.U64LE(head, 0x28);
            ulong declaredFileSize = ByteUtils.U64LE(head, 0x30);

            result = new DiscImage { FormatName = "PowerISO DAA", Path = path };

            if (format != 0x100 && format != 0x110)
            {
                return Refuse(result, out refusal,
                    $"variante con formato 0x{format:X} non supportata " +
                    "(sono gestiti solo i DAA classici non cifrati).");
            }

            if (isoSizeRaw == 0 || isoSizeRaw > long.MaxValue)
                return Refuse(result, out refusal, "la dimensione dichiarata dell'immagine non è valida.");
            long isoSize = (long)isoSizeRaw;

            if (chunkSize <= 0 || chunkSize > 64 * 1024 * 1024)
                return Refuse(result, out refusal, $"dimensione dei chunk non plausibile ({chunkSize} byte).");
            if (tableOffset < MinTableOffset || tableOffset > fileLength)
                return Refuse(result, out refusal, "la tabella dei chunk è fuori dal file.");
            if (dataOffset < tableOffset || dataOffset > fileLength)
                return Refuse(result, out refusal, "l'offset dei dati è fuori dal file.");

            if (declaredFileSize != 0 && (long)declaredFileSize != fileLength)
            {
                return Refuse(result, out refusal,
                    $"il file dichiara {declaredFileSize} byte ma ne misura {fileLength}: " +
                    "è probabilmente un DAA diviso in più parti (.d00, .d01...), formato non supportato.");
            }

            long chunkCount = (isoSize + chunkSize - 1) / chunkSize;
            if (chunkCount <= 0 || chunkCount > 4_000_000)
                return Refuse(result, out refusal, "il numero di chunk dichiarato non è plausibile.");

            long tableBytes = dataOffset - tableOffset;
            long[] offsets;
            long[] sizes;

            if (tableBytes <= 0)
            {
                // nessuna tabella: chunk memorizzati non compressi, uno dopo l'altro
                if (dataOffset + chunkCount * chunkSize > fileLength)
                    return Refuse(result, out refusal, "i dati non compressi non stanno nel file.");
                sizes = new long[chunkCount];
                for (int i = 0; i < chunkCount; i++) sizes[i] = chunkSize;
                result.Note("Nessuna tabella dei chunk: i dati sono memorizzati non compressi.");
            }
            else
            {
                // ogni voce occupa almeno 8 bit: più chunk che byte di tabella è impossibile
                if (chunkCount > tableBytes)
                    return Refuse(result, out refusal, "la tabella è troppo corta per il numero di chunk dichiarato.");

                var table = new byte[tableBytes];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Position = tableOffset;
                    int got = 0;
                    while (got < table.Length)
                    {
                        int n = fs.Read(table, got, table.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got != table.Length)
                        return Refuse(result, out refusal, "la tabella dei chunk è troncata.");
                }

                sizes = ResolveTable(table, chunkCount, dataOffset, fileLength, out int bits);
                if (sizes == null)
                {
                    return Refuse(result, out refusal,
                        "tabella dei chunk non interpretabile, la somma delle dimensioni non coincide " +
                        "con la lunghezza del file. Il DAA potrebbe essere cifrato o appartenere a una " +
                        "variante non supportata; non viene letto per non restituire dati sbagliati.");
                }
                result.Note($"Tabella dei chunk letta: {chunkCount} chunk da {chunkSize} byte, " +
                            $"{bits} bit per voce.");
            }

            offsets = new long[sizes.Length];
            long position = dataOffset;
            for (int i = 0; i < sizes.Length; i++)
            {
                offsets[i] = position;
                position += sizes[i];
            }
            if (position > fileLength)
                return Refuse(result, out refusal, "i chunk dichiarati sforano la fine del file.");

            bool zeroTail = true;
            for (int i = 0x38; i < 0x48; i++) if (head[i] != 0) { zeroTail = false; break; }
            if (!zeroTail)
                result.Note("Alcuni campi dell'intestazione, di norma azzerati, contengono dati: " +
                            "la struttura è comunque risultata coerente e il file è stato letto.");

            if (isoSize % 2048 != 0)
                result.Note($"La dimensione dichiarata ({isoSize} byte) non è multipla di 2048: " +
                            $"gli ultimi {isoSize % 2048} byte sono stati ignorati.");

            source = new DaaBlockSource(path, offsets, sizes, chunkSize, isoSize, Path.GetFileName(path));
            result.Own(source);

            // verifica reale: il primo chunk deve decomprimersi
            var probe = new byte[2048];
            if (source.ReadBlocks(0, 1, probe, 0) != 1)
            {
                return Refuse(result, out refusal,
                    "il primo chunk non si decomprime: il file è cifrato, danneggiato o di una " +
                    "variante non supportata.");
            }

            long sectors = isoSize / 2048;
            if (sectors <= 0)
                return Refuse(result, out refusal, "l'immagine contenuta non arriva a un settore.");

            var track = new ImageTrack
            {
                Number = 1,
                Session = 1,
                IsAudio = false,
                StartLba = 0,
                Sectors = sectors,
                SectorSize = 2048,
                DataOffset = 0,
                UserDataSize = 2048
            };
            result.Tracks.Add(track);
            result.Attach(track, source, 0, alreadyUserData: true);

            result.Note($"DAA letto: immagine da {sectors} settori ({isoSize} byte), " +
                        $"{sizes.Length} chunk compressi con deflate.");
            image = result;
            return true;
        }
        catch (Exception ex)
        {
            result?.Dispose();
            source?.Dispose();
            refusal = $"errore durante la lettura ({ex.GetType().Name}).";
            return false;
        }
    }

    /// <summary>Scarta l'immagine in costruzione e annota il motivo del rifiuto.</summary>
    private static bool Refuse(DiscImage image, out string refusal, string reason)
    {
        refusal = reason;
        image?.Dispose();
        return false;
    }

    /// <summary>
    /// La tabella è una sequenza di interi a larghezza fissa non dichiarata: proviamo le
    /// larghezze plausibili e teniamo quella per cui i chunk finiscono esattamente a fine file.
    /// </summary>
    private static long[] ResolveTable(byte[] table, long chunkCount, long dataOffset, long fileLength, out int bits)
    {
        bits = 0;
        long expected = fileLength - dataOffset;
        long availableBits = table.LongLength * 8;

        for (int width = 32; width >= 8; width--)
        {
            if (chunkCount * width > availableBits) continue;

            var sizes = new long[chunkCount];
            long total = 0;
            bool ok = true;
            var reader = new BitReader(table);

            for (long i = 0; i < chunkCount; i++)
            {
                long value = reader.Read(width);
                if (value < 0) { ok = false; break; }
                if (value == 0) { ok = false; break; }          // un chunk vuoto non esiste
                sizes[i] = value;
                total += value;
                if (total > expected) { ok = false; break; }
            }

            if (ok && total == expected)
            {
                bits = width;
                return sizes;
            }
        }
        return null;
    }

    /// <summary>Lettore di interi a larghezza arbitraria, bit più significativo per primo.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private long _bitPosition;

        public BitReader(byte[] data) { _data = data; }

        public long Read(int bits)
        {
            if (bits <= 0 || bits > 32) return -1;
            if (_bitPosition + bits > _data.LongLength * 8) return -1;

            long value = 0;
            for (int i = 0; i < bits; i++)
            {
                long index = _bitPosition >> 3;
                int shift = 7 - (int)(_bitPosition & 7);
                value = (value << 1) | (long)((_data[index] >> shift) & 1);
                _bitPosition++;
            }
            return value;
        }
    }
}

/// <summary>
/// Sorgente a blocchi da 2048 byte sopra un DAA: i chunk vengono decompressi su richiesta
/// e tenuti in una piccola cache, perché le letture di un filesystem sono quasi sempre vicine.
/// </summary>
public sealed class DaaBlockSource : BlockSourceBase
{
    private const int CacheSize = 4;

    private readonly FileStream _stream;
    private readonly long[] _offsets;
    private readonly long[] _sizes;
    private readonly long _chunkSize;
    private readonly long _isoSize;
    private readonly object _lock = new();
    private readonly int[] _cacheIndex = new int[CacheSize];
    private readonly byte[][] _cacheData = new byte[CacheSize][];
    private readonly int[] _cacheLength = new int[CacheSize];
    private int _cacheNext;
    private long _bad;

    public override string Name { get; }
    public override int BlockSize => 2048;
    public override long TotalBlocks => _isoSize / 2048;
    public override long Length => _isoSize;
    public override long BadBlockCount => _bad;

    public DaaBlockSource(string path, long[] offsets, long[] sizes, long chunkSize, long isoSize, string name)
    {
        _offsets = offsets;
        _sizes = sizes;
        _chunkSize = chunkSize;
        _isoSize = isoSize;
        Name = name;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        for (int i = 0; i < CacheSize; i++) _cacheIndex[i] = -1;
    }

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        if (count <= 0) return 0;
        Array.Clear(destination, destinationOffset, count * 2048);
        if (block < 0) return 0;

        int ok = 0;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                long lba = block + i;
                if (lba >= TotalBlocks) break;

                long byteOffset = lba * 2048L;
                int copied = 0;
                bool failed = false;

                // un blocco può cadere a cavallo di due chunk
                while (copied < 2048)
                {
                    long absolute = byteOffset + copied;
                    long chunk = absolute / _chunkSize;
                    int inChunk = (int)(absolute % _chunkSize);

                    var data = GetChunk(chunk, out int length);
                    if (data == null || inChunk >= length) { failed = true; break; }

                    int take = Math.Min(2048 - copied, length - inChunk);
                    Array.Copy(data, inChunk, destination, destinationOffset + i * 2048 + copied, take);
                    copied += take;
                }

                if (failed || copied < 2048) _bad++;
                else ok++;
            }
        }
        return ok;
    }

    /// <summary>Chunk decompresso, da cache o dal file. null se non è leggibile.</summary>
    private byte[] GetChunk(long index, out int length)
    {
        length = 0;
        if (index < 0 || index >= _offsets.LongLength) return null;

        for (int i = 0; i < CacheSize; i++)
            if (_cacheIndex[i] == index) { length = _cacheLength[i]; return _cacheData[i]; }

        long expected = Math.Min(_chunkSize, _isoSize - index * _chunkSize);
        if (expected <= 0) return null;

        try
        {
            var compressed = new byte[_sizes[index]];
            _stream.Position = _offsets[index];
            int got = 0;
            while (got < compressed.Length)
            {
                int n = _stream.Read(compressed, got, compressed.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != compressed.Length) return null;

            var output = new byte[expected];
            int produced = Inflate(compressed, output);
            if (produced <= 0)
            {
                // alcuni chunk sono memorizzati così come sono quando comprimere non conviene
                if (compressed.Length == expected)
                {
                    Array.Copy(compressed, output, (int)expected);
                    produced = (int)expected;
                }
                else return null;
            }

            int slot = _cacheNext;
            _cacheNext = (_cacheNext + 1) % CacheSize;
            _cacheIndex[slot] = (int)index;
            _cacheData[slot] = output;
            _cacheLength[slot] = produced;
            length = produced;
            return output;
        }
        catch { return null; }
    }

    /// <summary>Deflate grezzo; accetta anche gli stream con intestazione zlib.</summary>
    private static int Inflate(byte[] compressed, byte[] output)
    {
        int produced = TryInflate(compressed, 0, output);
        if (produced > 0) return produced;

        if (compressed.Length > 2 && (compressed[0] & 0x0F) == 8 &&
            ((compressed[0] << 8) | compressed[1]) % 31 == 0)
            return TryInflate(compressed, 2, output);

        return 0;
    }

    private static int TryInflate(byte[] compressed, int skip, byte[] output)
    {
        try
        {
            using var input = new MemoryStream(compressed, skip, compressed.Length - skip, false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            int produced = 0;
            while (produced < output.Length)
            {
                int n = deflate.Read(output, produced, output.Length - produced);
                if (n <= 0) break;
                produced += n;
            }
            return produced;
        }
        catch { return 0; }
    }

    public override void Dispose()
    {
        _stream?.Dispose();
        base.Dispose();
    }
}
