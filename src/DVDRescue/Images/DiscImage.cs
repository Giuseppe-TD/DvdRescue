using System.Text;
using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>Una traccia dentro un'immagine disco.</summary>
public sealed class ImageTrack
{
    /// <summary>Numero di traccia (1-based).</summary>
    public int Number;

    /// <summary>Sessione di appartenenza (1-based).</summary>
    public int Session = 1;

    /// <summary>True per le tracce audio (niente filesystem da leggere).</summary>
    public bool IsAudio;

    /// <summary>LBA logico della traccia sul disco.</summary>
    public long StartLba;

    /// <summary>Numero di settori della traccia.</summary>
    public long Sectors;

    /// <summary>Dimensione del settore nell'immagine: 2048, 2352, 2336, 2448...</summary>
    public int SectorSize = 2048;

    /// <summary>Offset del campo dati utente dentro il settore (-1 = da riconoscere dal sync).</summary>
    public int DataOffset;

    /// <summary>Byte di dati utente per settore: 2048 sui dati, 2352 sull'audio.</summary>
    public int UserDataSize = 2048;

    /// <summary>Titolo, se il formato lo fornisce.</summary>
    public string Title;

    public override string ToString()
        => $"Traccia {Number} ({(IsAudio ? "audio" : "dati")}) LBA {StartLba}, {Sectors} settori da {SectorSize}";
}

/// <summary>
/// Immagine disco aperta: elenco delle tracce e accesso ai dati utente.
/// Le sorgenti restituite da <see cref="GetTrackSource"/> espongono blocchi da 2048 byte
/// di solo user data, quindi i lettori di filesystem non devono sapere nulla del formato.
/// </summary>
public sealed class DiscImage : IDisposable
{
    /// <summary>Nome del formato riconosciuto: "RAW/ISO", "BIN/CUE", "Nero NRG"...</summary>
    public string FormatName = "Sconosciuto";

    /// <summary>Percorso del file principale dell'immagine.</summary>
    public string Path;

    public List<ImageTrack> Tracks = new();

    /// <summary>Diagnostica per l'utente: cosa è stato trovato, ignorato o non è supportato.</summary>
    public List<string> Notes = new();

    // dove stanno fisicamente i dati di ogni traccia
    private readonly Dictionary<ImageTrack, TrackBacking> _backing = new();
    private readonly List<IDisposable> _owned = new();
    private bool _disposed;

    internal sealed class TrackBacking
    {
        /// <summary>Sorgente grezza (il file immagine, o un decompressore).</summary>
        public IBlockSource Source;

        /// <summary>Offset in byte del primo settore della traccia dentro la sorgente.</summary>
        public long ByteOffset;

        /// <summary>True se la sorgente espone già solo dati utente (niente da sbucciare).</summary>
        public bool AlreadyUserData;
    }

    internal void Attach(ImageTrack track, IBlockSource source, long byteOffset, bool alreadyUserData = false)
        => _backing[track] = new TrackBacking { Source = source, ByteOffset = byteOffset, AlreadyUserData = alreadyUserData };

    /// <summary>Registra una risorsa da chiudere insieme all'immagine.</summary>
    internal void Own(IDisposable resource)
    {
        if (resource != null && !_owned.Contains(resource)) _owned.Add(resource);
    }

    internal void Note(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Notes.Add(text);
    }

    /// <summary>
    /// Sorgente dei dati utente di una traccia: blocchi da 2048 byte (2352 per l'audio,
    /// dove tutto il settore è dato utile).
    /// La sorgente è una vista: chiuderla non chiude l'immagine.
    /// </summary>
    public IBlockSource GetTrackSource(ImageTrack track)
    {
        if (track == null) throw new ArgumentNullException(nameof(track));
        if (!_backing.TryGetValue(track, out var backing) || backing.Source == null)
            throw new InvalidOperationException($"La traccia {track.Number} non ha dati associati.");

        string name = $"{System.IO.Path.GetFileName(Path ?? backing.Source.Name)} T{track.Number}";

        // sorgente già "pulita" (DAA e simili): basta una finestra
        if (backing.AlreadyUserData)
            return new SubRangeSource(backing.Source, backing.ByteOffset,
                                      track.Sectors * track.UserDataSize, track.UserDataSize, name);

        // settori già da 2048 senza intestazioni: finestra diretta, più veloce
        if (track.SectorSize == track.UserDataSize && track.DataOffset == 0)
            return new SubRangeSource(backing.Source, backing.ByteOffset,
                                      track.Sectors * (long)track.SectorSize, track.SectorSize, name);

        return new RawSectorSource(backing.Source, backing.ByteOffset, track.Sectors,
                                   track.SectorSize, track.DataOffset, track.UserDataSize, name);
    }

