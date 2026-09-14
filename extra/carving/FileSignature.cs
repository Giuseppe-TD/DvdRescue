using System.Text;
using DVDRescue.Core;

namespace DVDRescue.Carving;

/// <summary>
/// Descrizione di un tipo di file per il carving per firma.
/// Header e footer sono sequenze di byte; <see cref="MeasureLength"/> è la via maestra
/// quando la struttura del formato permette di calcolare la lunghezza esatta.
/// </summary>
public sealed class FileSignature
{
    /// <summary>Nome del tipo mostrato all'utente ("JPEG", "PDF", "ZIP/Office"...).</summary>
    public string Name;

    /// <summary>Estensione predefinita, senza punto.</summary>
    public string Extension;

    /// <summary>Firma iniziale. Deve essere presente.</summary>
    public byte[] Header;

    /// <summary>
    /// Maschera opzionale della stessa lunghezza di <see cref="Header"/>: 0xFF = byte da
    /// confrontare, 0x00 = byte variabile. I byte variabili in testa vengono saltati
    /// e la ricerca parte dal primo byte fisso.
    /// </summary>
    public byte[] HeaderMask;

    /// <summary>Footer opzionale, usato quando non c'è modo di calcolare la lunghezza.</summary>
    public byte[] Footer;

    /// <summary>Tetto di sicurezza: oltre questa lunghezza il file viene troncato.</summary>
    public long MaxLength = 64L << 20;

    /// <summary>
    /// Calcolo della lunghezza dalla struttura del file. Ritorna:
    /// &gt; 0 lunghezza esatta, &lt; 0 stima (valore assoluto), 0 nessuna idea.
    /// </summary>
    public Func<IBlockSource, long, long> MeasureLength;

    /// <summary>Allineamento del punto di partenza: 512 / 2048 / 1 a seconda del tipo.</summary>
    public int Alignment = 512;

    // ---- estensioni oltre il contratto minimo ----

    /// <summary>Quanti byte prima della firma comincia il file (4 per i box ftyp, 257 per il TAR).</summary>
    public int HeaderOffset;

    /// <summary>
    /// Controllo rapido sul buffer di scansione: (buffer, indice del primo byte del file,
    /// byte disponibili da quell'indice). Serve a scartare subito i falsi positivi.
    /// </summary>
    public Func<byte[], int, int, bool> Validate;

    /// <summary>Affina l'estensione leggendo il file (brand ftyp, primo nome dentro lo ZIP...).</summary>
    public Func<IBlockSource, long, string> ClassifyExtension;

    /// <summary>A parità di offset vince la firma con priorità più alta (CR2 batte TIFF).</summary>
    public int Priority;

    /// <summary>Soglia minima propria del tipo; se 0 vale quella del motore.</summary>
    public long MinLength;

    /// <summary>Per i formati testuali: assorbe il fine riga dopo il footer.</summary>
    public bool AbsorbTrailingEol;

    /// <summary>Descrizione breve in italiano.</summary>
    public string Description;

    public override string ToString() => Name + " (." + Extension + ")";
}

/// <summary>Catalogo delle firme riconosciute.</summary>
public static class SignatureLibrary
{
    private static readonly List<FileSignature> _all = Build();

    /// <summary>Elenco completo. La lista è condivisa: copiarla prima di modificarla.</summary>
    public static List<FileSignature> All => _all;

    /// <summary>Sottoinsieme per nome (confronto senza distinzione di maiuscole).</summary>
    public static List<FileSignature> ByNames(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return _all.FindAll(s => set.Contains(s.Name));
    }

    // --- scorciatoie per scrivere il catalogo in modo leggibile ---

    private static byte[] H(string hex)
    {
        var list = new List<byte>(hex.Length / 2);
        for (int i = 0; i + 1 < hex.Length; i += 2)
            list.Add((byte)((Hex(hex[i]) << 4) | Hex(hex[i + 1])));
        return list.ToArray();
    }

