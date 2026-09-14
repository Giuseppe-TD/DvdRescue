using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>
/// Lettore di immagini DiscJuggler (CDI).
///
/// Il formato non è documentato ufficialmente: la struttura qui implementata è quella
/// ricostruita dagli strumenti liberi (cdirip) ed è nota per funzionare sulle versioni
/// 2.x, 3.x e 3.5 di DiscJuggler.
///
/// I dati stanno all'inizio del file, il descrittore in fondo. Gli ultimi 8 byte danno
/// la versione (0x80000004 = 2.x, 0x80000005 = 3.x, 0x80000006 = 3.5) e la posizione del
/// descrittore: offset assoluto nelle versioni 2.x/3.x, distanza dalla fine del file nella 3.5.
/// Il descrittore elenca le sessioni e, per ciascuna, le tracce con pregap, lunghezza,
/// modalità, LBA e codice della dimensione del settore.
///
/// Le varianti che non rispettano questa struttura vengono rifiutate: meglio dirlo che
/// restituire settori a caso.
/// </summary>
public static class CdiImage
{
    private const uint VersionV2 = 0x80000004;
    private const uint VersionV3 = 0x80000005;
    private const uint VersionV35 = 0x80000006;

    /// <summary>Codici della dimensione del settore usati nel descrittore.</summary>
    private static readonly int[] SectorSizes = { 2048, 2336, 2352 };

    /// <summary>Vero se il footer ha l'aspetto di un CDI (controllo rapido, senza parsing).</summary>
    public static bool LooksLikeCdi(string path)
    {
        try
        {
            var tail = DiscImage.ReadTail(path, 8);
            if (tail.Length < 8) return false;
            uint version = ByteUtils.U32LE(tail, 0);
            uint offset = ByteUtils.U32LE(tail, 4);
            if (version != VersionV2 && version != VersionV3 && version != VersionV35) return false;
            if (offset == 0) return false;

            long length = new FileInfo(path).Length;
            long start = version == VersionV35 ? length - offset : offset;
            return start > 0 && start < length - 8;
        }
        catch { return false; }
    }

    public static bool TryOpen(string path, out DiscImage image)
    {
        image = null;
        DiscImage result = null;
        FileBlockSource file = null;
        try
        {
            if (!File.Exists(path)) return false;
            long fileLength = new FileInfo(path).Length;
            if (fileLength < 16) return false;

            var tail = DiscImage.ReadTail(path, 8);
            if (tail.Length < 8) return false;
            uint version = ByteUtils.U32LE(tail, 0);
            uint rawOffset = ByteUtils.U32LE(tail, 4);
            if (version != VersionV2 && version != VersionV3 && version != VersionV35) return false;
            if (rawOffset == 0) return false;

            long descriptorStart = version == VersionV35 ? fileLength - rawOffset : rawOffset;
            if (descriptorStart <= 0 || descriptorStart >= fileLength - 8) return false;

            long descriptorLength = fileLength - descriptorStart - 8;
            if (descriptorLength <= 4 || descriptorLength > 64 * 1024 * 1024) return false;

            var descriptor = new byte[descriptorLength];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = descriptorStart;
                int got = 0;
                while (got < descriptor.Length)
                {
                    int n = fs.Read(descriptor, got, descriptor.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got != descriptor.Length) return false;
            }

            var cursor = new Cursor(descriptor);
            int sessionCount = cursor.U16();
            if (sessionCount <= 0 || sessionCount > 99) return false;

            var parsed = new List<CdiTrack>();
            long dataPosition = 0;               // offset progressivo nei dati, dall'inizio del file
            long dataArea = descriptorStart;     // i dati occupano tutto ciò che precede il descrittore

            for (int s = 0; s < sessionCount; s++)
            {
                int trackCount = cursor.U16();
                if (trackCount < 0 || trackCount > 99) return false;

                for (int t = 0; t < trackCount; t++)
                {
                    var track = ReadTrack(cursor, version);
                    if (track == null) return false;

                    track.Session = s + 1;
                    track.Number = parsed.Count + 1;
                    track.FileOffset = dataPosition + track.Pregap * (long)track.SectorSize;
                    dataPosition += track.TotalLength * (long)track.SectorSize;
                    parsed.Add(track);
                }

                // coda di sessione: dimensione variabile a seconda della versione
                if (!SkipSessionTrailer(cursor, version)) return false;
            }

            if (parsed.Count == 0) return false;

            // verifica: i dati dichiarati devono stare nell'area dati, altrimenti la
            // struttura letta non è quella giusta e va rifiutata
            if (dataPosition <= 0 || dataPosition > dataArea) return false;

            result = new DiscImage { FormatName = "DiscJuggler CDI", Path = path };
            result.Note($"Descrittore CDI versione {VersionName(version)} a offset {descriptorStart} " +
                        $"({descriptorLength} byte), {sessionCount} sessioni, {parsed.Count} tracce.");
            if (dataPosition < dataArea)
                result.Note($"Area dati di {dataArea} byte, tracce dichiarate per {dataPosition} byte: " +
                            $"{dataArea - dataPosition} byte in coda non appartengono a nessuna traccia.");

            file = new FileBlockSource(path, 2048);
            result.Own(file);

            foreach (var t in parsed)
            {
                long sectors = t.Length;
                if (sectors <= 0) { result.Note($"Traccia {t.Number}: lunghezza nulla, saltata."); continue; }

                long available = (dataArea - t.FileOffset) / t.SectorSize;
                if (available <= 0) { result.Note($"Traccia {t.Number}: fuori dall'area dati, saltata."); continue; }
                if (sectors > available)
                {
                    result.Note($"Traccia {t.Number}: dichiarati {sectors} settori ma ne stanno {available}, accorciata.");
                    sectors = available;
                }

                bool audio = t.Mode == 0;
                var track = new ImageTrack
                {
                    Number = t.Number,
                    Session = t.Session,
                    IsAudio = audio,
                    StartLba = t.StartLba,
                    Sectors = sectors,
                    SectorSize = t.SectorSize
                };

                if (audio) { track.DataOffset = 0; track.UserDataSize = 2352; }
                else if (t.SectorSize == 2048) { track.DataOffset = 0; track.UserDataSize = 2048; }
                else if (t.SectorSize == 2336) { track.DataOffset = 8; track.UserDataSize = 2048; }
                else { track.DataOffset = -1; track.UserDataSize = 2048; }

                if (t.Pregap > 0)
                    result.Note($"Traccia {t.Number}: {t.Pregap} settori di pregap presenti nel file, esclusi dai dati.");

                result.Tracks.Add(track);
                result.Attach(track, file, t.FileOffset);
            }

            if (result.Tracks.Count == 0) { result.Dispose(); return false; }

            image = result;
            return true;
        }
        catch
        {
            result?.Dispose();
            file?.Dispose();
            return false;
        }
    }