    /// <summary>Prima traccia dati dell'immagine (il caso comune: un solo filesystem).</summary>
    public IBlockSource GetDataSource()
    {
        var track = FirstDataTrack();
        if (track == null) throw new InvalidOperationException("L'immagine non contiene tracce dati.");
        return GetTrackSource(track);
    }

    /// <summary>Prima traccia non audio, null se il disco è di solo audio.</summary>
    public ImageTrack FirstDataTrack()
    {
        foreach (var t in Tracks)
            if (!t.IsAudio && t.Sectors > 0) return t;
        return null;
    }

    // ---------------------------------------------------------------- apertura

    /// <summary>
    /// Apre un'immagine riconoscendone il formato: prima dall'estensione, poi dal contenuto,
    /// con ripiego su RAW. Lancia solo se il file non esiste o nessun formato è leggibile.
    /// </summary>
    public static DiscImage Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Percorso vuoto.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("File immagine non trovato.", path);

        // un .mdf/.img/.bin da solo: prova prima il file descrittore che lo accompagna
        string companion = FindCompanion(path);
        if (companion != null)
        {
            var byCompanion = TryFormats(companion, ExtensionOrder(companion));
            if (byCompanion != null) return byCompanion;
        }

        var result = TryFormats(path, ExtensionOrder(path));
        if (result != null) return result;

        // un DAA è compresso: leggerlo come settori grezzi darebbe solo spazzatura
        if (DaaImage.LooksLikeDaa(path))
        {
            DaaImage.TryOpen(path, out _, out string reason);
            throw new InvalidDataException("Immagine PowerISO DAA non leggibile: " + (reason ??
                "formato non riconosciuto. Sono supportati solo i DAA non cifrati e non divisi in più parti."));
        }

        // un descrittore è solo testo o metadati: non ha senso leggerlo come settori
        string descriptorExt = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
        if (descriptorExt is ".cue" or ".ccd" or ".mds" or ".toc" or ".sub")
            throw new InvalidDataException(
                $"\"{System.IO.Path.GetFileName(path)}\" è un file descrittore ma i dati che indica " +
                "non sono utilizzabili: il file immagine che lo accompagna manca, è vuoto o non è leggibile.");

        // ultimo ripiego: settori grezzi
        var raw = OpenRaw(path);
        if (raw != null)
        {
            if (CdiImage.LooksLikeCdi(path))
                raw.Note("Il footer indica un'immagine DiscJuggler CDI, ma il descrittore non è di una " +
                         "variante riconosciuta: il file viene letto come settori grezzi e le tracce " +
                         "oltre la prima non sono individuabili.");
            else if (NrgVersion(path) > 0)
                raw.Note("Il footer indica un'immagine Nero NRG, ma i chunk non sono leggibili: " +
                         "il file viene letto come settori grezzi.");
            return raw;
        }