    private static int Hex(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;

    private static byte[] A(string ascii) => Encoding.ASCII.GetBytes(ascii);

    private static List<FileSignature> Build()
    {
        var l = new List<FileSignature>();

        // =========================== IMMAGINI ===========================

        l.Add(new FileSignature
        {
            Name = "JPEG", Extension = "jpg", Description = "Immagine JPEG/JFIF/Exif",
            Header = H("FFD8FF"), Alignment = 1, MaxLength = 128L << 20,
            Validate = Sigs.VJpeg, MeasureLength = Sigs.Jpeg, MinLength = 256
        });

        l.Add(new FileSignature
        {
            Name = "PNG", Extension = "png", Description = "Immagine PNG",
            Header = H("89504E470D0A1A0A"), Alignment = 1, MaxLength = 512L << 20,
            MeasureLength = Sigs.Png, MinLength = 67
        });

        l.Add(new FileSignature
        {
            Name = "GIF", Extension = "gif", Description = "Immagine GIF87a/89a",
            Header = A("GIF8"), Alignment = 1, MaxLength = 128L << 20,
            Validate = Sigs.VGif, MeasureLength = Sigs.Gif, MinLength = 64
        });

        l.Add(new FileSignature
        {
            Name = "BMP", Extension = "bmp", Description = "Bitmap Windows",
            Header = A("BM"), MaxLength = 512L << 20,
            Validate = Sigs.VBmp, MeasureLength = Sigs.Bmp, MinLength = 128
        });

        l.Add(new FileSignature
        {
            Name = "CR2", Extension = "cr2", Description = "RAW Canon (TIFF)",
            Header = H("49492A00100000004352"), Priority = 20, MaxLength = 512L << 20,
            MeasureLength = Sigs.Tiff
        });

        l.Add(new FileSignature
        {
            Name = "TIFF", Extension = "tif", Description = "TIFF e RAW derivati (NEF/ARW/DNG/ORF)",
            Header = H("49492A00"), MaxLength = 1L << 30,
            Validate = Sigs.VTiffLe, MeasureLength = Sigs.Tiff, ClassifyExtension = Sigs.CTiff
        });

        l.Add(new FileSignature
        {
            Name = "TIFF-BE", Extension = "tif", Description = "TIFF big endian (Motorola)",
            Header = H("4D4D002A"), MaxLength = 1L << 30,
            MeasureLength = Sigs.Tiff, ClassifyExtension = Sigs.CTiff
        });

        l.Add(new FileSignature
        {
            Name = "WebP", Extension = "webp", Description = "Immagine WebP (RIFF)",
            Header = A("RIFF\0\0\0\0WEBP"), HeaderMask = H("FFFFFFFF00000000FFFFFFFF"),
            MaxLength = 256L << 20, MeasureLength = Sigs.Riff, MinLength = 32
        });

        l.Add(new FileSignature
        {
            Name = "HEIF/MP4", Extension = "mp4", Description = "Contenitore ISO BMFF (heic, mp4, mov, 3gp, m4a)",
            Header = A("ftyp"), HeaderOffset = 4, MaxLength = 8L << 30,
            Validate = Sigs.VFtyp, MeasureLength = Sigs.IsoBmff, ClassifyExtension = Sigs.CFtyp,
            MinLength = 32
        });

        l.Add(new FileSignature
        {
            Name = "PSD", Extension = "psd", Description = "Adobe Photoshop",
            Header = H("3842505300010000"), MaxLength = 2L << 30, MeasureLength = Sigs.Psd
        });

        // =========================== DOCUMENTI ===========================

        l.Add(new FileSignature
        {
            Name = "PDF", Extension = "pdf", Description = "Documento PDF",
            Header = A("%PDF-"), MaxLength = 512L << 20, Alignment = 1,
            Validate = Sigs.VPdf, MeasureLength = Sigs.Pdf, MinLength = 256
        });

        l.Add(new FileSignature
        {
            Name = "RTF", Extension = "rtf", Description = "Rich Text Format",
            Header = A("{\\rtf"), MaxLength = 128L << 20, MeasureLength = Sigs.Rtf, MinLength = 64
        });

        l.Add(new FileSignature
        {
            Name = "OLE2", Extension = "doc", Description = "Compound File (doc/xls/ppt/msi)",
            Header = H("D0CF11E0A1B11AE1"), MaxLength = 1L << 30,
            MeasureLength = Sigs.Ole2, ClassifyExtension = Sigs.COle2, MinLength = 1024
        });

        l.Add(new FileSignature
        {
            Name = "ZIP/Office", Extension = "zip", Description = "ZIP e derivati (docx/xlsx/pptx/odt/epub/jar)",
            Header = H("504B0304"), MaxLength = 4L << 30,
            MeasureLength = Sigs.Zip, ClassifyExtension = Sigs.CZip, MinLength = 30
        });

        l.Add(new FileSignature
        {
            Name = "EML", Extension = "eml", Description = "Messaggio di posta RFC822",
            Header = A("Return-Path: "), MaxLength = 64L << 20, Validate = Sigs.VText, MinLength = 128
        });

        l.Add(new FileSignature
        {
            Name = "EML-Received", Extension = "eml", Description = "Messaggio di posta (prima riga Received)",
            Header = A("Received: from "), MaxLength = 64L << 20, Validate = Sigs.VText, MinLength = 128
        });

        l.Add(new FileSignature
        {
            Name = "ICS", Extension = "ics", Description = "Calendario iCalendar",
            Header = A("BEGIN:VCALENDAR"), Footer = A("END:VCALENDAR"), AbsorbTrailingEol = true,
            MaxLength = 16L << 20, MinLength = 64
        });

        l.Add(new FileSignature
        {
            Name = "VCF", Extension = "vcf", Description = "Rubrica vCard",
            Header = A("BEGIN:VCARD"), MaxLength = 16L << 20,
            MeasureLength = Sigs.VCard, MinLength = 48
        });

        l.Add(new FileSignature
        {
            Name = "XML", Extension = "xml", Description = "Documento XML",
            Header = A("<?xml"), MaxLength = 64L << 20, Validate = Sigs.VXml,
            MeasureLength = Sigs.Xml, AbsorbTrailingEol = true, MinLength = 64
        });

        l.Add(new FileSignature
        {
            Name = "HTML", Extension = "html", Description = "Pagina HTML",
            Header = A("<!DOCTYPE html"), Footer = A("</html>"), AbsorbTrailingEol = true,
            MaxLength = 32L << 20, MinLength = 64
        });

        l.Add(new FileSignature
        {
            Name = "JSON", Extension = "json", Description = "Documento JSON (oggetto radice)",
            Header = A("{"), MaxLength = 256L << 20, Validate = Sigs.VJson,
            MeasureLength = Sigs.Json, MinLength = 512
        });

        l.Add(new FileSignature
        {
            Name = "PST", Extension = "pst", Description = "Archivio Outlook",
            Header = H("2142444E"), MaxLength = 64L << 30,
            Validate = Sigs.VPst, MeasureLength = Sigs.Pst, MinLength = 512
        });

        l.Add(new FileSignature
        {
            Name = "REGF", Extension = "dat", Description = "Registro di Windows",
            Header = A("regf"), MaxLength = 2L << 30,
            Validate = Sigs.VRegf, MeasureLength = Sigs.Regf, MinLength = 4096
        });

        // =========================== ARCHIVI ===========================

        l.Add(new FileSignature
        {
            Name = "RAR5", Extension = "rar", Description = "Archivio RAR 5",
            Header = H("526172211A070100"), Priority = 10, MaxLength = 8L << 30,
            MeasureLength = Sigs.Rar5, MinLength = 20
        });

        l.Add(new FileSignature
        {
            Name = "RAR4", Extension = "rar", Description = "Archivio RAR 4",
            Header = H("526172211A0700"), MaxLength = 8L << 30,
            MeasureLength = Sigs.Rar4, MinLength = 20
        });

        l.Add(new FileSignature
        {
            Name = "7z", Extension = "7z", Description = "Archivio 7-Zip",
            Header = H("377ABCAF271C"), MaxLength = 8L << 30,
            MeasureLength = Sigs.SevenZip, MinLength = 32
        });

        l.Add(new FileSignature
        {
            Name = "GZIP", Extension = "gz", Description = "Compresso gzip",
            Header = H("1F8B08"), MaxLength = 4L << 30,
            Validate = Sigs.VGzip, MeasureLength = Sigs.Gzip, MinLength = 20
        });

        l.Add(new FileSignature
        {
            Name = "BZIP2", Extension = "bz2", Description = "Compresso bzip2",
            Header = A("BZh"), MaxLength = 4L << 30,
            Validate = Sigs.VBzip2, MeasureLength = Sigs.Bzip2, MinLength = 14
        });

        l.Add(new FileSignature
        {
            Name = "XZ", Extension = "xz", Description = "Compresso xz",
            Header = H("FD377A585A00"), MaxLength = 8L << 30,
            MeasureLength = Sigs.Xz, MinLength = 32
        });

        l.Add(new FileSignature
        {
            Name = "TAR", Extension = "tar", Description = "Archivio tar (ustar)",
            Header = A("ustar"), HeaderOffset = 257, MaxLength = 8L << 30,
            Validate = Sigs.VTar, MeasureLength = Sigs.Tar, MinLength = 1024
        });

        l.Add(new FileSignature
        {
            Name = "CAB", Extension = "cab", Description = "Cabinet Microsoft",
            Header = A("MSCF"), MaxLength = 2L << 30,
            Validate = Sigs.VCab, MeasureLength = Sigs.Cab, MinLength = 36
        });

        // =========================== AUDIO / VIDEO ===========================

        l.Add(new FileSignature
        {
            Name = "MP3-ID3", Extension = "mp3", Description = "MP3 con tag ID3v2",
            Header = A("ID3"), Priority = 10, MaxLength = 1L << 30,
            Validate = Sigs.VId3, MeasureLength = Sigs.Mp3WithId3, MinLength = 512
        });

        l.Add(new FileSignature
        {
            Name = "MPEG-Audio", Extension = "mp3", Description = "Flusso MPEG audio senza tag",
            Header = H("FFE0"), HeaderMask = H("FFE0"), MaxLength = 1L << 30,
            Validate = Sigs.VMpegAudio, MeasureLength = Sigs.MpegAudio, MinLength = 4096
        });

        l.Add(new FileSignature
        {
            Name = "FLAC", Extension = "flac", Description = "Audio FLAC",
            Header = A("fLaC"), MaxLength = 2L << 30,
            Validate = Sigs.VFlac, MeasureLength = Sigs.Flac, MinLength = 64
        });

        l.Add(new FileSignature
        {
            Name = "WAV", Extension = "wav", Description = "Audio WAV (RIFF)",
            Header = A("RIFF\0\0\0\0WAVE"), HeaderMask = H("FFFFFFFF00000000FFFFFFFF"),
            MaxLength = 4L << 30, MeasureLength = Sigs.Riff, MinLength = 44
        });

        l.Add(new FileSignature
        {
            Name = "AVI", Extension = "avi", Description = "Video AVI (RIFF)",
            Header = A("RIFF\0\0\0\0AVI "), HeaderMask = H("FFFFFFFF00000000FFFFFFFF"),
            MaxLength = 8L << 30, MeasureLength = Sigs.Riff, MinLength = 1024
        });

        l.Add(new FileSignature
        {
            Name = "Matroska", Extension = "mkv", Description = "Matroska / WebM (EBML)",
            Header = H("1A45DFA3"), MaxLength = 16L << 30,
            MeasureLength = Sigs.Matroska, ClassifyExtension = Sigs.CMatroska, MinLength = 64
        });

        l.Add(new FileSignature
        {
            Name = "MPEG-PS", Extension = "mpg", Description = "MPEG Program Stream",
            Header = H("000001BA"), MaxLength = 16L << 30,
            Validate = Sigs.VMpegPs, MeasureLength = Sigs.MpegPs, MinLength = 4096
        });

        l.Add(new FileSignature
        {
            Name = "MPEG-TS", Extension = "ts", Description = "MPEG Transport Stream (pacchetti da 188)",
            Header = H("47"), MaxLength = 32L << 30,
            Validate = Sigs.VMpegTs, MeasureLength = Sigs.MpegTs, MinLength = 188 * 16
        });

        l.Add(new FileSignature
        {
            Name = "OGG", Extension = "ogg", Description = "Contenitore Ogg (Vorbis/Opus/FLAC/Theora)",
            Header = A("OggS"), MaxLength = 4L << 30,
            Validate = Sigs.VOgg, MeasureLength = Sigs.Ogg, ClassifyExtension = Sigs.COgg, MinLength = 64
        });

        // =========================== ALTRO ===========================

        l.Add(new FileSignature
        {
            Name = "SQLite", Extension = "db", Description = "Database SQLite 3",
            Header = A("SQLite format 3\0"), MaxLength = 16L << 30,
            MeasureLength = Sigs.Sqlite, MinLength = 512
        });

        l.Add(new FileSignature
        {
            Name = "PE", Extension = "exe", Description = "Eseguibile Windows EXE/DLL",
            Header = A("MZ"), MaxLength = 1L << 30,
            Validate = Sigs.VPe, MeasureLength = Sigs.Pe, ClassifyExtension = Sigs.CPe, MinLength = 512
        });

        l.Add(new FileSignature
        {
            Name = "ELF", Extension = "elf", Description = "Eseguibile ELF",
            Header = H("7F454C46"), MaxLength = 1L << 30,
            Validate = Sigs.VElf, MeasureLength = Sigs.Elf, MinLength = 128
        });

        l.Add(new FileSignature
        {
            Name = "TrueType", Extension = "ttf", Description = "Font TrueType",
            Header = H("0001000000"), MaxLength = 64L << 20,
            Validate = Sigs.VSfnt, MeasureLength = Sigs.Sfnt, MinLength = 256
        });

        l.Add(new FileSignature
        {
            Name = "OpenType", Extension = "otf", Description = "Font OpenType (CFF)",
            Header = A("OTTO"), MaxLength = 64L << 20,
            Validate = Sigs.VSfnt, MeasureLength = Sigs.Sfnt, MinLength = 256
        });

        return l;
    }
}
