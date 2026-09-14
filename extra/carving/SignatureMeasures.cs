using System.IO.Compression;
using System.Text;
using DVDRescue.Core;
using static DVDRescue.Carving.CarveIO;

namespace DVDRescue.Carving;

/// <summary>
/// Validatori rapidi (sul buffer di scansione) e misuratori di lunghezza (sulla sorgente).
/// Convenzione dei misuratori: valore &gt; 0 = lunghezza esatta, &lt; 0 = stima, 0 = ignota.
/// Tutto quanto sta qui gira su dati arbitrari: niente eccezioni, niente allocazioni grandi.
/// </summary>
internal static class Sigs
{
    // ============================== utilità ==============================

    /// <summary>Lettore sequenziale con buffer proprio: evita una chiamata per byte.</summary>
    private sealed class Cursor
    {
        private readonly IBlockSource _s;
        private readonly byte[] _b;
        private long _bufStart;
        private int _len, _i;

        public Cursor(IBlockSource s, long start, int bufSize = 64 * 1024)
        {
            _s = s; _bufStart = start; _b = new byte[bufSize];
        }

        public long Position => _bufStart + _i;

        public int Next()
        {
            if (_i >= _len)
            {
                _bufStart += _len;
                _i = 0;
                _len = Read(_s, _bufStart, _b, _b.Length);
                if (_len <= 0) { _len = 0; return -1; }
            }
            return _b[_i++];
        }
    }

    private static bool Printable(byte c) => c >= 0x20 && c < 0x7F;

