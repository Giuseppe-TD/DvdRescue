using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>
/// Lettore di immagini Nero NRG.
///
/// Il file è una sequenza di chunk (id di 4 byte + dimensione a 32 bit big endian) posta
/// in coda ai dati; dove comincia lo dice il footer:
///   v1: ultimi 8 byte  = "NERO" + offset a 32 bit
///   v2: ultimi 12 byte = "NER5" + offset a 64 bit
/// Chunk gestiti: CUES/CUEX (posizioni delle tracce), DAOI/DAOX (disc at once),
/// ETNF/ETN2 (track at once), SINF (tracce per sessione), MTYP (tipo di supporto), END!.
/// </summary>
public static class NrgImage
{
    private sealed class NrgTrack
    {
        public int Number;
        public int Session = 1;
        public long FileOffset;      // offset dei dati (index 1) nel file NRG
        public long PregapOffset = -1;
        public long EndOffset;
        public int SectorSize = 2048;
        public int Mode;
        public long StartLba = -1;
        public long Sectors;
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
            if (fileLength < 32) return false;

            int version = DiscImage.NrgVersion(path);
            if (version == 0) return false;

            var tail = DiscImage.ReadTail(path, 12);
            long chunkStart;
            if (version == 2)
            {
                if (tail.Length < 12) return false;
                ulong v = ByteUtils.U64BE(tail, 4);
                if (v > long.MaxValue) return false;
                chunkStart = (long)v;
            }
            else
            {
                int o = tail.Length - 8;
                if (o < 0) return false;
                chunkStart = ByteUtils.U32BE(tail, o + 4);
            }

            if (chunkStart <= 0 || chunkStart >= fileLength) return false;

            result = new DiscImage
            {
                FormatName = version == 2 ? "Nero NRG (v2)" : "Nero NRG (v1)",
                Path = path
            };

            var tracks = new List<NrgTrack>();
            var cueLba = new Dictionary<int, long>();   // numero traccia → LBA di INDEX 1
            var sessionTrackCounts = new List<int>();
            bool sawDao = false, sawEtn = false;
            var ignored = new List<string>();

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long position = chunkStart;
                int guard = 0;
                while (position + 8 <= fileLength && guard++ < 4096)
                {
                    var header = new byte[8];
                    fs.Position = position;
                    if (!ReadExact(fs, header, 8)) break;

                    string id = ByteUtils.Ascii(header, 0, 4);
                    long size = ByteUtils.U32BE(header, 4);
                    long payloadAt = position + 8;
                    if (size < 0 || payloadAt + size > fileLength)
                    {
                        result.Note($"Chunk \"{id}\" con dimensione dichiarata {size} oltre la fine del file: " +
                                    "lettura interrotta qui.");
                        break;
                    }

                    if (id == "END!") break;

                    var payload = new byte[size];
                    if (size > 0 && !ReadExact(fs, payload, (int)size))
                    {
                        result.Note($"Chunk \"{id}\" troncato: lettura interrotta.");
                        break;
                    }

                    switch (id)
                    {
                        case "CUES":
                            ParseCue(payload, false, cueLba);
                            break;
                        case "CUEX":
                            ParseCue(payload, true, cueLba);
                            break;
                        case "DAOI":
                            sawDao = true;
                            ParseDao(result, payload, false, tracks, fileLength);
                            break;
                        case "DAOX":
                            sawDao = true;
                            ParseDao(result, payload, true, tracks, fileLength);
                            break;
                        case "ETNF":
                            sawEtn = true;
                            ParseEtn(payload, false, tracks);
                            break;
                        case "ETN2":
                            sawEtn = true;
                            ParseEtn(payload, true, tracks);
                            break;
                        case "SINF":
                            if (payload.Length >= 4) sessionTrackCounts.Add((int)ByteUtils.U32BE(payload, 0));
                            break;
                        case "MTYP":
                            if (payload.Length >= 4) result.Note($"Tipo di supporto dichiarato (MTYP): 0x{ByteUtils.U32BE(payload, 0):X}.");
                            break;
                        default:
                            if (!ignored.Contains(id)) ignored.Add(id);
                            break;
                    }

                    position = payloadAt + size;
                }
            }

