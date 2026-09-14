using System.Text;
using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>
/// Lettore di cue sheet (BIN/CUE): FILE, TRACK, INDEX, PREGAP, POSTGAP.
/// Le posizioni sono in MM:SS:FF con 75 frame al secondo; gli INDEX sono relativi
/// all'inizio del file BIN che li contiene, gli LBA del disco si ottengono sommando
/// i file nell'ordine in cui compaiono più i gap dichiarati con PREGAP/POSTGAP.
/// </summary>
public static class CueSheet
{
    private sealed class CueTrack
    {
        public int Number;
        public string Mode = "MODE1/2352";
        public int SectorSize = 2352;
        public int DataOffset = -1;
        public int UserDataSize = 2048;
        public bool IsAudio;
        public string Title;
        public int FileIndex = -1;
        public long Index0 = -1;      // pregap presente nel file
        public long Index1 = -1;      // inizio dei dati
        public long Pregap;           // gap NON presente nel file
        public long Postgap;
    }

    private sealed class CueFile
    {
        public string Declared;
        public string Resolved;
        public string Kind = "BINARY";
        public long Length;
    }

    /// <summary>Apre un .cue (o un .bin/.img accompagnato da un .cue).</summary>
    public static bool TryOpen(string path, out DiscImage image)
    {
        image = null;
        DiscImage result = null;
        try
        {
            string cuePath = path;
            if (!string.Equals(Path.GetExtension(path), ".cue", StringComparison.OrdinalIgnoreCase))
            {
                string sibling = DiscImage.ResolveCaseInsensitive(Path.ChangeExtension(path, ".cue"));
                if (sibling == null || !File.Exists(sibling)) return false;
                cuePath = sibling;
            }
            if (!File.Exists(cuePath)) return false;

            string text = ReadTextTolerant(cuePath);
            if (string.IsNullOrWhiteSpace(text)) return false;

            var files = new List<CueFile>();
            var tracks = new List<CueTrack>();
            var notes = new List<string>();
            string discTitle = null;
            string catalog = null;
            CueTrack current = null;
            int currentFile = -1;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0) continue;

                var parts = Tokenize(line);
                if (parts.Count == 0) continue;
                string cmd = parts[0].ToUpperInvariant();

                switch (cmd)
                {
                    case "REM":
                        break;

                    case "CATALOG":
                        if (parts.Count > 1) catalog = parts[1];
                        break;

                    case "TITLE":
                        if (parts.Count > 1)
                        {
                            if (current == null) discTitle = parts[1];
                            else current.Title = parts[1];
                        }
                        break;

                    case "FILE":
                        {
                            if (parts.Count < 2) break;
                            var file = new CueFile
                            {
                                Declared = parts[1],
                                Kind = parts.Count > 2 ? parts[2].ToUpperInvariant() : "BINARY"
                            };
                            file.Resolved = ResolveFile(cuePath, file.Declared);
                            if (file.Resolved != null)
                            {
                                try { file.Length = new FileInfo(file.Resolved).Length; } catch { file.Length = 0; }
                            }
                            files.Add(file);
                            currentFile = files.Count - 1;
                            current = null;
                            break;
                        }

                    case "TRACK":
                        {
                            if (parts.Count < 3) break;
                            if (!int.TryParse(parts[1], out int number)) break;
                            current = new CueTrack { Number = number, FileIndex = currentFile };
                            ApplyMode(current, parts[2]);
                            tracks.Add(current);
                            break;
                        }

                    case "INDEX":
                        {
                            if (current == null || parts.Count < 3) break;
                            if (!int.TryParse(parts[1], out int idx)) break;
                            long pos = ParseMsf(parts[2]);
                            if (pos < 0) break;
                            if (idx == 0) current.Index0 = pos;
                            else if (idx == 1) current.Index1 = pos;
                            break;
                        }

                    case "PREGAP":
                        if (current != null && parts.Count > 1)
                        {
                            long g = ParseMsf(parts[1]);
                            if (g > 0) current.Pregap = g;
                        }
                        break;

                    case "POSTGAP":
                        if (current != null && parts.Count > 1)
                        {
                            long g = ParseMsf(parts[1]);
                            if (g > 0) current.Postgap = g;
                        }
                        break;

                    case "FLAGS":
                    case "ISRC":
                    case "PERFORMER":
                    case "SONGWRITER":
                    case "CDTEXTFILE":
                        break;

                    default:
                        notes.Add($"Comando cue non gestito, ignorato: {cmd}");
                        break;
                }
            }

            if (tracks.Count == 0 || files.Count == 0) return false;

            result = new DiscImage { FormatName = "BIN/CUE", Path = cuePath };
            foreach (string n in notes) result.Note(n);
            if (!string.IsNullOrEmpty(discTitle)) result.Note($"Titolo del disco: {discTitle}");
            if (!string.IsNullOrEmpty(catalog)) result.Note($"Codice catalogo (MCN): {catalog}");