    private sealed class CdiTrack
    {
        public int Number;
        public int Session;
        public long Pregap;
        public long Length;
        public long TotalLength;
        public long StartLba;
        public int Mode;
        public int SectorSize = 2352;
        public long FileOffset;
    }

    /// <summary>
    /// Legge una voce di traccia. Le sequenze di byte saltati sono campi non identificati:
    /// le lunghezze vengono dalla struttura ricostruita da cdirip.
    /// </summary>
    private static CdiTrack ReadTrack(Cursor c, uint version)
    {
        try
        {
            // le versioni dalla 3.00.780 in poi antepongono 8 byte; le riconosci perché
            // la voce comincia sempre con 00 00 01 00
            var marker = c.Peek(4);
            if (marker == null) return null;
            if (!(marker[0] == 0x00 && marker[1] == 0x00 && marker[2] == 0x01 && marker[3] == 0x00))
            {
                if (!c.Skip(8)) return null;
                marker = c.Peek(4);
                if (marker == null) return null;
                if (!(marker[0] == 0x00 && marker[1] == 0x00 && marker[2] == 0x01 && marker[3] == 0x00))
                    return null;   // struttura non riconosciuta: meglio rinunciare
            }

            if (!c.Skip(24)) return null;              // marcatore + campi non identificati
            int nameLength = c.U8();
            if (nameLength < 0) return null;
            if (!c.Skip(nameLength)) return null;      // nome della traccia

            if (!c.Skip(19)) return null;
            uint mark = c.U32();
            if (mark == 0x80000000) { if (!c.Skip(8)) return null; }   // DiscJuggler 4

            if (!c.Skip(16)) return null;
            var track = new CdiTrack();
            track.Pregap = c.U32();
            track.Length = c.U32();
            if (!c.Skip(6)) return null;
            track.Mode = (int)c.U32();
            if (!c.Skip(12)) return null;
            track.StartLba = c.U32();
            track.TotalLength = c.U32();
            if (!c.Skip(16)) return null;
            uint sizeCode = c.U32();

            if (c.Failed) return null;
            if (sizeCode >= (uint)SectorSizes.Length) return null;
            track.SectorSize = SectorSizes[sizeCode];

            if (track.Mode < 0 || track.Mode > 2) return null;
            if (track.Pregap < 0 || track.Length < 0 || track.TotalLength < 0) return null;
            if (track.TotalLength < track.Pregap + track.Length) return null;

            if (!c.Skip(29)) return null;
            if (version != VersionV2)
            {
                if (!c.Skip(5)) return null;
                uint extra = c.U32();
                if (extra == 0xFFFFFFFF && !c.Skip(78)) return null;
            }

            return c.Failed ? null : track;
        }
        catch { return null; }
    }

    /// <summary>Coda della sessione, dopo l'ultima traccia.</summary>
    private static bool SkipSessionTrailer(Cursor c, uint version)
    {
        if (!c.Skip(4)) return false;
        int flag = c.U8();
        if (flag < 0) return false;
        if (flag == 1 && !c.Skip(8)) return false;
        if (version >= VersionV3 && !c.Skip(1)) return false;
        return !c.Failed;
    }

    private static string VersionName(uint version) => version switch
    {
        VersionV2 => "2.x",
        VersionV3 => "3.x",
        VersionV35 => "3.5",
        _ => "sconosciuta"
    };

    /// <summary>Lettore sequenziale su buffer che non lancia: segnala l'errore e basta.</summary>
    private sealed class Cursor
    {
        private readonly byte[] _data;
        private int _position;

        public bool Failed { get; private set; }

        public Cursor(byte[] data) { _data = data; }

        public bool Skip(int count)
        {
            if (count < 0 || _position + count > _data.Length) { Failed = true; return false; }
            _position += count;
            return true;
        }

        public byte[] Peek(int count)
        {
            if (count < 0 || _position + count > _data.Length) { Failed = true; return null; }
            var buffer = new byte[count];
            Array.Copy(_data, _position, buffer, 0, count);
            return buffer;
        }

        public int U8()
        {
            if (_position + 1 > _data.Length) { Failed = true; return -1; }
            return _data[_position++];
        }

        public int U16()
        {
            if (_position + 2 > _data.Length) { Failed = true; return -1; }
            int v = ByteUtils.U16LE(_data, _position);
            _position += 2;
            return v;
        }

        public uint U32()
        {
            if (_position + 4 > _data.Length) { Failed = true; return 0; }
            uint v = ByteUtils.U32LE(_data, _position);
            _position += 4;
            return v;
        }
    }
}
