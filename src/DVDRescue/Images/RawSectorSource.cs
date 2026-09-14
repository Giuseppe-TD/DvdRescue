using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>
/// Come è fatto un settore grezzo: dove comincia il campo dati utente e quanto è lungo.
/// </summary>
public struct SectorLayout
{
    /// <summary>Offset del campo dati utente dentro il settore grezzo.</summary>
    public int DataOffset;

    /// <summary>Lunghezza del campo dati utente (2048, 2324, 2336, 2352...).</summary>
    public int DataSize;

    /// <summary>Modalità dichiarata nell'header: 0, 1, 2; -1 se il settore non ha header.</summary>
    public int Mode;

    /// <summary>True per i settori MODE2 FORM2 (2324 byte di dati, niente ECC).</summary>
    public bool Form2;

    /// <summary>True se i 12 byte di sync sono presenti.</summary>
    public bool HasSync;
}

/// <summary>
/// Vista su una sorgente a settori grezzi (2352, 2336, 2448...) che espone soltanto
/// il campo dati utente, di norma blocchi da 2048 byte.
///
/// Il tipo di settore viene riconosciuto dal pattern di sync
/// 00 FF FF FF FF FF FF FF FF FF FF 00 seguito dall'header (MIN, SEC, FRAME, MODE):
///   MODE1/2352        dati a offset 16, 2048 byte
///   MODE2 FORM1/2352  dati a offset 24, 2048 byte (subheader di 8 byte a offset 16)
///   MODE2 FORM2/2352  dati a offset 24, 2324 byte
///   MODE2 "formless"  dati a offset 16, 2336 byte (le due copie del subheader non coincidono)
///   MODE2/2336        dati a offset 8 (nessun sync, il settore comincia dal subheader)
/// Senza sync i settori vengono trattati come dati puri (offset 0).
/// </summary>
public sealed class RawSectorSource : BlockSourceBase
{
    /// <summary>Pattern di sincronizzazione in testa a ogni settore grezzo di un CD.</summary>
    public static readonly byte[] SyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    };

    private readonly IBlockSource _inner;
    private readonly long _startByte;
    private readonly long _sectors;
    private readonly int _rawSize;
    private readonly int _fixedOffset;   // -1 = riconoscimento automatico settore per settore
    private readonly int _userSize;
    private readonly bool _ownsInner;
    private readonly byte[] _scratchSector;
    private readonly object _lock = new();
    private long _bad;

    public override string Name { get; }
    public override int BlockSize => _userSize;
    public override long TotalBlocks => _sectors;
    public override long Length => _sectors * (long)_userSize;
    public override long BadBlockCount => _bad;

    /// <summary>Dimensione del settore grezzo sottostante.</summary>
    public int RawSectorSize => _rawSize;

    /// <summary>
    /// </summary>
    /// <param name="inner">sorgente dei settori grezzi (di norma il file immagine)</param>
    /// <param name="startByte">offset in byte del primo settore della traccia</param>
    /// <param name="sectors">numero di settori</param>
    /// <param name="rawSectorSize">dimensione del settore grezzo (2048, 2336, 2352, 2448...)</param>
    /// <param name="dataOffset">offset fisso dei dati utente, oppure -1 per riconoscerlo dal sync</param>
    /// <param name="userDataSize">byte esposti per settore (di norma 2048)</param>
    public RawSectorSource(IBlockSource inner, long startByte, long sectors, int rawSectorSize,
                           int dataOffset = -1, int userDataSize = 2048,
                           string name = null, bool ownsInner = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _startByte = Math.Max(0, startByte);
        _sectors = Math.Max(0, sectors);
        _rawSize = rawSectorSize > 0 ? rawSectorSize : 2048;
        _fixedOffset = dataOffset;
        _userSize = userDataSize > 0 ? userDataSize : 2048;
        _ownsInner = ownsInner;
        _scratchSector = new byte[_rawSize];
        Name = name ?? inner.Name;

        // un campo dati più grande del settore non ha senso: meglio accorciarlo che leggere fuori
        if (_fixedOffset >= 0 && _fixedOffset + _userSize > _rawSize)
            _userSize = Math.Max(1, _rawSize - _fixedOffset);
    }

    public override int ReadBlocks(long block, int count, byte[] destination, int destinationOffset)
    {
        if (count <= 0) return 0;
        Array.Clear(destination, destinationOffset, count * _userSize);
        if (block < 0) return 0;

        int ok = 0;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                long lba = block + i;
                if (lba >= _sectors) break;

                long offset = _startByte + lba * (long)_rawSize;
                Array.Clear(_scratchSector, 0, _rawSize);
                int got = _inner.ReadBytes(offset, _rawSize, _scratchSector, 0);
                if (got <= 0) { _bad++; continue; }

                var layout = _fixedOffset >= 0
                    ? new SectorLayout { DataOffset = _fixedOffset, DataSize = _userSize, Mode = -1 }
                    : Detect(_scratchSector, 0, _rawSize);

                int available = Math.Min(layout.DataSize, _rawSize - layout.DataOffset);
                available = Math.Min(available, got - layout.DataOffset);
                if (available <= 0) { _bad++; continue; }

                // i FORM2 hanno 2324 byte: se il chiamante ne vuole 2048 ne prendiamo la prima parte,
                // se il campo è più corto del blocco il resto resta a zero
                int copy = Math.Min(available, _userSize);
                Array.Copy(_scratchSector, layout.DataOffset, destination, destinationOffset + i * _userSize, copy);
                ok++;
            }
        }
        return ok;
    }

    /// <summary>Riconosce la struttura di un settore grezzo.</summary>
    public static SectorLayout Detect(byte[] buffer, int offset, int rawSectorSize)
    {
        var layout = new SectorLayout { DataOffset = 0, DataSize = rawSectorSize, Mode = -1, HasSync = false };

        // 2336: il settore comincia direttamente dal subheader MODE2, niente sync
        if (rawSectorSize == 2336)
        {
            layout.DataOffset = 8;
            layout.DataSize = 2048;
            layout.Mode = 2;
            if (offset + 8 <= buffer.Length && IsForm2(buffer, offset))
            {
                layout.Form2 = true;
                layout.DataSize = 2324;
            }
            return layout;
        }

        if (rawSectorSize < 2352 || !HasSync(buffer, offset))
            return layout;   // dati puri (2048) o settore senza sync: niente da sbucciare

        layout.HasSync = true;
        if (offset + 16 > buffer.Length) return layout;

        int mode = buffer[offset + 15] & 0x03;
        layout.Mode = mode;

        switch (mode)
        {
            case 1:
                layout.DataOffset = 16;
                layout.DataSize = 2048;
                break;

            case 2:
                // XA: il subheader di 4 byte è scritto due volte (16..19 e 20..23).
                // Se le due copie non coincidono il settore è MODE2 "formless" (CD-I): 2336 byte a offset 16.
                if (offset + 24 <= buffer.Length && SubHeaderDuplicated(buffer, offset + 16))
                {
                    layout.DataOffset = 24;
                    if (IsForm2(buffer, offset + 16))
                    {
                        layout.Form2 = true;
                        layout.DataSize = 2324;
                    }
                    else layout.DataSize = 2048;
                }
                else
                {
                    layout.DataOffset = 16;
                    layout.DataSize = 2336;
                }
                break;

            default:
                // mode 0 = settore vuoto, mode 3 = riservato: trattali come MODE1 per non perdere nulla
                layout.DataOffset = 16;
                layout.DataSize = 2048;
                break;
        }
        return layout;
    }

    /// <summary>True se nel buffer, a <paramref name="offset"/>, c'è il pattern di sync.</summary>
    public static bool HasSync(byte[] buffer, int offset)
    {
        if (buffer == null || offset < 0 || offset + 12 > buffer.Length) return false;
        for (int i = 0; i < 12; i++)
            if (buffer[offset + i] != SyncPattern[i]) return false;
        return true;
    }

    /// <summary>Le due copie del subheader XA (4+4 byte) coincidono?</summary>
    private static bool SubHeaderDuplicated(byte[] buffer, int subHeaderOffset)
    {
        if (subHeaderOffset + 8 > buffer.Length) return false;
        for (int i = 0; i < 4; i++)
            if (buffer[subHeaderOffset + i] != buffer[subHeaderOffset + 4 + i]) return false;
        return true;
    }

    /// <summary>Bit 5 del submode: settore FORM2 (2324 byte di dati utente).</summary>
    private static bool IsForm2(byte[] buffer, int subHeaderOffset)
    {
        if (subHeaderOffset + 3 > buffer.Length) return false;
        return (buffer[subHeaderOffset + 2] & 0x20) != 0;
    }

    /// <summary>
    /// Guarda i primi settori di un file per capire se è scritto a 2352 (con sync) o a 2048.
    /// Ritorna 2352, 2448, 2336 o 2048.
    /// </summary>
    public static int ProbeRawSectorSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ProbeRawSectorSize(fs);
        }
        catch { return 2048; }
    }

    /// <summary>Come sopra, su uno stream già aperto (la posizione viene ripristinata).</summary>
    public static int ProbeRawSectorSize(Stream stream)
    {
        try
        {
            long saved = stream.Position;
            try
            {
                long len = stream.Length;
                if (len < 2448) return len > 0 && len % 2352 == 0 ? 2352 : 2048;

                var head = new byte[2448 * 2];
                stream.Position = 0;
                int got = 0;
                while (got < head.Length)
                {
                    int n = stream.Read(head, got, head.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (!HasSync(head, 0)) return 2048;

                // sync anche a 2448 → c'è il subchannel in coda; altrimenti 2352
                if (len % 2448 == 0 && got >= 2448 + 12 && HasSync(head, 2448)) return 2448;
                if (got >= 2352 + 12 && HasSync(head, 2352)) return 2352;
                return len % 2352 == 0 ? 2352 : 2048;
            }
            finally { stream.Position = saved; }
        }
        catch { return 2048; }
    }

    public override void Dispose()
    {
        if (_ownsInner) _inner?.Dispose();
        base.Dispose();
    }
}