            if (tracks.Count == 0)
            {
                result.Dispose();
                return false;
            }

            if (ignored.Count > 0)
                result.Note("Chunk presenti ma non utilizzati: " + string.Join(", ", ignored) + ".");
            if (sawDao && sawEtn)
                result.Note("L'immagine contiene sia chunk DAO sia chunk ETN: sono stati usati entrambi.");

            // sessioni: SINF dice quante tracce ci sono in ciascuna
            if (sessionTrackCounts.Count > 0)
            {
                int t = 0;
                for (int s = 0; s < sessionTrackCounts.Count; s++)
                {
                    for (int k = 0; k < sessionTrackCounts[s] && t < tracks.Count; k++, t++)
                        tracks[t].Session = s + 1;
                }
                result.Note($"Sessioni dichiarate (SINF): {sessionTrackCounts.Count} " +
                            $"({string.Join("+", sessionTrackCounts)} tracce).");
            }

            file = new FileBlockSource(path, 2048);
            result.Own(file);

            long runningLba = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                t.Number = i + 1;

                if (t.Sectors <= 0 && t.EndOffset > t.FileOffset && t.SectorSize > 0)
                    t.Sectors = (t.EndOffset - t.FileOffset) / t.SectorSize;
                if (t.Sectors <= 0)
                {
                    result.Note($"Traccia {t.Number}: lunghezza non determinabile, saltata.");
                    continue;
                }

                if (t.StartLba < 0 && cueLba.TryGetValue(t.Number, out long lba)) t.StartLba = lba;
                if (t.StartLba < 0) t.StartLba = runningLba;
                runningLba = t.StartLba + t.Sectors;

                if (t.FileOffset < 0 || t.FileOffset + t.Sectors * (long)t.SectorSize > fileLength)
                {
                    long max = (fileLength - t.FileOffset) / Math.Max(1, t.SectorSize);
                    if (max <= 0)
                    {
                        result.Note($"Traccia {t.Number}: i dati cadono fuori dal file, saltata.");
                        continue;
                    }
                    result.Note($"Traccia {t.Number}: dichiarati {t.Sectors} settori ma il file ne contiene {max}, " +
                                "la traccia è stata accorciata.");
                    t.Sectors = max;
                }