            // sorgenti: una per file BIN, condivisa fra le tracce che ci stanno dentro
            var sources = new Dictionary<int, IBlockSource>();
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (f.Resolved == null || f.Length <= 0)
                {
                    result.Note($"File dichiarato nel cue ma non trovato o vuoto: \"{f.Declared}\". " +
                                "Le tracce che lo usano sono state saltate.");
                    continue;
                }
                if (f.Kind != "BINARY" && f.Kind != "MOTOROLA")
                    result.Note($"File \"{f.Declared}\" di tipo {f.Kind}: viene letto come dati grezzi, " +
                                "senza decodifica audio.");
                if (f.Kind == "MOTOROLA")
                    result.Note($"File \"{f.Declared}\" in formato MOTOROLA (big endian): " +
                                "i campioni audio non vengono riordinati.");

                var src = new FileBlockSource(f.Resolved, 2048);
                result.Own(src);
                sources[i] = src;
            }

            BuildTracks(result, files, tracks, sources);

            if (result.Tracks.Count == 0) { result.Dispose(); return false; }

            int audio = 0, data = 0;
            foreach (var t in result.Tracks) { if (t.IsAudio) audio++; else data++; }
            result.Note($"Cue sheet letto: {files.Count} file, {result.Tracks.Count} tracce " +
                        $"({data} dati, {audio} audio).");

            image = result;
            return true;
        }
        catch
        {
            result?.Dispose();
            return false;
        }
    }

    /// <summary>Calcola LBA, offset e lunghezze di ogni traccia.</summary>
    private static void BuildTracks(DiscImage image, List<CueFile> files, List<CueTrack> tracks,
                                    Dictionary<int, IBlockSource> sources)
    {
        long runningLba = 0;
        int index = 0;

        while (index < tracks.Count)
        {
            int fileIndex = tracks[index].FileIndex;
            int groupStart = index;
            while (index < tracks.Count && tracks[index].FileIndex == fileIndex) index++;
            int groupEnd = index;   // esclusivo

            if (fileIndex < 0 || fileIndex >= files.Count) continue;
            var file = files[fileIndex];

            // dimensione del settore del file: quella della prima traccia che lo usa
            int fileSectorSize = tracks[groupStart].SectorSize;
            for (int i = groupStart + 1; i < groupEnd; i++)
            {
                if (tracks[i].SectorSize != fileSectorSize)
                {
                    image.Note($"Il file \"{file.Declared}\" contiene tracce con settori di dimensione diversa " +
                               $"({fileSectorSize} e {tracks[i].SectorSize}): le posizioni INDEX sono state " +
                               $"convertite usando {fileSectorSize} byte per settore.");
                    break;
                }
            }

            long fileSectors = fileSectorSize > 0 ? file.Length / fileSectorSize : 0;
            if (file.Length % Math.Max(1, fileSectorSize) != 0)
                image.Note($"Il file \"{file.Declared}\" non è multiplo di {fileSectorSize} byte: " +
                           $"gli ultimi {file.Length % fileSectorSize} byte sono stati ignorati.");

            // posizione del primo settore usato nel file
            var first = tracks[groupStart];
            if (first.Index1 < 0) first.Index1 = Math.Max(0, first.Index0);
            long firstPos = first.Index0 >= 0 ? first.Index0 : first.Index1;

            runningLba += first.Pregap;
            long fileBase = runningLba - firstPos;
            long gapShift = 0;

            for (int i = groupStart; i < groupEnd; i++)
            {
                var t = tracks[i];
                if (i > groupStart) gapShift += t.Pregap;
                if (t.Index1 < 0) t.Index1 = Math.Max(0, t.Index0);

                long endPos;
                if (i + 1 < groupEnd)
                {
                    var next = tracks[i + 1];
                    endPos = next.Index0 >= 0 ? next.Index0 : Math.Max(0, next.Index1);
                }
                else endPos = fileSectors;

                long sectors = endPos - t.Index1;
                if (sectors < 0) sectors = 0;

                if (t.Pregap > 0)
                    image.Note($"Traccia {t.Number}: PREGAP di {t.Pregap} settori non presente nel file, " +
                               "conteggiato solo negli LBA.");
                if (t.Postgap > 0)
                    image.Note($"Traccia {t.Number}: POSTGAP di {t.Postgap} settori non presente nel file.");

                if (!sources.TryGetValue(fileIndex, out var source) || sectors == 0)
                {
                    if (sectors == 0)
                        image.Note($"Traccia {t.Number}: nessun settore nel file, saltata.");
                    gapShift += t.Postgap;
                    continue;
                }

                var track = new ImageTrack
                {
                    Number = t.Number,
                    Session = 1,
                    IsAudio = t.IsAudio,
                    StartLba = fileBase + gapShift + t.Index1,
                    Sectors = sectors,
                    SectorSize = t.SectorSize,
                    DataOffset = t.DataOffset,
                    UserDataSize = t.UserDataSize,
                    Title = t.Title
                };
                image.Tracks.Add(track);
                image.Attach(track, source, t.Index1 * (long)fileSectorSize);

                gapShift += t.Postgap;
            }

            runningLba = fileBase + gapShift + fileSectors;
        }
    }

    /// <summary>Traduce la modalità dichiarata nel cue in geometria del settore.</summary>
    private static void ApplyMode(CueTrack track, string mode)
    {
        track.Mode = (mode ?? "").ToUpperInvariant();
        switch (track.Mode)
        {
            case "AUDIO":
                track.IsAudio = true; track.SectorSize = 2352; track.DataOffset = 0; track.UserDataSize = 2352;
                break;
            case "CDG":
            case "CDG/2448":
                track.IsAudio = true; track.SectorSize = 2448; track.DataOffset = 0; track.UserDataSize = 2352;
                break;
            case "MODE1/2048":
            case "MODE2/2048":
            case "CDI/2048":
                track.SectorSize = 2048; track.DataOffset = 0; track.UserDataSize = 2048;
                break;
            case "MODE1/2352":
                track.SectorSize = 2352; track.DataOffset = 16; track.UserDataSize = 2048;
                break;
            case "MODE1/2448":
                track.SectorSize = 2448; track.DataOffset = 16; track.UserDataSize = 2048;
                break;
            case "MODE2/2336":
            case "CDI/2336":
                track.SectorSize = 2336; track.DataOffset = 8; track.UserDataSize = 2048;
                break;
            case "MODE2/2324":
                track.SectorSize = 2324; track.DataOffset = 0; track.UserDataSize = 2048;
                break;
            case "MODE2/2352":
            case "CDI/2352":
                // FORM1 a 24, FORM2 a 24 con 2324 byte: si decide settore per settore
                track.SectorSize = 2352; track.DataOffset = -1; track.UserDataSize = 2048;
                break;
            case "MODE2/2448":
                track.SectorSize = 2448; track.DataOffset = -1; track.UserDataSize = 2048;
                break;
            default:
                // modalità sconosciuta: settori da 2352 con riconoscimento automatico
                track.SectorSize = 2352; track.DataOffset = -1; track.UserDataSize = 2048;
                break;
        }
    }

    /// <summary>MM:SS:FF → settori (75 frame al secondo). -1 se non è una posizione valida.</summary>
    public static long ParseMsf(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return -1;
        var parts = value.Trim().Split(':');
        if (parts.Length != 3) return -1;
        if (!int.TryParse(parts[0], out int m) || !int.TryParse(parts[1], out int s) ||
            !int.TryParse(parts[2], out int f)) return -1;
        if (m < 0 || s < 0 || f < 0 || s > 59 || f > 74) return -1;
        return (m * 60L + s) * 75L + f;
    }

    /// <summary>Settori → MM:SS:FF.</summary>
    public static string FormatMsf(long sectors)
    {
        if (sectors < 0) sectors = 0;
        long f = sectors % 75; sectors /= 75;
        long s = sectors % 60; long m = sectors / 60;
        return $"{m:00}:{s:00}:{f:00}";
    }

    /// <summary>Spezza una riga del cue tenendo insieme le stringhe fra virgolette.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            if (line[i] == '"')
            {
                i++;
                int start = i;
                while (i < line.Length && line[i] != '"') i++;
                tokens.Add(line.Substring(start, i - start));
                if (i < line.Length) i++;
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                tokens.Add(line.Substring(start, i - start));
            }
        }
        return tokens;
    }

    /// <summary>
    /// Trova il file dichiarato nel cue: percorso relativo alla cartella del cue, solo il nome
    /// se il cue contiene un percorso assoluto di un'altra macchina, con ricerca case insensitive.
    /// </summary>
    private static string ResolveFile(string cuePath, string declared)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(declared)) return null;
            string dir = Path.GetDirectoryName(Path.GetFullPath(cuePath));
            if (string.IsNullOrEmpty(dir)) dir = ".";

            string name = declared.Replace('\\', Path.DirectorySeparatorChar);
            string candidate = Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
            string found = DiscImage.ResolveCaseInsensitive(candidate);
            if (found != null) return found;

            // il cue arriva spesso da un'altra macchina: prova con il solo nome del file
            string bare = Path.GetFileName(name);
            found = DiscImage.ResolveCaseInsensitive(Path.Combine(dir, bare));
            if (found != null) return found;

            // ultimo tentativo: stesso nome del cue con estensione .bin/.img
            foreach (string ext in new[] { ".bin", ".img", ".iso" })
            {
                found = DiscImage.ResolveCaseInsensitive(Path.ChangeExtension(cuePath, ext));
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Legge il cue come testo senza inciampare in BOM o caratteri non UTF-8.</summary>
    private static string ReadTextTolerant(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            // se è UTF-8 valido usiamo quello, altrimenti Latin1 che non fallisce mai
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch { return Encoding.Latin1.GetString(bytes); }
        }
        catch { return null; }
    }
}