        throw new InvalidDataException($"Formato immagine non riconosciuto: {System.IO.Path.GetFileName(path)}");
    }

    /// <summary>Nome del formato senza aprire davvero l'immagine; null se non riconosciuto.</summary>
    public static string Identify(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".cue": return LooksLikeCue(path) ? "BIN/CUE" : null;
                case ".nrg": return NrgVersion(path) > 0 ? "Nero NRG" : null;
                case ".mds": return HasMagic(path, 0, "MEDIA DESCRIPTOR") ? "Alcohol MDS/MDF" : null;
                case ".mdf":
                    return ResolveCaseInsensitive(System.IO.Path.ChangeExtension(path, ".mds")) != null
                        ? "Alcohol MDS/MDF" : RawName(path);
                case ".ccd": return LooksLikeCcd(path) ? "CloneCD CCD/IMG" : null;
                case ".sub": return null;
                case ".cdi": return CdiImage.LooksLikeCdi(path) ? "DiscJuggler CDI" : null;
                case ".daa": return DaaImage.LooksLikeDaa(path) ? "PowerISO DAA" : null;
            }

            // riconoscimento dal contenuto
            if (HasMagic(path, 0, "MEDIA DESCRIPTOR")) return "Alcohol MDS/MDF";
            if (DaaImage.LooksLikeDaa(path)) return "PowerISO DAA";
            if (NrgVersion(path) > 0) return "Nero NRG";
            if (LooksLikeCcd(path)) return "CloneCD CCD/IMG";
            if (LooksLikeCue(path)) return "BIN/CUE";
            if (CdiImage.LooksLikeCdi(path)) return "DiscJuggler CDI";

            // il companion decide: un .img con il suo .ccd, un .bin con il suo .cue
            string companion = FindCompanion(path);
            if (companion != null) return Identify(companion);

            return RawName(path);
        }
        catch { return null; }
    }

    /// <summary>
    /// Un file grezzo è riconosciuto tale se contiene almeno un settore: la coda che avanza
    /// viene ignorata, come fa <see cref="OpenRaw"/>. Così Identify e Open non si contraddicono.
    /// </summary>
    private static string RawName(string path)
    {
        try
        {
            long len = new FileInfo(path).Length;
            return len >= 2048 ? "RAW/ISO" : null;
        }
        catch { return null; }
    }

    private delegate bool TryOpenDelegate(string path, out DiscImage image);

    private static DiscImage TryFormats(string path, IEnumerable<TryOpenDelegate> order)
    {
        foreach (var attempt in order)
        {
            DiscImage image = null;
            try
            {
                if (attempt(path, out image) && image != null && image.Tracks.Count > 0) return image;
            }
            catch
            {
                // un formato che esplode non deve impedire di provare gli altri
            }
            image?.Dispose();
        }
        return null;
    }

    /// <summary>Ordine dei tentativi: prima quello suggerito dall'estensione, poi gli altri.</summary>
    private static List<TryOpenDelegate> ExtensionOrder(string path)
    {
        var all = new List<TryOpenDelegate>
        {
            CueSheet.TryOpen, NrgImage.TryOpen, MdsImage.TryOpen,
            CcdImage.TryOpen, CdiImage.TryOpen, DaaImage.TryOpen
        };

        TryOpenDelegate preferred = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant() switch
        {
            ".cue" => CueSheet.TryOpen,
            ".nrg" => NrgImage.TryOpen,
            ".mds" => MdsImage.TryOpen,
            ".ccd" => CcdImage.TryOpen,
            ".cdi" => CdiImage.TryOpen,
            ".daa" => DaaImage.TryOpen,
            _ => null
        };

        if (preferred != null)
        {
            all.Remove(preferred);
            all.Insert(0, preferred);
        }
        return all;
    }

    /// <summary>
    /// Per .bin/.img/.mdf cerca il file descrittore che gli sta accanto (.cue, .ccd, .mds).
    /// </summary>
    private static string FindCompanion(string path)
    {
        try
        {
            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            string[] candidates = ext switch
            {
                ".bin" => new[] { ".cue", ".ccd" },
                ".img" => new[] { ".ccd", ".cue" },
                ".mdf" => new[] { ".mds" },
                ".sub" => new[] { ".ccd", ".cue" },
                _ => null
            };
            if (candidates == null) return null;

            foreach (string c in candidates)
            {
                string sibling = ResolveCaseInsensitive(System.IO.Path.ChangeExtension(path, c));
                if (sibling != null && File.Exists(sibling)) return sibling;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Su filesystem case sensitive i file di appoggio spesso hanno l'estensione in un altro
    /// case rispetto a quello scritto nel descrittore: cerchiamo anche così.
    /// </summary>
    internal static string ResolveCaseInsensitive(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) return path;

            string dir = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            if (!Directory.Exists(dir)) return null;

            string target = System.IO.Path.GetFileName(path);
            foreach (string candidate in Directory.EnumerateFiles(dir))
                if (string.Equals(System.IO.Path.GetFileName(candidate), target, StringComparison.OrdinalIgnoreCase))
                    return candidate;
        }
        catch { }
        return null;
    }

    // ---------------------------------------------------------------- RAW/ISO

    /// <summary>
    /// Apre un file grezzo. Se la dimensione è multipla di 2352/2448 e i settori hanno il sync,
    /// lo tratta come raw; altrimenti come settori da 2048.
    /// </summary>
    public static DiscImage OpenRaw(string path)
    {
        FileBlockSource file = null;
        try
        {
            long length = new FileInfo(path).Length;
            if (length <= 0) return null;

            // il sync comanda: se c'è, i settori sono grezzi anche quando il file ha una coda
            // che non appartiene ai dati (descrittori in fondo, dump troncati)
            int rawSize = RawSectorSource.ProbeRawSectorSize(path);
            if (rawSize == 2048 && length % 2048 != 0)
            {
                if (length % 2352 == 0) rawSize = 2352;
                else if (length % 2336 == 0) rawSize = 2336;
            }

            long sectors = length / rawSize;
            if (sectors <= 0) return null;   // troppo corto per contenere anche un solo settore

            var image = new DiscImage { FormatName = "RAW/ISO", Path = path };
            file = new FileBlockSource(path, rawSize);
            image.Own(file);

            var track = new ImageTrack
            {
                Number = 1,
                Session = 1,
                IsAudio = false,
                StartLba = 0,
                Sectors = sectors,
                SectorSize = rawSize,
                DataOffset = rawSize == 2048 ? 0 : -1,
                UserDataSize = 2048
            };
            image.Tracks.Add(track);
            image.Attach(track, file, 0);

            if (rawSize == 2048)
                image.Note($"File grezzo con settori da 2048 byte: {sectors} settori.");
            else
                image.Note($"File grezzo con settori da {rawSize} byte (sync riconosciuto): {sectors} settori, " +
                           "dati utente estratti in base alla modalità di ogni settore.");

            if (length % rawSize != 0)
                image.Note($"Il file non è multiplo di {rawSize}: gli ultimi {length % rawSize} byte sono stati ignorati.");

            return image;
        }
        catch
        {
            file?.Dispose();
            return null;
        }
    }

    // ---------------------------------------------------------------- utilità comuni ai formati

    /// <summary>Legge i primi byte di un file, quanti ce ne sono.</summary>
    internal static byte[] ReadHead(string path, int count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(count, fs.Length)];
            int got = 0;
            while (got < buffer.Length)
            {
                int n = fs.Read(buffer, got, buffer.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < buffer.Length) Array.Resize(ref buffer, got);
            return buffer;
        }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>Legge gli ultimi byte di un file.</summary>
    internal static byte[] ReadTail(string path, int count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int want = (int)Math.Min(count, fs.Length);
            if (want <= 0) return Array.Empty<byte>();
            var buffer = new byte[want];
            fs.Position = fs.Length - want;
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(buffer, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            return buffer;
        }
        catch { return Array.Empty<byte>(); }
    }

    private static bool HasMagic(string path, int offset, string magic)
    {
        var head = ReadHead(path, offset + magic.Length);
        if (head.Length < offset + magic.Length) return false;
        for (int i = 0; i < magic.Length; i++)
            if (head[offset + i] != (byte)magic[i]) return false;
        return true;
    }

    /// <summary>2 per NER5, 1 per NERO, 0 se non è un NRG.</summary>
    internal static int NrgVersion(string path)
    {
        var tail = ReadTail(path, 12);
        if (tail.Length >= 12 && tail[0] == 'N' && tail[1] == 'E' && tail[2] == 'R' && tail[3] == '5') return 2;
        if (tail.Length >= 8)
        {
            int o = tail.Length - 8;
            if (tail[o] == 'N' && tail[o + 1] == 'E' && tail[o + 2] == 'R' && tail[o + 3] == 'O') return 1;
        }
        return 0;
    }

    private static bool LooksLikeCue(string path)
    {
        try
        {
            var head = ReadHead(path, 4096);
            if (head.Length == 0) return false;
            string text = Encoding.Latin1.GetString(head).ToUpperInvariant();
            return text.Contains("TRACK ") && (text.Contains("FILE ") || text.Contains("INDEX "));
        }
        catch { return false; }
    }

    private static bool LooksLikeCcd(string path)
    {
        try
        {
            var head = ReadHead(path, 1024);
            if (head.Length == 0) return false;
            string text = Encoding.Latin1.GetString(head).ToUpperInvariant();
            return text.Contains("[CLONECD]") || (text.Contains("[DISC]") && text.Contains("TOCENTRIES"));
        }
        catch { return false; }
    }

    /// <summary>Dimensione totale dei dati utente di tutte le tracce dati.</summary>
    public long TotalDataSectors
    {
        get
        {
            long total = 0;
            foreach (var t in Tracks) if (!t.IsAudio) total += t.Sectors;
            return total;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var resource in _owned)
        {
            try { resource.Dispose(); } catch { }
        }
        _owned.Clear();
        _backing.Clear();
    }
}