                bool audio = t.Mode == 0x07 || t.Mode == 0x10;
                var track = new ImageTrack
                {
                    Number = t.Number,
                    Session = t.Session,
                    IsAudio = audio,
                    StartLba = t.StartLba,
                    Sectors = t.Sectors,
                    SectorSize = t.SectorSize
                };
                ApplyGeometry(track, audio);
                result.Tracks.Add(track);
                result.Attach(track, file, t.FileOffset);
            }

            if (result.Tracks.Count == 0) { result.Dispose(); return false; }

            result.Note($"NRG letto: {result.Tracks.Count} tracce, chunk iniziale a offset {chunkStart}.");
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

    /// <summary>Dalla dimensione del settore ricava dove stanno i dati utente.</summary>
    private static void ApplyGeometry(ImageTrack track, bool audio)
    {
        if (audio)
        {
            track.DataOffset = 0;
            track.UserDataSize = 2352;
            return;
        }
        switch (track.SectorSize)
        {
            case 2048: track.DataOffset = 0; track.UserDataSize = 2048; break;
            case 2336: track.DataOffset = 8; track.UserDataSize = 2048; break;
            case 2324: track.DataOffset = 0; track.UserDataSize = 2048; break;
            case 2352:
            case 2448: track.DataOffset = -1; track.UserDataSize = 2048; break;
            default: track.DataOffset = 0; track.UserDataSize = Math.Min(2048, track.SectorSize); break;
        }
    }

    /// <summary>
    /// CUES/CUEX: voci da 8 byte (adr/ctl, traccia BCD, indice BCD, 0, posizione).
    /// In CUEX la posizione è un LBA a 32 bit big endian con segno, in CUES è MSF.
    /// </summary>
    private static void ParseCue(byte[] payload, bool extended, Dictionary<int, long> cueLba)
    {
        for (int o = 0; o + 8 <= payload.Length; o += 8)
        {
            int trackBcd = payload[o + 1];
            int indexBcd = payload[o + 2];
            if (trackBcd == 0 || trackBcd == 0xAA) continue;    // lead-in / lead-out
            int number = FromBcd(trackBcd);
            int index = FromBcd(indexBcd);
            if (number <= 0 || index != 1) continue;            // ci serve solo INDEX 01

            long lba;
            if (extended) lba = unchecked((int)ByteUtils.U32BE(payload, o + 4));
            else
            {
                int m = payload[o + 5], s = payload[o + 6], f = payload[o + 7];
                lba = (m * 60L + s) * 75L + f - 150;
            }
            if (!cueLba.ContainsKey(number)) cueLba[number] = lba;
        }
    }

    /// <summary>
    /// DAOI/DAOX: intestazione (dimensione, UPC, tipo TOC, prima e ultima traccia) seguita da una
    /// voce per traccia, 30 byte in v1 (offset a 32 bit) e 42 in v2 (offset a 64 bit).
    /// L'intestazione è di 22 o 23 byte a seconda di chi ha scritto il file: quella giusta è
    /// quella per cui le voci tornano (dimensione del settore nota e offset crescenti).
    /// </summary>
    private static void ParseDao(DiscImage image, byte[] payload, bool wide, List<NrgTrack> tracks, long fileLength)
    {
        int entrySize = wide ? 42 : 30;
        int headerSize = ChooseDaoHeaderSize(payload, entrySize, wide, fileLength);
        if (headerSize < 0)
        {
            image.Note("Blocco DAO con struttura non riconosciuta: ignorato.");
            return;
        }

        string upc = ByteUtils.Ascii(payload, 4, 13);
        int firstTrack = payload[headerSize - 2];
        int lastTrack = payload[headerSize - 1];
        if (!string.IsNullOrEmpty(upc)) image.Note($"UPC/MCN dichiarato nel DAO: {upc}.");

        int count = (payload.Length - headerSize) / entrySize;
        if (count <= 0) return;

        int expected = lastTrack - firstTrack + 1;
        if (expected > 0 && expected != count)
            image.Note($"Il blocco DAO dichiara le tracce da {firstTrack} a {lastTrack} ma contiene {count} voci: " +
                       "sono state usate le voci effettivamente presenti.");

        for (int i = 0; i < count; i++)
        {
            int o = headerSize + i * entrySize;
            var t = new NrgTrack
            {
                SectorSize = ByteUtils.U16BE(payload, o + 12),
                Mode = payload[o + 14]
            };

            if (wide)
            {
                t.PregapOffset = (long)ByteUtils.U64BE(payload, o + 18);
                t.FileOffset = (long)ByteUtils.U64BE(payload, o + 26);
                t.EndOffset = (long)ByteUtils.U64BE(payload, o + 34);
            }
            else
            {
                t.PregapOffset = ByteUtils.U32BE(payload, o + 18);
                t.FileOffset = ByteUtils.U32BE(payload, o + 22);
                t.EndOffset = ByteUtils.U32BE(payload, o + 26);
            }

            if (t.SectorSize <= 0) t.SectorSize = SectorSizeFromMode(t.Mode);
            tracks.Add(t);
        }
    }

    /// <summary>Dimensioni di settore che un masterizzatore può davvero scrivere.</summary>
    private static bool IsKnownSectorSize(int size)
        => size == 2048 || size == 2052 || size == 2056 || size == 2324 || size == 2332 ||
           size == 2336 || size == 2340 || size == 2352 || size == 2368 || size == 2448 ||
           size == 2452 || size == 2646 || size == 2688;

    /// <summary>
    /// Prova le due lunghezze note dell'intestazione DAO e tiene quella che rende coerenti
    /// tutte le voci. -1 se nessuna funziona.
    /// </summary>
    private static int ChooseDaoHeaderSize(byte[] payload, int entrySize, bool wide, long fileLength)
    {
        foreach (int headerSize in new[] { 22, 23 })
        {
            if (payload.Length <= headerSize) continue;
            int remainder = payload.Length - headerSize;
            if (remainder % entrySize != 0) continue;

            int count = remainder / entrySize;
            bool ok = true;
            long previousEnd = -1;

            for (int i = 0; i < count && ok; i++)
            {
                int o = headerSize + i * entrySize;
                int sectorSize = ByteUtils.U16BE(payload, o + 12);
                if (!IsKnownSectorSize(sectorSize)) { ok = false; break; }

                long start, end;
                if (wide)
                {
                    start = (long)ByteUtils.U64BE(payload, o + 26);
                    end = (long)ByteUtils.U64BE(payload, o + 34);
                }
                else
                {
                    start = ByteUtils.U32BE(payload, o + 22);
                    end = ByteUtils.U32BE(payload, o + 26);
                }

                if (start < 0 || end <= start || end > fileLength) { ok = false; break; }
                if (previousEnd >= 0 && start < previousEnd) { ok = false; break; }
                if ((end - start) % sectorSize != 0) { ok = false; break; }
                previousEnd = end;
            }

            if (ok) return headerSize;
        }
        return -1;
    }

    /// <summary>
    /// ETNF/ETN2 (track at once): offset e dimensione in byte, modalità e LBA iniziale.
    /// Voci da 20 byte in v1, 32 in v2.
    /// </summary>
    private static void ParseEtn(byte[] payload, bool wide, List<NrgTrack> tracks)
    {
        int entrySize = wide ? 32 : 20;
        int count = payload.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            int o = i * entrySize;
            var t = new NrgTrack();
            long size;
            if (wide)
            {
                t.FileOffset = (long)ByteUtils.U64BE(payload, o);
                size = (long)ByteUtils.U64BE(payload, o + 8);
                t.Mode = (int)ByteUtils.U32BE(payload, o + 16);
                t.StartLba = ByteUtils.U32BE(payload, o + 20);
            }
            else
            {
                t.FileOffset = ByteUtils.U32BE(payload, o);
                size = ByteUtils.U32BE(payload, o + 4);
                t.Mode = (int)ByteUtils.U32BE(payload, o + 8);
                t.StartLba = ByteUtils.U32BE(payload, o + 12);
            }

            t.SectorSize = SectorSizeFromMode(t.Mode);
            t.EndOffset = t.FileOffset + size;
            if (t.SectorSize > 0) t.Sectors = size / t.SectorSize;
            tracks.Add(t);
        }
    }

    /// <summary>Codici di modalità Nero → dimensione del settore.</summary>
    private static int SectorSizeFromMode(int mode) => mode switch
    {
        0x00 => 2048,   // MODE1
        0x01 => 2336,   // MODE2 form1 senza sync
        0x02 => 2048,   // MODE2 form1
        0x03 => 2336,   // MODE2 misto
        0x05 => 2352,   // MODE1 grezzo
        0x06 => 2352,   // MODE2 grezzo
        0x07 => 2352,   // audio
        0x0F => 2448,   // MODE1 grezzo + subchannel
        0x10 => 2448,   // audio + subchannel
        0x11 => 2448,   // MODE2 grezzo + subchannel
        _ => 2048
    };

    private static int FromBcd(int value)
    {
        int high = (value >> 4) & 0x0F, low = value & 0x0F;
        if (high > 9 || low > 9) return value;    // alcuni masterizzatori scrivono già in binario
        return high * 10 + low;
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = stream.Read(buffer, got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}