    private static bool TagChar(byte c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
        c == ' ' || c == '.' || c == '_' || c == '-' || c == '+';

    /// <summary>I tag delle tabelle sfnt ammettono qualunque carattere stampabile ("OS/2", "cvt ").</summary>
    private static bool SfntTagChar(byte c) => c >= 0x20 && c <= 0x7E;

    private static bool Pow2(long v) => v > 0 && (v & (v - 1)) == 0;

    // ============================== JPEG ==============================

    public static bool VJpeg(byte[] b, int i, int n)
    {
        if (n < 4) return false;
        byte m = b[i + 3];
        return (m >= 0xC0 && m <= 0xCF && m != 0xC8) || (m >= 0xE0 && m <= 0xEF) ||
               m == 0xDB || m == 0xDD || m == 0xFE || m == 0xD8;
    }

    /// <summary>Percorre i marker fino a FFD9, saltando i dati entropici con il byte stuffing.</summary>
    public static long Jpeg(IBlockSource s, long off)
    {
        const long Max = 128L << 20;
        var b = new byte[4];
        long p = off + 2;
        long end = off + Max;

        while (p < end)
        {
            if (Read(s, p, b, 2) < 2) return 0;
            if (b[0] != 0xFF) return 0;
            byte m = b[1];

            if (m == 0xFF) { p++; continue; }                       // byte di riempimento
            if (m == 0xD9) return p + 2 - off;                      // fine immagine
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD8)) { p += 2; continue; }

            if (Read(s, p + 2, b, 2) < 2) return 0;
            int seg = (b[0] << 8) | b[1];
            if (seg < 2) return 0;

            if (m == 0xDA)                                          // inizio scansione
            {
                long q = JpegEntropy(s, p + 2 + seg, end);
                if (q < 0) return 0;
                p = q;
                continue;
            }
            p += 2 + seg;
        }
        return 0;
    }

    /// <summary>Posizione del primo marker vero dentro i dati entropici (salta FF00 e gli RST).</summary>
    private static long JpegEntropy(IBlockSource s, long from, long end)
    {
        var c = new Cursor(s, from);
        long p = from;
        while (p < end)
        {
            int v = c.Next();
            if (v < 0) return -1;
            p++;
            if (v != 0xFF) continue;

            int w = c.Next();
            if (w < 0) return -1;
            p++;
            if (w == 0x00 || w == 0xFF) continue;                   // stuffing o riempimento
            if (w >= 0xD0 && w <= 0xD7) continue;                   // marker di riavvio
            return p - 2;
        }
        return -1;
    }

    // ============================== PNG ==============================

    public static long Png(IBlockSource s, long off)
    {
        const long Max = 512L << 20;
        var h = new byte[8];
        long p = off + 8;

        for (int guard = 0; guard < 200000; guard++)
        {
            if (Read(s, p, h, 8) < 8) return 0;
            uint len = ByteUtils.U32BE(h, 0);
            if (len > (1u << 31)) return 0;

            for (int k = 4; k < 8; k++)
            {
                byte c = h[k];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return 0;
            }

            bool iend = h[4] == 'I' && h[5] == 'E' && h[6] == 'N' && h[7] == 'D';
            p += 12L + len;
            if (p - off > Max) return 0;
            if (iend) return p - off;
        }
        return 0;
    }

    // ============================== GIF ==============================

    public static bool VGif(byte[] b, int i, int n) =>
        n >= 6 && (b[i + 4] == '7' || b[i + 4] == '9') && b[i + 5] == 'a';

    public static long Gif(IBlockSource s, long off)
    {
        const long Max = 128L << 20;
        var h = new byte[16];
        if (Read(s, off, h, 13) < 13) return 0;

        long p = off + 13;
        if ((h[10] & 0x80) != 0) p += 3L * (2 << (h[10] & 7));      // tavola colori globale

        var one = new byte[1];
        for (int guard = 0; guard < 1000000; guard++)
        {
            if (p - off > Max) return 0;
            if (Read(s, p, one, 1) < 1) return 0;
            byte blk = one[0];

            if (blk == 0x3B) return p + 1 - off;                    // terminatore

            if (blk == 0x21)                                        // estensione
            {
                p += 2;                                             // introduttore + etichetta
                p = SkipGifSubBlocks(s, p, off + Max);
                if (p < 0) return 0;
                continue;
            }

            if (blk == 0x2C)                                        // descrittore immagine
            {
                if (Read(s, p + 1, h, 9) < 9) return 0;
                p += 10;
                if ((h[8] & 0x80) != 0) p += 3L * (2 << (h[8] & 7));
                p += 1;                                             // LZW minimum code size
                p = SkipGifSubBlocks(s, p, off + Max);
                if (p < 0) return 0;
                continue;
            }
            return 0;
        }
        return 0;
    }

    private static long SkipGifSubBlocks(IBlockSource s, long p, long limit)
    {
        var one = new byte[1];
        while (p < limit)
        {
            if (Read(s, p, one, 1) < 1) return -1;
            if (one[0] == 0) return p + 1;
            p += 1 + one[0];
        }
        return -1;
    }

    // ============================== BMP ==============================

    public static bool VBmp(byte[] b, int i, int n)
    {
        if (n < 30) return false;
        uint size = U32LE(b, i + 2);
        if (U32LE(b, i + 6) != 0) return false;                     // i due campi riservati
        uint offBits = U32LE(b, i + 10);
        uint dib = U32LE(b, i + 14);
        if (size < 26 || size > (512u << 20)) return false;
        if (offBits < 26 || offBits > size) return false;
        return dib == 12 || dib == 40 || dib == 52 || dib == 56 || dib == 64 || dib == 108 || dib == 124;
    }

    public static long Bmp(IBlockSource s, long off)
    {
        var h = new byte[6];
        if (Read(s, off, h, 6) < 6) return 0;
        long size = ByteUtils.U32LE(h, 2);
        return size >= 26 && size <= (512L << 20) ? size : 0;
    }

    // ============================== TIFF ==============================

    public static bool VTiffLe(byte[] b, int i, int n)
    {
        if (n < 8) return false;
        uint ifd = U32LE(b, i + 4);
        return ifd >= 8 && ifd < (1u << 31);
    }

    private static int TiffTypeSize(int t) => t switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0
    };

    /// <summary>
    /// Estensione massima raggiunta dalle IFD e dai dati che puntano (strip, tile, EXIF, SubIFD).
    /// È la lunghezza esatta per i TIFF scritti in modo compatto.
    /// </summary>
    public static long Tiff(IBlockSource s, long off)
    {
        var h = new byte[8];
        if (Read(s, off, h, 8) < 8) return 0;

        bool le;
        if (h[0] == 'I' && h[1] == 'I' && h[2] == 42 && h[3] == 0) le = true;
        else if (h[0] == 'M' && h[1] == 'M' && h[2] == 0 && h[3] == 42) le = false;
        else return 0;

        long extent = 8;
        var queue = new List<long> { le ? ByteUtils.U32LE(h, 4) : ByteUtils.U32BE(h, 4) };
        var seen = new HashSet<long>();
        var cb = new byte[2];
        var e = new byte[12];

        for (int qi = 0; qi < queue.Count && qi < 64; qi++)
        {
            long ifd = queue[qi];
            while (ifd >= 8 && ifd < (1L << 32) && seen.Add(ifd) && seen.Count < 256)
            {
                if (Read(s, off + ifd, cb, 2) < 2) break;
                int cnt = le ? ByteUtils.U16LE(cb, 0) : ByteUtils.U16BE(cb, 0);
                if (cnt <= 0 || cnt > 4096) break;

                long dirEnd = ifd + 2 + 12L * cnt + 4;
                if (dirEnd > extent) extent = dirEnd;

                long[] stripOff = null, stripLen = null, tileOff = null, tileLen = null;

                for (int i = 0; i < cnt; i++)
                {
                    if (Read(s, off + ifd + 2 + 12L * i, e, 12) < 12) return extent;
                    int tag = le ? ByteUtils.U16LE(e, 0) : ByteUtils.U16BE(e, 0);
                    int type = le ? ByteUtils.U16LE(e, 2) : ByteUtils.U16BE(e, 2);
                    long count = le ? ByteUtils.U32LE(e, 4) : ByteUtils.U32BE(e, 4);
                    int ts = TiffTypeSize(type);
                    if (ts == 0 || count < 0 || count > (1L << 28)) continue;

                    long size = ts * count;
                    long vo = le ? ByteUtils.U32LE(e, 8) : ByteUtils.U32BE(e, 8);
                    if (size > 4 && vo + size > extent) extent = vo + size;

                    switch (tag)
                    {
                        case 273: stripOff = TiffValues(s, off, e, le, ts, count, size); break;
                        case 279: stripLen = TiffValues(s, off, e, le, ts, count, size); break;
                        case 324: tileOff = TiffValues(s, off, e, le, ts, count, size); break;
                        case 325: tileLen = TiffValues(s, off, e, le, ts, count, size); break;
                        case 330:                                    // SubIFD
                        case 34665:                                  // EXIF IFD
                        case 34853:                                  // GPS IFD
                            var sub = TiffValues(s, off, e, le, ts, count, size);
                            if (sub != null)
                                foreach (long v in sub)
                                    if (v >= 8 && v < (1L << 32) && queue.Count < 64) queue.Add(v);
                            break;
                    }
                }

                extent = TiffStripExtent(extent, stripOff, stripLen);
                extent = TiffStripExtent(extent, tileOff, tileLen);

                if (Read(s, off + ifd + 2 + 12L * cnt, e, 4) < 4) break;
                ifd = le ? ByteUtils.U32LE(e, 0) : ByteUtils.U32BE(e, 0);
            }
        }

        return extent > 8 ? extent : 0;
    }

    private static long TiffStripExtent(long extent, long[] offs, long[] lens)
    {
        if (offs == null || lens == null) return extent;
        int n = Math.Min(offs.Length, lens.Length);
        for (int i = 0; i < n; i++)
        {
            long e = offs[i] + lens[i];
            if (e > extent && e < (1L << 40)) extent = e;
        }
        return extent;
    }

    private static long[] TiffValues(IBlockSource s, long off, byte[] entry, bool le, int ts, long count, long size)
    {
        if (count <= 0 || count > 262144) return null;
        var data = new byte[size <= 4 ? 4 : (int)size];

        if (size <= 4) Array.Copy(entry, 8, data, 0, 4);
        else
        {
            long vo = le ? ByteUtils.U32LE(entry, 8) : ByteUtils.U32BE(entry, 8);
            if (Read(s, off + vo, data, (int)size) < size) return null;
        }

        var res = new long[count];
        for (int i = 0; i < count; i++)
        {
            int o = i * ts;
            res[i] = ts switch
            {
                1 => data[o],
                2 => le ? ByteUtils.U16LE(data, o) : ByteUtils.U16BE(data, o),
                4 => le ? ByteUtils.U32LE(data, o) : ByteUtils.U32BE(data, o),
                _ => 0
            };
        }
        return res;
    }

    public static string CTiff(IBlockSource s, long off)
    {
        // Il produttore (tag 271 Make) distingue i RAW che condividono il magic TIFF.
        var h = new byte[8];
        if (Read(s, off, h, 8) < 8) return null;
        bool le = h[0] == 'I';
        long ifd = le ? ByteUtils.U32LE(h, 4) : ByteUtils.U32BE(h, 4);
        var cb = new byte[2];
        if (Read(s, off + ifd, cb, 2) < 2) return null;
        int cnt = le ? ByteUtils.U16LE(cb, 0) : ByteUtils.U16BE(cb, 0);
        if (cnt <= 0 || cnt > 4096) return null;

        var e = new byte[12];
        for (int i = 0; i < cnt; i++)
        {
            if (Read(s, off + ifd + 2 + 12L * i, e, 12) < 12) break;
            int tag = le ? ByteUtils.U16LE(e, 0) : ByteUtils.U16BE(e, 0);
            if (tag != 271) continue;

            long count = le ? ByteUtils.U32LE(e, 4) : ByteUtils.U32BE(e, 4);
            if (count <= 0 || count > 64) break;
            var val = new byte[(int)count];
            if (count <= 4) Array.Copy(e, 8, val, 0, (int)count);
            else
            {
                long vo = le ? ByteUtils.U32LE(e, 8) : ByteUtils.U32BE(e, 8);
                if (Read(s, off + vo, val, (int)count) < count) break;
            }
            string make = Encoding.ASCII.GetString(val).TrimEnd('\0', ' ').ToUpperInvariant();
            if (make.StartsWith("NIKON")) return "nef";
            if (make.StartsWith("SONY")) return "arw";
            if (make.StartsWith("CANON")) return "cr2";
            if (make.StartsWith("OLYMPUS")) return "orf";
            if (make.StartsWith("PENTAX")) return "pef";
            if (make.StartsWith("PANASONIC")) return "rw2";
            break;
        }
        return null;
    }

    // ============================== RIFF (WAV/AVI/WebP) ==============================

    public static long Riff(IBlockSource s, long off)
    {
        var h = new byte[8];
        if (Read(s, off, h, 8) < 8) return 0;
        long len = 8L + ByteUtils.U32LE(h, 4);
        return len >= 12 && len <= (8L << 30) ? len : 0;
    }

    // ============================== ISO BMFF (MP4/MOV/HEIC/3GP) ==============================

    public static bool VFtyp(byte[] b, int i, int n)
    {
        if (n < 16) return false;
        uint boxSize = U32BE(b, i);
        if (boxSize < 12 || boxSize > 4096 || (boxSize & 3) != 0) return false;
        for (int k = 8; k < 12; k++) if (!TagChar(b[i + k])) return false;
        return true;
    }

    /// <summary>Somma la catena dei box di primo livello finché restano validi.</summary>
    public static long IsoBmff(IBlockSource s, long off)
    {
        const long Max = 8L << 30;
        var h = new byte[16];
        long p = off;
        int boxes = 0;

        while (p - off < Max)
        {
            int got = Read(s, p, h, 16);
            if (got < 8) break;

            long size = ByteUtils.U32BE(h, 0);
            bool tagOk = true;
            for (int k = 4; k < 8; k++) if (!TagChar(h[k])) { tagOk = false; break; }
            if (!tagOk) break;

            if (size == 1)
            {
                if (got < 16) break;
                size = (long)ByteUtils.U64BE(h, 8);
                if (size < 16) break;
            }
            else if (size == 0)
            {
                // il box arriva a fine file: non calcolabile
                return boxes > 0 ? -(p - off) : 0;
            }
            else if (size < 8) break;

            p += size;
            boxes++;
            if (boxes > 100000) break;
        }

        if (boxes < 2) return 0;                                    // servono almeno ftyp + un altro
        return p - off;
    }

    public static string CFtyp(IBlockSource s, long off)
    {
        var h = new byte[12];
        if (Read(s, off, h, 12) < 12) return null;
        string brand = Encoding.ASCII.GetString(h, 8, 4).Trim();
        switch (brand)
        {
            case "heic": case "heix": case "hevc": case "heim": case "heis": case "mif1": case "msf1":
                return "heic";
            case "avif": case "avis": return "avif";
            case "qt": return "mov";
            case "M4A": return "m4a";
            case "M4V": return "m4v";
            case "crx": return "cr3";
        }
        if (brand.StartsWith("3g")) return "3gp";
        return "mp4";
    }

    // ============================== PSD ==============================

    public static long Psd(IBlockSource s, long off)
    {
        var h = new byte[26];
        if (Read(s, off, h, 26) < 26) return 0;

        int channels = ByteUtils.U16BE(h, 12);
        long height = ByteUtils.U32BE(h, 14);
        long width = ByteUtils.U32BE(h, 18);
        int depth = ByteUtils.U16BE(h, 22);

        if (channels <= 0 || channels > 56 || height <= 0 || width <= 0) return 0;
        if (depth != 1 && depth != 8 && depth != 16 && depth != 32) return 0;
        if (height > 300000 || width > 300000) return 0;

        long p = off + 26;
        var u = new byte[4];
        for (int sec = 0; sec < 3; sec++)                           // color mode, risorse, livelli
        {
            if (Read(s, p, u, 4) < 4) return 0;
            long len = ByteUtils.U32BE(u, 0);
            if (len < 0 || len > (1L << 31)) return 0;
            p += 4 + len;
        }

        var c = new byte[2];
        if (Read(s, p, c, 2) < 2) return 0;
        int comp = ByteUtils.U16BE(c, 0);
        p += 2;

        long rows = height * channels;
        if (comp == 0)
        {
            long bytesPerRow = (width * depth + 7) / 8;
            return p + bytesPerRow * rows - off;
        }
        if (comp == 1)
        {
            if (rows > 4_000_000) return 0;
            var tbl = new byte[(int)(rows * 2)];
            if (Read(s, p, tbl, tbl.Length) < tbl.Length) return 0;
            long sum = 0;
            for (int i = 0; i < rows; i++) sum += ByteUtils.U16BE(tbl, i * 2);
            return p + tbl.Length + sum - off;
        }
        return 0;                                                    // zip: non calcolabile
    }

    // ============================== PDF ==============================

    public static bool VPdf(byte[] b, int i, int n) =>
        n >= 8 && b[i + 5] >= '1' && b[i + 5] <= '2' && b[i + 6] == '.' && b[i + 7] >= '0' && b[i + 7] <= '9';

    private static readonly byte[] PdfEof = Encoding.ASCII.GetBytes("%%EOF");
    private static readonly byte[] PdfHdr = Encoding.ASCII.GetBytes("%PDF-");

    public static long Pdf(IBlockSource s, long off)
    {
        const long Max = 512L << 20;

        // un altro %PDF- chiude il campo di ricerca: è un file diverso
        long nextHdr = Find(s, off + 5, Max, PdfHdr);
        long span = (nextHdr < 0 ? off + Max : nextHdr) - off;
        if (span <= 0) return 0;

        long last = FindLast(s, off, span, PdfEof);
        if (last < 0) return 0;

        long end = last + 5;
        end += TrailingEol(s, end);
        return end - off;
    }

    // ============================== RTF ==============================

    public static long Rtf(IBlockSource s, long off)
    {
        const long Max = 128L << 20;
        var c = new Cursor(s, off);
        long p = off;
        int depth = 0;

        while (p - off < Max)
        {
            int v = c.Next();
            if (v < 0) return 0;
            p++;

            if (v == '\\')                                           // sequenza di escape
            {
                if (c.Next() < 0) return 0;
                p++;
                continue;
            }
            if (v == '{') depth++;
            else if (v == '}')
            {
                depth--;
                if (depth == 0) return p + TrailingEol(s, p) - off;
                if (depth < 0) return 0;
            }
        }
        return 0;
    }

    // ============================== OLE2 / Compound File ==============================

    public static long Ole2(IBlockSource s, long off)
    {
        var h = new byte[512];
        if (Read(s, off, h, 512) < 512) return 0;

        int shift = ByteUtils.U16LE(h, 30);
        if (shift < 7 || shift > 20) return 0;
        int sectorSize = 1 << shift;

        long numFat = ByteUtils.U32LE(h, 44);
        if (numFat <= 0 || numFat > 1_000_000) return 0;

        long difatStart = ByteUtils.U32LE(h, 68);
        long numDifat = ByteUtils.U32LE(h, 72);
        if (numDifat > 1_000_000) return 0;

        long maxUsed = -1;
        var fat = new byte[sectorSize];
        var difat = new byte[sectorSize];
        int entriesPerSector = sectorSize / 4;
        long read = 0;
        long difatSector = difatStart;
        int difatIndex = 0;

        while (read < numFat && read < 1_000_000)
        {
            long fatSector;
            if (read < 109)
            {
                fatSector = ByteUtils.U32LE(h, 76 + (int)read * 4);
            }
            else
            {
                if (difatSector >= 0xFFFFFFFAL || difatSector < 0) break;
                if (difatIndex == 0 && Read(s, off + (difatSector + 1) * sectorSize, difat, sectorSize) < sectorSize) break;
                fatSector = ByteUtils.U32LE(difat, difatIndex * 4);
                difatIndex++;
                if (difatIndex >= entriesPerSector - 1)
                {
                    difatSector = ByteUtils.U32LE(difat, (entriesPerSector - 1) * 4);
                    difatIndex = 0;
                }
            }
            read++;

            if (fatSector >= 0xFFFFFFFAL) continue;
            if (fatSector > maxUsed) maxUsed = fatSector;            // il settore FAT stesso è allocato
            if (Read(s, off + (fatSector + 1) * sectorSize, fat, sectorSize) < sectorSize) break;

            for (int i = entriesPerSector - 1; i >= 0; i--)
            {
                uint v = ByteUtils.U32LE(fat, i * 4);
                if (v == 0xFFFFFFFFu) continue;                      // libero
                long sect = (read - 1) * entriesPerSector + i;
                if (sect > maxUsed) maxUsed = sect;
                break;
            }
        }

        if (maxUsed < 0) return 0;
        long len = (maxUsed + 2) * (long)sectorSize;
        return len > 0 && len <= (4L << 30) ? len : 0;
    }

    public static string COle2(IBlockSource s, long off)
    {
        var h = new byte[512];
        if (Read(s, off, h, 512) < 512) return null;
        int shift = ByteUtils.U16LE(h, 30);
        if (shift < 7 || shift > 20) return null;
        int sectorSize = 1 << shift;

        long dir = ByteUtils.U32LE(h, 48);
        var sec = new byte[sectorSize];
        var names = new StringBuilder();

        // il primo settore di directory basta a riconoscere il tipo
        if (dir < 0xFFFFFFFAL && Read(s, off + (dir + 1) * sectorSize, sec, sectorSize) >= sectorSize)
        {
            for (int e = 0; e + 128 <= sectorSize; e += 128)
            {
                int nameLen = ByteUtils.U16LE(sec, e + 64);
                if (nameLen < 2 || nameLen > 64) continue;
                names.Append(ByteUtils.Utf16LE(sec, e, nameLen - 2)).Append('\n');
            }
        }

        string n = names.ToString();
        if (n.Contains("WordDocument")) return "doc";
        if (n.Contains("Workbook") || n.Contains("Book")) return "xls";
        if (n.Contains("PowerPoint Document")) return "ppt";
        if (n.Contains("VisioDocument")) return "vsd";
        return null;
    }

    // ============================== ZIP ==============================

    private static readonly byte[] ZipEocd = { 0x50, 0x4B, 0x05, 0x06 };

    public static long Zip(IBlockSource s, long off)
    {
        const long Max = 4L << 30;
        var b = new byte[46];
        long p = off;

        // 1) catena degli header locali
        for (int guard = 0; guard < 500000; guard++)
        {
            if (Read(s, p, b, 30) < 30) return ZipByEocd(s, off, Max);
            uint sig = ByteUtils.U32LE(b, 0);

            if (sig == 0x04034B50)
            {
                ushort flags = ByteUtils.U16LE(b, 6);
                uint csize = ByteUtils.U32LE(b, 18);
                int nlen = ByteUtils.U16LE(b, 26), elen = ByteUtils.U16LE(b, 28);
                if ((flags & 8) != 0 && csize == 0) return ZipByEocd(s, off, Max);   // data descriptor
                if (csize == 0xFFFFFFFF) return ZipByEocd(s, off, Max);              // zip64
                p += 30L + nlen + elen + csize;
                if (p - off > Max) return ZipByEocd(s, off, Max);
                continue;
            }
            if (sig == 0x02014B50) break;
            return ZipByEocd(s, off, Max);
        }

        // 2) directory centrale
        for (int guard = 0; guard < 500000; guard++)
        {
            if (Read(s, p, b, 46) < 46) return ZipByEocd(s, off, Max);
            uint sig = ByteUtils.U32LE(b, 0);

            if (sig == 0x02014B50)
            {
                p += 46L + ByteUtils.U16LE(b, 28) + ByteUtils.U16LE(b, 30) + ByteUtils.U16LE(b, 32);
                if (p - off > Max) return ZipByEocd(s, off, Max);
                continue;
            }
            if (sig == 0x06054B50) return p + 22 + ByteUtils.U16LE(b, 20) - off;
            return ZipByEocd(s, off, Max);
        }
        return ZipByEocd(s, off, Max);
    }

    /// <summary>Ripiego: cerca l'EOCD coerente più lontano (regge i data descriptor e lo zip64).</summary>
    private static long ZipByEocd(IBlockSource s, long off, long max)
    {
        long best = -1;
        long p = off;
        var b = new byte[22];

        for (int guard = 0; guard < 100000; guard++)
        {
            long hit = Find(s, p, max - (p - off), ZipEocd);
            if (hit < 0) break;
            if (Read(s, hit, b, 22) >= 22)
            {
                long cdSize = ByteUtils.U32LE(b, 12);
                long cdOff = ByteUtils.U32LE(b, 16);
                if (cdOff != 0xFFFFFFFFL && off + cdOff + cdSize == hit) best = hit + 22 + ByteUtils.U16LE(b, 20);
            }
            p = hit + 4;
            if (p - off >= max) break;
        }
        return best < 0 ? 0 : best - off;
    }

    public static string CZip(IBlockSource s, long off)
    {
        var b = new byte[30];
        if (Read(s, off, b, 30) < 30) return null;
        int nlen = ByteUtils.U16LE(b, 26), elen = ByteUtils.U16LE(b, 28);
        if (nlen <= 0 || nlen > 512) return null;

        var nameBytes = new byte[nlen];
        if (Read(s, off + 30, nameBytes, nlen) < nlen) return null;
        string name = Encoding.UTF8.GetString(nameBytes);

        if (name == "mimetype")
        {
            long dataOff = off + 30 + nlen + elen;
            int csize = (int)Math.Min(ByteUtils.U32LE(b, 18), 200);
            if (csize > 0 && ByteUtils.U16LE(b, 8) == 0)
            {
                var mt = new byte[csize];
                if (Read(s, dataOff, mt, csize) >= csize)
                {
                    string m = Encoding.ASCII.GetString(mt);
                    if (m.Contains("epub")) return "epub";
                    if (m.Contains("opendocument.text")) return "odt";
                    if (m.Contains("opendocument.spreadsheet")) return "ods";
                    if (m.Contains("opendocument.presentation")) return "odp";
                    if (m.Contains("opendocument.graphics")) return "odg";
                }
            }
            return "zip";
        }

        if (name == "[Content_Types].xml")
        {
            string xml = ZipEntryText(s, off, b, nlen, elen, 64 * 1024);
            if (xml != null)
            {
                if (xml.Contains("wordprocessingml.document")) return "docx";
                if (xml.Contains("spreadsheetml.sheet")) return "xlsx";
                if (xml.Contains("presentationml.presentation")) return "pptx";
                if (xml.Contains("wordprocessingml.template")) return "dotx";
            }
            return "zip";
        }

        if (name.StartsWith("META-INF/")) return "jar";
        if (name.StartsWith("word/")) return "docx";
        if (name.StartsWith("xl/")) return "xlsx";
        if (name.StartsWith("ppt/")) return "pptx";
        return "zip";
    }

    /// <summary>Estrae in memoria il primo file dello ZIP (limitato) per riconoscere il sottotipo.</summary>
    private static string ZipEntryText(IBlockSource s, long off, byte[] local, int nlen, int elen, int cap)
    {
        try
        {
            int method = ByteUtils.U16LE(local, 8);
            long csize = ByteUtils.U32LE(local, 18);
            long usize = ByteUtils.U32LE(local, 22);
            if (csize <= 0 || csize > (16L << 20)) return null;
            long dataOff = off + 30 + nlen + elen;

            if (method == 0)
            {
                int n = (int)Math.Min(csize, cap);
                var raw = new byte[n];
                if (Read(s, dataOff, raw, n) < n) return null;
                return Encoding.UTF8.GetString(raw);
            }
            if (method != 8) return null;
            if (usize > (64L << 20)) return null;

            using var src = new CountingBlockStream(s, dataOff, csize, 8192);
            using var ds = new DeflateStream(src, CompressionMode.Decompress, true);
            var outBuf = new byte[Math.Min(cap, 64 * 1024)];
            int total = 0, got;
            var sb = new StringBuilder();
            while (total < cap && (got = ds.Read(outBuf, 0, outBuf.Length)) > 0)
            {
                sb.Append(Encoding.UTF8.GetString(outBuf, 0, got));
                total += got;
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    // ============================== testo (EML, vCard, XML, JSON) ==============================

    public static bool VText(byte[] b, int i, int n)
    {
        int len = Math.Min(n, 200);
        if (len < 32) return false;
        for (int k = 0; k < len; k++)
        {
            byte c = b[i + k];
            if (c == 0x0D || c == 0x0A || c == 0x09) continue;
            if (!Printable(c)) return false;
        }
        return true;
    }

    private static readonly byte[] VcardEnd = Encoding.ASCII.GetBytes("END:VCARD");
    private static readonly byte[] VcardBegin = Encoding.ASCII.GetBytes("BEGIN:VCARD");

    public static long VCard(IBlockSource s, long off)
    {
        const long Max = 16L << 20;
        long p = off;
        long end = -1;

        for (int guard = 0; guard < 100000; guard++)
        {
            long e = Find(s, p, Max - (p - off), VcardEnd);
            if (e < 0) break;
            end = e + VcardEnd.Length;
            end += TrailingEol(s, end);

            // altre schede a seguire?
            var probe = new byte[VcardBegin.Length];
            if (Read(s, end, probe, probe.Length) < probe.Length) break;
            bool more = true;
            for (int k = 0; k < probe.Length; k++) if (probe[k] != VcardBegin[k]) { more = false; break; }
            if (!more) break;
            p = end;
        }
        return end > off ? end - off : 0;
    }

    public static bool VXml(byte[] b, int i, int n) =>
        n >= 8 && (b[i + 5] == ' ' || b[i + 5] == 'v') && VText(b, i, n);

    /// <summary>Trova la chiusura dell'elemento radice.</summary>
    public static long Xml(IBlockSource s, long off)
    {
        const long Max = 64L << 20;
        var c = new Cursor(s, off);
        long p = off;

        // salta il prologo fino al primo tag di elemento
        var name = new StringBuilder(64);
        bool inTag = false;
        while (p - off < 65536)
        {
            int v = c.Next();
            if (v < 0) return 0;
            p++;
            if (!inTag)
            {
                if (v == '<')
                {
                    int w = c.Next();
                    if (w < 0) return 0;
                    p++;
                    if (w == '?' || w == '!') { inTag = false; if (!XmlSkipTo(c, ref p, off)) return 0; continue; }
                    if (w == '/') return 0;
                    name.Clear();
                    name.Append((char)w);
                    inTag = true;
                }
                continue;
            }
            if (v == ' ' || v == '>' || v == '/' || v == '\t' || v == '\r' || v == '\n') break;
            if (name.Length < 64) name.Append((char)v);
        }

        if (name.Length == 0) return 0;
        var close = Encoding.ASCII.GetBytes("</" + name + ">");

        // un altro prologo XML chiude il campo di ricerca: è un file diverso
        long nextDecl = Find(s, off + 5, Max, Encoding.ASCII.GetBytes("<?xml"));
        long span = (nextDecl < 0 ? off + Max : nextDecl) - off;
        if (span <= 0) return 0;

        long hit = FindLast(s, off, span, close);
        if (hit < 0) return 0;
        long end = hit + close.Length;
        end += TrailingEol(s, end);
        return end - off;
    }

    private static bool XmlSkipTo(Cursor c, ref long p, long off)
    {
        while (p - off < 65536)
        {
            int v = c.Next();
            if (v < 0) return false;
            p++;
            if (v == '>') return true;
        }
        return false;
    }

    /// <summary>
    /// Dopo la graffa iniziale deve arrivare una chiave fra virgolette seguita dai due punti,
    /// eventualmente dopo spazi o a capo (i JSON scritti per essere letti sono indentati).
    /// </summary>
    public static bool VJson(byte[] b, int i, int n)
    {
        if (n < 16) return false;
        int limit = i + Math.Min(n, 128);
        int k = i + 1;

        int ws = 0;
        while (k < limit && (b[k] == ' ' || b[k] == '\t' || b[k] == '\r' || b[k] == '\n')) { k++; if (++ws > 16) return false; }
        if (k >= limit || b[k] != '"') return false;
        k++;

        int keyLen = 0;
        while (k < limit && b[k] != '"')
        {
            if (!Printable(b[k])) return false;
            k++;
            if (++keyLen > 64) return false;
        }
        if (keyLen == 0 || k >= limit) return false;
        k++;

        while (k < limit && (b[k] == ' ' || b[k] == '\t')) k++;
        return k < limit && b[k] == ':';
    }

    /// <summary>Bilanciamento di graffe e quadre, con le stringhe gestite a parte.</summary>
    public static long Json(IBlockSource s, long off)
    {
        const long Max = 256L << 20;
        var c = new Cursor(s, off);
        long p = off;
        int depth = 0;
        bool inStr = false, esc = false;

        while (p - off < Max)
        {
            int v = c.Next();
            if (v < 0) return 0;
            p++;

            if (inStr)
            {
                if (esc) { esc = false; continue; }
                if (v == '\\') { esc = true; continue; }
                if (v == '"') inStr = false;
                continue;
            }

            switch (v)
            {
                case '"': inStr = true; break;
                case '{': case '[': depth++; break;
                case '}': case ']':
                    depth--;
                    if (depth == 0) return p + TrailingEol(s, p) - off;
                    if (depth < 0) return 0;
                    break;
                default:
                    if (v > 0x7E || (v < 0x20 && v != 0x09 && v != 0x0A && v != 0x0D)) return 0;
                    break;
            }
        }
        return 0;
    }

    // ============================== PST / REGF ==============================

    public static bool VPst(byte[] b, int i, int n)
    {
        if (n < 24) return false;
        if (b[i + 8] != 'S' || b[i + 9] != 'M') return false;
        int ver = U16LE(b, i + 10);
        return ver == 14 || ver == 15 || ver == 23 || ver == 36 || ver == 37;
    }

    public static long Pst(IBlockSource s, long off)
    {
        var h = new byte[0xC0];
        if (Read(s, off, h, 0xC0) < 0xC0) return 0;
        int ver = ByteUtils.U16LE(h, 10);
        long size = ver >= 23 ? (long)ByteUtils.U64LE(h, 0xB8) : ByteUtils.U32LE(h, 0xA8);
        return size >= 512 && size < (64L << 30) ? size : 0;
    }

    public static bool VRegf(byte[] b, int i, int n)
    {
        if (n < 48) return false;
        if (U32LE(b, i + 20) != 1) return false;                     // major version
        uint minor = U32LE(b, i + 24);
        return minor <= 16;
    }

    public static long Regf(IBlockSource s, long off)
    {
        var h = new byte[48];
        if (Read(s, off, h, 48) < 48) return 0;
        long hbins = ByteUtils.U32LE(h, 40);
        if (hbins <= 0 || hbins > (2L << 30) || (hbins % 4096) != 0) return 0;
        return 4096 + hbins;
    }

    // ============================== RAR / 7z ==============================

    public static long Rar4(IBlockSource s, long off)
    {
        const long Max = 8L << 30;
        var b = new byte[11];
        long p = off;

        for (int guard = 0; guard < 500000; guard++)
        {
            if (Read(s, p, b, 11) < 7) break;
            int type = b[2];
            int flags = ByteUtils.U16LE(b, 3);
            long size = ByteUtils.U16LE(b, 5);
            if (size < 7) break;

            long add = 0;
            if ((flags & 0x8000) != 0)
            {
                if (Read(s, p + 7, b, 4) < 4) break;
                add = ByteUtils.U32LE(b, 0);
            }
            p += size + add;
            if (p - off > Max) break;
            if (type == 0x7B) return p - off;                        // blocco di fine archivio
            if (type < 0x72 || type > 0x7B) break;
        }
        long len = p - off;
        return len > 7 ? -len : 0;
    }

    private static long Vint(IBlockSource s, ref long p, int maxBytes = 10)
    {
        long v = 0;
        int shift = 0;
        var b = new byte[1];
        for (int i = 0; i < maxBytes; i++)
        {
            if (Read(s, p, b, 1) < 1) return -1;
            p++;
            v |= (long)(b[0] & 0x7F) << shift;
            if ((b[0] & 0x80) == 0) return v;
            shift += 7;
            if (shift > 56) return -1;
        }
        return -1;
    }

    public static long Rar5(IBlockSource s, long off)
    {
        const long Max = 8L << 30;
        long p = off + 8;

        for (int guard = 0; guard < 500000; guard++)
        {
            long q = p + 4;                                          // salta il CRC32 dell'header
            long hdrStart = q;
            long hdrSize = Vint(s, ref q);
            if (hdrSize <= 0 || hdrSize > (1L << 24)) break;
            long vintLen = q - hdrStart;

            long r = q;
            long type = Vint(s, ref r);
            long flags = Vint(s, ref r);
            if (type < 0 || flags < 0) break;
            if ((flags & 1) != 0 && Vint(s, ref r) < 0) break;        // extra area
            long dataSize = 0;
            if ((flags & 2) != 0)
            {
                dataSize = Vint(s, ref r);
                if (dataSize < 0) break;
            }

            p += 4 + vintLen + hdrSize + dataSize;
            if (p - off > Max) break;
            if (type == 5) return p - off;                            // fine archivio
            if (type < 1 || type > 5) break;
        }
        long len = p - off;
        return len > 8 ? -len : 0;
    }

    public static long SevenZip(IBlockSource s, long off)
    {
        var h = new byte[32];
        if (Read(s, off, h, 32) < 32) return 0;
        long nextOff = (long)ByteUtils.U64LE(h, 12);
        long nextSize = (long)ByteUtils.U64LE(h, 20);
        if (nextOff < 0 || nextSize < 0 || nextOff > (8L << 30) || nextSize > (1L << 30)) return 0;
        return 32 + nextOff + nextSize;
    }

    // ============================== compressori a flusso ==============================

    public static bool VGzip(byte[] b, int i, int n)
    {
        if (n < 10) return false;
        if ((b[i + 3] & 0xE0) != 0) return false;                    // bit di flag non definiti
        byte os = b[i + 9];
        return os <= 13 || os == 255;
    }

    private static long SkipZeroTerminated(IBlockSource s, long p, long limit)
    {
        var b = new byte[256];
        while (p < limit)
        {
            int got = Read(s, p, b, b.Length);
            if (got <= 0) return -1;
            for (int i = 0; i < got; i++) if (b[i] == 0) return p + i + 1;
            p += got;
        }
        return -1;
    }

    /// <summary>
    /// Decomprime a vuoto per sapere quanti byte occupa il flusso deflate, poi aggancia
    /// il trailer (CRC32 + dimensione) per azzerare l'errore di lettura anticipata.
    /// </summary>
    public static long Gzip(IBlockSource s, long off)
    {
        const long MaxIn = 2L << 30;
        var h = new byte[10];
        if (Read(s, off, h, 10) < 10) return 0;
        if (h[0] != 0x1F || h[1] != 0x8B || h[2] != 8) return 0;

        int flg = h[3];
        long p = off + 10;
        long limit = off + MaxIn;

        if ((flg & 4) != 0)
        {
            var x = new byte[2];
            if (Read(s, p, x, 2) < 2) return 0;
            p += 2 + ByteUtils.U16LE(x, 0);
        }
        if ((flg & 8) != 0) { p = SkipZeroTerminated(s, p, limit); if (p < 0) return 0; }
        if ((flg & 16) != 0) { p = SkipZeroTerminated(s, p, limit); if (p < 0) return 0; }
        if ((flg & 2) != 0) p += 2;
        if (p <= off || p >= limit) return 0;

        const int Grain = 64;
        uint crc = 0;
        long total = 0;
        long consumed;

        try
        {
            using var bs = new CountingBlockStream(s, p, MaxIn, Grain);
            using (var ds = new DeflateStream(bs, CompressionMode.Decompress, true))
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    crc = Crc32Continue(crc, buf, 0, n);
                    total += n;
                    if (total > (16L << 30)) return 0;
                }
            }
            consumed = bs.Consumed;
        }
        catch { return 0; }

        if (consumed <= 0) return 0;

        var want = new byte[8];
        want[0] = (byte)crc; want[1] = (byte)(crc >> 8); want[2] = (byte)(crc >> 16); want[3] = (byte)(crc >> 24);
        uint isize = (uint)total;
        want[4] = (byte)isize; want[5] = (byte)(isize >> 8); want[6] = (byte)(isize >> 16); want[7] = (byte)(isize >> 24);

        long end = p + consumed;
        long from = Math.Max(p, end - Grain - 16);
        var probe = new byte[8 + (int)(end + 16 - from)];
        int got = Read(s, from, probe, probe.Length);
        for (int i = 0; i + 8 <= got; i++)
        {
            bool ok = true;
            for (int k = 0; k < 8; k++) if (probe[i + k] != want[k]) { ok = false; break; }
            if (ok) return from + i + 8 - off;
        }
        return -(end + 8 - off);
    }

    public static bool VBzip2(byte[] b, int i, int n)
    {
        if (n < 10) return false;
        if (b[i + 3] < '1' || b[i + 3] > '9') return false;
        return b[i + 4] == 0x31 && b[i + 5] == 0x41 && b[i + 6] == 0x59 &&
               b[i + 7] == 0x26 && b[i + 8] == 0x53 && b[i + 9] == 0x59;
    }

    /// <summary>
    /// Il magic di fine flusso (0x177245385090) non è allineato ai byte: va cercato
    /// su tutti gli otto scorrimenti di bit.
    /// </summary>
    public static long Bzip2(IBlockSource s, long off)
    {
        const long Max = 4L << 30;
        const ulong Magic = 0x177245385090UL;
        const int Chunk = 64 * 1024;

        var buf = new byte[Chunk + 8];
        long pos = off + 4;

        while (pos - off < Max)
        {
            int got = Read(s, pos, buf, buf.Length);
            if (got < 11) break;

            int last = got - 7;
            for (int i = 0; i < last; i++)
            {
                ulong v = 0;
                for (int k = 0; k < 7; k++) v = (v << 8) | buf[i + k];
                for (int sh = 0; sh < 8; sh++)
                {
                    if (((v >> (8 - sh)) & 0xFFFFFFFFFFFFUL) != Magic) continue;
                    long bitStart = (pos + i - off) * 8 + sh;
                    long bitEnd = bitStart + 48 + 32;                // magic + CRC combinato
                    return (bitEnd + 7) / 8;
                }
            }

            if (got < buf.Length) break;
            pos += Chunk;
        }
        return 0;
    }

    private static readonly byte[] XzFooterTag = { 0x59, 0x5A };

    public static long Xz(IBlockSource s, long off)
    {
        const long Max = 8L << 30;
        var hdr = new byte[12];
        if (Read(s, off, hdr, 12) < 12) return 0;

        long streamStart = off;
        long best = 0;
        var f = new byte[12];

        for (int stream = 0; stream < 64; stream++)
        {
            long p = streamStart;
            long found = -1;

            for (int guard = 0; guard < 200000; guard++)
            {
                long hit = Find(s, p, Max - (p - off), XzFooterTag);
                if (hit < 0) break;
                long fs = hit - 10;                                  // inizio del footer
                p = hit + 1;

                if (fs < streamStart + 12 || ((fs - streamStart) & 3) != 0) continue;
                if (Read(s, fs, f, 12) < 12) break;
                if (f[8] != hdr[6] || f[9] != hdr[7]) continue;      // stream flags coerenti
                if (Crc32(f, 4, 6) != ByteUtils.U32LE(f, 0)) continue;

                found = fs + 12;
                break;
            }

            if (found < 0) break;
            best = found;

            // eventuale flusso concatenato (con padding a multipli di 4)
            long q = found;
            var probe = new byte[6];
            bool another = false;
            for (int pad = 0; pad < 4; pad++)
            {
                if (Read(s, q, probe, 6) < 6) break;
                if (probe[0] == 0xFD && probe[1] == 0x37 && probe[2] == 0x7A &&
                    probe[3] == 0x58 && probe[4] == 0x5A && probe[5] == 0x00)
                {
                    another = true;
                    break;
                }
                if (probe[0] != 0 || probe[1] != 0 || probe[2] != 0 || probe[3] != 0) break;
                q += 4;
            }
            if (!another) break;
            streamStart = q;
            if (Read(s, streamStart, hdr, 12) < 12) break;
        }

        return best > off ? best - off : 0;
    }

    // ============================== TAR ==============================

    public static bool VTar(byte[] b, int i, int n)
    {
        if (n < 512) return false;
        if (b[i] == 0) return false;
        for (int k = 0; k < 100 && b[i + k] != 0; k++) if (!Printable(b[i + k])) return false;
        for (int k = 124; k < 135; k++)
        {
            byte c = b[i + k];
            if (c == 0 || c == ' ') continue;
            if (c < '0' || c > '7') return false;
        }
        return true;
    }

    private static long TarOctal(byte[] b, int o, int len)
    {
        long v = 0;
        for (int i = 0; i < len; i++)
        {
            byte c = b[o + i];
            if (c == 0 || c == ' ') break;
            if (c < '0' || c > '7') return -1;
            v = v * 8 + (c - '0');
        }
        return v;
    }

    public static long Tar(IBlockSource s, long off)
    {
        const long Max = 8L << 30;
        var h = new byte[512];
        long p = off;

        for (int guard = 0; guard < 1000000; guard++)
        {
            if (Read(s, p, h, 512) < 512) break;

            bool zero = true;
            for (int i = 0; i < 512; i++) if (h[i] != 0) { zero = false; break; }
            if (zero)
            {
                // due blocchi nulli chiudono l'archivio; GNU tar riempie fino a 10240 byte
                long end = p + 1024;
                long padded = off + ((end - off + 10239) / 10240) * 10240;
                if (TarAllZero(s, end, padded - end)) end = padded;
                return end - off;
            }

            if (!(h[257] == 'u' && h[258] == 's' && h[259] == 't' && h[260] == 'a' && h[261] == 'r')) break;
            long size = TarOctal(h, 124, 12);
            if (size < 0) break;
            p += 512 + ((size + 511) / 512) * 512;
            if (p - off > Max) break;
        }
        long len = p - off;
        return len > 512 ? -len : 0;
    }

    private static bool TarAllZero(IBlockSource s, long from, long count)
    {
        if (count <= 0) return true;
        if (count > 10240) return false;
        var b = new byte[(int)count];
        if (Read(s, from, b, b.Length) < b.Length) return false;
        for (int i = 0; i < b.Length; i++) if (b[i] != 0) return false;
        return true;
    }

    // ============================== CAB ==============================

    public static bool VCab(byte[] b, int i, int n)
    {
        if (n < 36) return false;
        if (U32LE(b, i + 4) != 0 || U32LE(b, i + 12) != 0 || U32LE(b, i + 20) != 0) return false;
        uint size = U32LE(b, i + 8);
        if (size < 36 || size > (2u << 30)) return false;
        return b[i + 24] == 3 && b[i + 25] == 1;
    }

    public static long Cab(IBlockSource s, long off)
    {
        var h = new byte[12];
        if (Read(s, off, h, 12) < 12) return 0;
        long size = ByteUtils.U32LE(h, 8);
        return size >= 36 && size <= (2L << 30) ? size : 0;
    }

    // ============================== MPEG audio / MP3 ==============================

    private static readonly int[,] Bitrates =
    {
        // MPEG1 L1, L2, L3
        {0,32,64,96,128,160,192,224,256,288,320,352,384,416,448,-1},
        {0,32,48,56, 64, 80, 96,112,128,160,192,224,256,320,384,-1},
        {0,32,40,48, 56, 64, 80, 96,112,128,160,192,224,256,320,-1},
        // MPEG2/2.5 L1, L2, L3
        {0,32,48,56, 64, 80, 96,112,128,144,160,176,192,224,256,-1},
        {0, 8,16,24, 32, 40, 48, 56, 64, 80, 96,112,128,144,160,-1},
        {0, 8,16,24, 32, 40, 48, 56, 64, 80, 96,112,128,144,160,-1}
    };

    private static readonly int[] SampleRates = { 44100, 48000, 32000 };

    /// <summary>Lunghezza in byte di un frame MPEG audio, 0 se l'intestazione non è valida.</summary>
    private static int MpegFrameLength(byte[] b, int o, out int verId, out int layer, out int srIdx)
    {
        verId = layer = srIdx = -1;
        if (b[o] != 0xFF || (b[o + 1] & 0xE0) != 0xE0) return 0;

        verId = (b[o + 1] >> 3) & 3;                                 // 0=2.5, 1=riservato, 2=2, 3=1
        layer = (b[o + 1] >> 1) & 3;                                 // 1=L3, 2=L2, 3=L1
        if (verId == 1 || layer == 0) return 0;

        int brIdx = (b[o + 2] >> 4) & 0x0F;
        srIdx = (b[o + 2] >> 2) & 3;
        int pad = (b[o + 2] >> 1) & 1;
        if (brIdx == 0 || brIdx == 15 || srIdx == 3) return 0;

        int row = verId == 3 ? 3 - layer : 3 + (3 - layer);
        int kbps = Bitrates[row, brIdx];
        if (kbps <= 0) return 0;

        int sr = SampleRates[srIdx];
        if (verId == 2) sr /= 2;
        else if (verId == 0) sr /= 4;

        int bitrate = kbps * 1000;
        int len;
        if (layer == 3) len = (12 * bitrate / sr + pad) * 4;         // Layer I
        else if (verId == 3 || layer == 2) len = 144 * bitrate / sr + pad;
        else len = 72 * bitrate / sr + pad;                          // Layer III su MPEG2/2.5

        return len >= 24 && len <= 5760 ? len : 0;
    }

    public static bool VMpegAudio(byte[] b, int i, int n)
    {
        if (n < 8) return false;
        int len = MpegFrameLength(b, i, out int v1, out int l1, out int s1);
        if (len == 0) return false;

        // servono almeno tre frame coerenti di seguito: il sync da solo è troppo comune
        int p = i + len;
        for (int f = 0; f < 2; f++)
        {
            if (p + 4 > i + n) return f > 0;
            int l = MpegFrameLength(b, p, out int v2, out int l2, out int s2);
            if (l == 0 || v2 != v1 || l2 != l1 || s2 != s1) return false;
            p += l;
        }
        return true;
    }

    public static bool VId3(byte[] b, int i, int n)
    {
        if (n < 10) return false;
        if (b[i + 3] < 2 || b[i + 3] > 4 || b[i + 4] == 0xFF) return false;
        for (int k = 6; k < 10; k++) if ((b[i + k] & 0x80) != 0) return false;
        return true;
    }

    public static long Mp3WithId3(IBlockSource s, long off)
    {
        var h = new byte[10];
        if (Read(s, off, h, 10) < 10) return 0;
        long size = ((long)(h[6] & 0x7F) << 21) | ((long)(h[7] & 0x7F) << 14) |
                    ((long)(h[8] & 0x7F) << 7) | (long)(h[9] & 0x7F);
        long audio = off + 10 + size;
        if ((h[5] & 0x10) != 0) audio += 10;                         // footer ID3v2.4

        long frames = MpegAudio(s, audio);
        if (frames <= 0) return -(audio - off);
        return audio + frames - off;
    }

    /// <summary>Percorre i frame MPEG audio fino al primo sync non valido, poi i tag in coda.</summary>
    public static long MpegAudio(IBlockSource s, long off)
    {
        const long Max = 1L << 30;
        const int Window = 64 * 1024;
        var buf = new byte[Window];

        long p = off;
        int count = 0;
        bool stop = false;

        while (!stop && p - off < Max)
        {
            int got = Read(s, p, buf, Window);
            if (got < 4) break;

            int idx = 0;
            while (idx + 4 <= got)
            {
                int len = MpegFrameLength(buf, idx, out _, out _, out _);
                if (len == 0) { stop = true; break; }
                if (idx + len > got) break;                          // il frame sfora: ricarica il buffer
                idx += len;
                count++;
            }

            if (idx == 0)
            {
                if (stop) break;
                // frame più lungo dei byte disponibili: avanza con una lettura mirata
                var one = new byte[4];
                if (Read(s, p, one, 4) < 4) break;
                int len = MpegFrameLength(one, 0, out _, out _, out _);
                if (len == 0) break;
                p += len;
                count++;
                continue;
            }
            p += idx;
        }

        if (count < 3) return 0;

        long end = p;
        var tag = new byte[8];
        if (Read(s, end, tag, 8) >= 3 && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G') end += 128;
        else if (Read(s, end, tag, 8) >= 8 && Encoding.ASCII.GetString(tag) == "APETAGEX")
        {
            var ape = new byte[32];
            if (Read(s, end, ape, 32) >= 32) end += 32 + ByteUtils.U32LE(ape, 12);
        }
        return end - off;
    }

    // ============================== FLAC ==============================

    public static bool VFlac(byte[] b, int i, int n)
    {
        if (n < 8) return false;
        if ((b[i + 4] & 0x7F) != 0) return false;                    // il primo blocco è STREAMINFO
        return ((b[i + 5] << 16) | (b[i + 6] << 8) | b[i + 7]) == 34;
    }

    private static readonly byte[] Crc8Table = BuildCrc8();
    private static readonly ushort[] Crc16Table = BuildCrc16();

    private static byte[] BuildCrc8()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int k = 0; k < 8; k++) c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) & 0xFF : (c << 1) & 0xFF;
            t[i] = (byte)c;
        }
        return t;
    }

    private static ushort[] BuildCrc16()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) & 0xFFFF : (c << 1) & 0xFFFF;
            t[i] = (ushort)c;
        }
        return t;
    }

    private static byte Crc8(byte[] b, int o, int n)
    {
        byte c = 0;
        for (int i = 0; i < n; i++) c = Crc8Table[c ^ b[o + i]];
        return c;
    }

    /// <summary>Dimensione dell'intestazione di frame FLAC se valida (CRC-8 compreso), altrimenti 0.</summary>
    private static int FlacHeaderSize(byte[] b, int o, int avail)
    {
        if (avail < 6) return 0;
        if (b[o] != 0xFF || (b[o + 1] & 0xFC) != 0xF8) return 0;

        int bsCode = (b[o + 2] >> 4) & 0x0F;
        int srCode = b[o + 2] & 0x0F;
        int chCode = (b[o + 3] >> 4) & 0x0F;
        int bpsCode = (b[o + 3] >> 1) & 0x07;
        if (bsCode == 0 || srCode == 15 || chCode > 10 || bpsCode == 3 || bpsCode == 7) return 0;
        if ((b[o + 3] & 1) != 0) return 0;                           // bit riservato

        int p = o + 4;
        // numero di frame/campione in UTF-8 esteso
        int first = b[p];
        int extra = 0;
        if ((first & 0x80) == 0) extra = 0;
        else if ((first & 0xE0) == 0xC0) extra = 1;
        else if ((first & 0xF0) == 0xE0) extra = 2;
        else if ((first & 0xF8) == 0xF0) extra = 3;
        else if ((first & 0xFC) == 0xF8) extra = 4;
        else if ((first & 0xFE) == 0xFC) extra = 5;
        else if (first == 0xFE) extra = 6;
        else return 0;
        p += 1 + extra;

        if (bsCode == 6) p += 1; else if (bsCode == 7) p += 2;
        if (srCode == 12) p += 1; else if (srCode == 13 || srCode == 14) p += 2;

        int size = p - o + 1;
        if (o + size > o + avail) return 0;
        return Crc8(b, o, size - 1) == b[p] ? size : 0;
    }

    /// <summary>
    /// I frame FLAC non dichiarano la propria lunghezza: la fine si trova con il CRC-16
    /// che chiude ogni frame, confermata dall'intestazione valida del frame successivo.
    /// </summary>
    public static long Flac(IBlockSource s, long off)
    {
        const long Max = 2L << 30;
        var hb = new byte[4];
        long p = off + 4;

        for (int i = 0; i < 1024; i++)                               // blocchi di metadati
        {
            if (Read(s, p, hb, 4) < 4) return 0;
            bool last = (hb[0] & 0x80) != 0;
            long len = (hb[1] << 16) | (hb[2] << 8) | hb[3];
            p += 4 + len;
            if (p - off > Max) return 0;
            if (last) break;
        }

        long audioStart = p;
        const int Window = 1 << 20;
        var buf = new byte[Window];
        int frames = 0;

        while (p - off < Max)
        {
            int got = Read(s, p, buf, Window);
            if (got < 16) break;
            if (FlacHeaderSize(buf, 0, got) == 0) break;

            int crc = 0;
            int firstCrcOnly = -1;
            int endIdx = -1;

            for (int i = 0; i < got - 2; i++)
            {
                crc = ((crc << 8) ^ Crc16Table[((crc >> 8) ^ buf[i]) & 0xFF]) & 0xFFFF;
                if (i < 14) continue;

                int stored = (buf[i + 1] << 8) | buf[i + 2];
                if (stored != crc) continue;

                int cand = i + 3;                                     // byte successivi al CRC
                if (firstCrcOnly < 0) firstCrcOnly = cand;
                if (cand + 6 <= got && FlacHeaderSize(buf, cand, got - cand) != 0) { endIdx = cand; break; }
            }

            if (endIdx >= 0) { p += endIdx; frames++; continue; }

            // nessun frame valido dopo: siamo sull'ultimo, lo chiude il solo CRC-16
            if (firstCrcOnly >= 0)
            {
                p += firstCrcOnly;
                frames++;
            }
            break;
        }

        if (frames == 0) return audioStart > off ? -(audioStart - off) : 0;
        return p - off;
    }

    // ============================== Matroska ==============================

    private static long EbmlVint(IBlockSource s, ref long p, out bool unknown)
    {
        unknown = false;
        var b = new byte[8];
        if (Read(s, p, b, 1) < 1) return -1;
        int first = b[0];
        if (first == 0) return -1;

        int len = 1;
        int mask = 0x80;
        while ((first & mask) == 0) { mask >>= 1; len++; }
        if (len > 8) return -1;
        if (Read(s, p, b, len) < len) return -1;

        long v = b[0] & (mask - 1);
        long allOnes = mask - 1;
        for (int i = 1; i < len; i++) { v = (v << 8) | b[i]; allOnes = (allOnes << 8) | 0xFF; }
        p += len;
        if (v == allOnes) unknown = true;
        return v;
    }

    public static long Matroska(IBlockSource s, long off)
    {
        var b = new byte[4];
        long p = off;
        if (Read(s, p, b, 4) < 4) return 0;
        if (b[0] != 0x1A || b[1] != 0x45 || b[2] != 0xDF || b[3] != 0xA3) return 0;
        p += 4;

        long hdr = EbmlVint(s, ref p, out bool unk);
        if (hdr < 0 || unk || hdr > (1 << 20)) return 0;
        p += hdr;

        for (int i = 0; i < 8; i++)                                  // salta Void/CRC prima del Segment
        {
            if (Read(s, p, b, 4) < 4) return 0;
            if (b[0] == 0x18 && b[1] == 0x53 && b[2] == 0x80 && b[3] == 0x67)
            {
                p += 4;
                long seg = EbmlVint(s, ref p, out unk);
                if (seg < 0 || unk) return -(p - off);
                return p + seg - off;
            }
            if (b[0] == 0xEC || b[0] == 0xBF)                        // Void, CRC-32
            {
                long q = p + 1;
                long sz = EbmlVint(s, ref q, out unk);
                if (sz < 0 || unk) return 0;
                p = q + sz;
                continue;
            }
            return 0;
        }
        return 0;
    }

    public static string CMatroska(IBlockSource s, long off)
    {
        var b = new byte[64];
        if (Read(s, off, b, 64) < 32) return null;
        string t = Encoding.ASCII.GetString(b);
        if (t.Contains("webm")) return "webm";
        return "mkv";
    }

    // ============================== MPEG PS / TS ==============================

    public static bool VMpegPs(byte[] b, int i, int n)
    {
        if (n < 14) return false;
        int c = b[i + 4];
        return (c & 0xC0) == 0x40 || (c & 0xF0) == 0x20;
    }

    public static long MpegPs(IBlockSource s, long off)
    {
        const long Max = 16L << 30;
        var b = new byte[6];
        long p = off;
        int packs = 0;

        while (p - off < Max)
        {
            if (Read(s, p, b, 6) < 6) break;
            if (b[0] != 0 || b[1] != 0 || b[2] != 1) break;
            int sc = b[3];

            if (sc == 0xB9) return p + 4 - off;                       // fine programma
            if (sc == 0xBA)
            {
                if ((b[4] & 0xC0) == 0x40)
                {
                    var st = new byte[1];
                    if (Read(s, p + 13, st, 1) < 1) break;
                    p += 14 + (st[0] & 7);
                }
                else if ((b[4] & 0xF0) == 0x20) p += 12;
                else break;
                packs++;
                continue;
            }
            if (sc < 0xB9) break;

            int len = (b[4] << 8) | b[5];
            if (len == 0) break;
            p += 6 + len;
            packs++;
        }

        long l = p - off;
        if (l <= 0 || packs == 0) return 0;

        // ogni pacchetto dichiara la propria lunghezza: se la catena si chiude su un codice
        // non valido, la somma è la fine vera del flusso (molti muxer omettono il codice B9)
        return packs >= 8 ? l : -l;
    }

    public static bool VMpegTs(byte[] b, int i, int n)
    {
        if (n < 188 * 4 + 4) return false;
        for (int k = 0; k < 4; k++)
        {
            int o = i + 188 * k;
            if (b[o] != 0x47) return false;
            if ((b[o + 1] & 0x80) != 0) return false;                // transport error
            if ((b[o + 3] & 0x30) == 0) return false;                // adaptation field non valido
        }
        return true;
    }

    public static long MpegTs(IBlockSource s, long off)
    {
        const int Pk = 188;
        const long Max = 32L << 30;
        const int Batch = Pk * 512;
        var buf = new byte[Batch];
        long p = off;

        while (p - off < Max)
        {
            int got = Read(s, p, buf, Batch);
            if (got < Pk) break;

            int packets = got / Pk;
            int ok = 0;
            for (int i = 0; i < packets; i++)
            {
                int o = i * Pk;
                if (buf[o] != 0x47 || (buf[o + 1] & 0x80) != 0 || (buf[o + 3] & 0x30) == 0) break;
                ok++;
            }
            p += (long)ok * Pk;
            if (ok < packets) break;
        }

        long len = p - off;
        return len >= Pk * 16 ? len : 0;
    }

    // ============================== OGG ==============================

    public static bool VOgg(byte[] b, int i, int n) =>
        n >= 27 && b[i + 4] == 0 && (b[i + 5] & 0xF8) == 0;

    public static long Ogg(IBlockSource s, long off)
    {
        const long Max = 4L << 30;
        var h = new byte[27];
        var seg = new byte[255];
        long p = off;
        int pages = 0;

        while (p - off < Max)
        {
            if (Read(s, p, h, 27) < 27) break;
            if (h[0] != 'O' || h[1] != 'g' || h[2] != 'g' || h[3] != 'S' || h[4] != 0) break;

            int ns = h[26];
            if (ns > 0 && Read(s, p + 27, seg, ns) < ns) break;
            int data = 0;
            for (int i = 0; i < ns; i++) data += seg[i];

            p += 27 + ns + data;
            pages++;
            if (pages > 5_000_000) break;
        }
        return pages > 0 ? p - off : 0;
    }

    public static string COgg(IBlockSource s, long off)
    {
        var b = new byte[64];
        if (Read(s, off, b, 64) < 40) return null;
        string t = Encoding.ASCII.GetString(b, 27, 30);
        if (t.Contains("vorbis")) return "ogg";
        if (t.Contains("Opus")) return "opus";
        if (t.Contains("FLAC")) return "oga";
        if (t.Contains("theora")) return "ogv";
        if (t.Contains("Speex")) return "spx";
        return "ogg";
    }

    // ============================== SQLite ==============================

    public static long Sqlite(IBlockSource s, long off)
    {
        var h = new byte[100];
        if (Read(s, off, h, 100) < 100) return 0;

        long pageSize = ByteUtils.U16BE(h, 16);
        if (pageSize == 1) pageSize = 65536;
        if (!Pow2(pageSize) || pageSize < 512) return 0;
        if (h[18] > 2 || h[19] > 2 || h[18] == 0) return 0;          // versioni di lettura/scrittura

        long pages = ByteUtils.U32BE(h, 28);
        if (pages <= 0 || pages > (1L << 31)) return 0;

        // il conteggio pagine vale solo se il contatore di modifica coincide
        if (ByteUtils.U32BE(h, 24) != ByteUtils.U32BE(h, 92)) return -(pageSize * pages);
        return pageSize * pages;
    }

    // ============================== PE / ELF ==============================

    public static bool VPe(byte[] b, int i, int n)
    {
        if (n < 0x40) return false;
        uint lfanew = U32LE(b, i + 0x3C);
        if (lfanew < 0x40 || lfanew > 0x1000) return false;
        if (i + lfanew + 4 > i + n) return true;                     // controllo rimandato al misuratore
        return b[i + (int)lfanew] == 'P' && b[i + (int)lfanew + 1] == 'E' &&
               b[i + (int)lfanew + 2] == 0 && b[i + (int)lfanew + 3] == 0;
    }

    public static long Pe(IBlockSource s, long off)
    {
        var h = new byte[64];
        if (Read(s, off, h, 64) < 64) return 0;
        long pe = ByteUtils.U32LE(h, 0x3C);
        if (pe < 0x40 || pe > 0x1000) return 0;

        var c = new byte[24];
        if (Read(s, off + pe, c, 24) < 24) return 0;
        if (c[0] != 'P' || c[1] != 'E' || c[2] != 0 || c[3] != 0) return 0;

        int sections = ByteUtils.U16LE(c, 6);
        int optSize = ByteUtils.U16LE(c, 20);
        if (sections <= 0 || sections > 96 || optSize > 4096) return 0;

        long secTable = pe + 24 + optSize;
        long extent = secTable + 40L * sections;

        var opt = new byte[Math.Max(optSize, 2)];
        if (optSize >= 96 && Read(s, off + pe + 24, opt, optSize) >= 96)
        {
            int magic = ByteUtils.U16LE(opt, 0);
            int ddOff = magic == 0x20B ? 112 : 96;                    // PE32+ / PE32
            if (optSize >= ddOff + 8 * 5)
            {
                long certOff = ByteUtils.U32LE(opt, ddOff + 8 * 4);
                long certSize = ByteUtils.U32LE(opt, ddOff + 8 * 4 + 4);
                if (certOff > 0 && certSize > 0 && certOff + certSize < (1L << 30))
                    extent = Math.Max(extent, certOff + certSize);
            }
        }

        var sec = new byte[40];
        for (int i = 0; i < sections; i++)
        {
            if (Read(s, off + secTable + 40L * i, sec, 40) < 40) break;
            long raw = ByteUtils.U32LE(sec, 20);
            long size = ByteUtils.U32LE(sec, 16);
            if (raw == 0 || size == 0) continue;
            if (raw > (1L << 31) || size > (1L << 31)) continue;
            if (raw + size > extent) extent = raw + size;
        }

        return extent > 64 && extent < (1L << 31) ? extent : 0;
    }

    public static string CPe(IBlockSource s, long off)
    {
        var h = new byte[64];
        if (Read(s, off, h, 64) < 64) return null;
        long pe = ByteUtils.U32LE(h, 0x3C);
        var c = new byte[24];
        if (Read(s, off + pe, c, 24) < 24) return null;
        int chars = ByteUtils.U16LE(c, 22);
        return (chars & 0x2000) != 0 ? "dll" : "exe";
    }

    public static bool VElf(byte[] b, int i, int n) =>
        n >= 20 && (b[i + 4] == 1 || b[i + 4] == 2) && (b[i + 5] == 1 || b[i + 5] == 2) && b[i + 6] == 1;

    public static long Elf(IBlockSource s, long off)
    {
        var h = new byte[64];
        if (Read(s, off, h, 64) < 52) return 0;
        bool x64 = h[4] == 2;
        bool le = h[5] == 1;

        long U16(int o) => le ? ByteUtils.U16LE(h, o) : ByteUtils.U16BE(h, o);
        long U32(int o) => le ? ByteUtils.U32LE(h, o) : ByteUtils.U32BE(h, o);
        long U64(int o) => le ? (long)ByteUtils.U64LE(h, o) : (long)ByteUtils.U64BE(h, o);

        long phoff, shoff;
        int ehsize, phentsize, phnum, shentsize, shnum;
        if (x64)
        {
            phoff = U64(32); shoff = U64(40);
            ehsize = (int)U16(52); phentsize = (int)U16(54); phnum = (int)U16(56);
            shentsize = (int)U16(58); shnum = (int)U16(60);
        }
        else
        {
            phoff = U32(28); shoff = U32(32);
            ehsize = (int)U16(40); phentsize = (int)U16(42); phnum = (int)U16(44);
            shentsize = (int)U16(46); shnum = (int)U16(48);
        }

        if (phnum > 65535 || shnum > 65535 || phoff < 0 || shoff < 0) return 0;
        long extent = ehsize;
        extent = Math.Max(extent, phoff + (long)phnum * phentsize);
        extent = Math.Max(extent, shoff + (long)shnum * shentsize);

        var sh = new byte[64];
        for (int i = 0; i < shnum && i < 4096; i++)
        {
            if (Read(s, off + shoff + (long)i * shentsize, sh, Math.Min(shentsize, 64)) < 16) break;
            long type = le ? ByteUtils.U32LE(sh, 4) : ByteUtils.U32BE(sh, 4);
            if (type == 8) continue;                                 // SHT_NOBITS non occupa spazio
            long so = x64 ? (le ? (long)ByteUtils.U64LE(sh, 24) : (long)ByteUtils.U64BE(sh, 24))
                          : (le ? ByteUtils.U32LE(sh, 16) : ByteUtils.U32BE(sh, 16));
            long ss = x64 ? (le ? (long)ByteUtils.U64LE(sh, 32) : (long)ByteUtils.U64BE(sh, 32))
                          : (le ? ByteUtils.U32LE(sh, 20) : ByteUtils.U32BE(sh, 20));
            if (so < 0 || ss < 0 || so + ss > (1L << 32)) continue;
            if (so + ss > extent) extent = so + ss;
        }

        var ph = new byte[56];
        for (int i = 0; i < phnum && i < 4096; i++)
        {
            if (Read(s, off + phoff + (long)i * phentsize, ph, Math.Min(phentsize, 56)) < 32) break;
            long po = x64 ? (le ? (long)ByteUtils.U64LE(ph, 8) : (long)ByteUtils.U64BE(ph, 8))
                          : (le ? ByteUtils.U32LE(ph, 4) : ByteUtils.U32BE(ph, 4));
            long psz = x64 ? (le ? (long)ByteUtils.U64LE(ph, 32) : (long)ByteUtils.U64BE(ph, 32))
                           : (le ? ByteUtils.U32LE(ph, 16) : ByteUtils.U32BE(ph, 16));
            if (po < 0 || psz < 0 || po + psz > (1L << 32)) continue;
            if (po + psz > extent) extent = po + psz;
        }

        return extent > 52 && extent < (1L << 31) ? extent : 0;
    }

    // ============================== font sfnt ==============================

    public static bool VSfnt(byte[] b, int i, int n)
    {
        if (n < 12) return false;
        int num = U16BE(b, i + 4);
        if (num < 1 || num > 512) return false;
        int sr = U16BE(b, i + 6);
        int p2 = 16;
        while (p2 * 2 <= num * 16) p2 *= 2;
        return sr == p2;
    }

    public static long Sfnt(IBlockSource s, long off)
    {
        var h = new byte[12];
        if (Read(s, off, h, 12) < 12) return 0;
        int num = ByteUtils.U16BE(h, 4);
        if (num < 1 || num > 512) return 0;

        long extent = 12 + 16L * num;
        var rec = new byte[16];
        for (int i = 0; i < num; i++)
        {
            if (Read(s, off + 12 + 16L * i, rec, 16) < 16) return 0;
            for (int k = 0; k < 4; k++) if (!SfntTagChar(rec[k])) return 0;
            long to = ByteUtils.U32BE(rec, 8);
            long tl = ByteUtils.U32BE(rec, 12);
            if (to < 0 || tl < 0 || to + tl > (64L << 20)) return 0;
            if (to + tl > extent) extent = to + tl;
        }
        return (extent + 3) & ~3L;                                   // le tabelle sono allineate a 4
    }
}
