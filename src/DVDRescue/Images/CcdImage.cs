using System.Globalization;
using System.Text;
using DVDRescue.Core;

namespace DVDRescue.Images;

/// <summary>
/// Lettore di immagini CloneCD (CCD descrittore + IMG dati + SUB subchannel).
///
/// Il .ccd è un file INI: [CloneCD], [Disc], [Session N], [Entry N] e [TRACK N].
/// Ogni [Entry] è una voce di TOC: Point da 0x01 a 0x63 è una traccia e PLBA ne dà l'LBA
/// iniziale; Point 0xA0/0xA1 danno prima e ultima traccia, 0xA2 l'inizio del lead-out,
/// cioè la fine dell'ultima traccia.
/// Il .img contiene i settori grezzi da 2352 byte a partire da LBA 0.
/// </summary>
public static class CcdImage
{
    public static bool TryOpen(string path, out DiscImage image)
    {
        image = null;
        DiscImage result = null;
        FileBlockSource img = null;
        try
        {
            string ccdPath = path;
            if (!string.Equals(Path.GetExtension(path), ".ccd", StringComparison.OrdinalIgnoreCase))
            {
                string sibling = DiscImage.ResolveCaseInsensitive(Path.ChangeExtension(path, ".ccd"));
                if (sibling == null) return false;
                ccdPath = sibling;
            }
            if (!File.Exists(ccdPath)) return false;

            var ini = ParseIni(ccdPath);
            if (ini == null || ini.Count == 0) return false;
            if (!ini.ContainsKey("clonecd") && !ini.ContainsKey("disc")) return false;

            string imgPath = DiscImage.ResolveCaseInsensitive(Path.ChangeExtension(ccdPath, ".img"));
            if (imgPath == null || !File.Exists(imgPath)) return false;
            long imgLength = new FileInfo(imgPath).Length;
            if (imgLength <= 0) return false;

            result = new DiscImage { FormatName = "CloneCD CCD/IMG", Path = ccdPath };

            int version = GetInt(ini, "clonecd", "version", 0);
            int sessions = GetInt(ini, "disc", "sessions", 1);
            int tocEntries = GetInt(ini, "disc", "tocentries", 0);
            int scrambled = GetInt(ini, "disc", "datatracksscrambled", 0);
            int cdTextLength = GetInt(ini, "disc", "cdtextlength", 0);

            result.Note($"Descrittore CloneCD versione {version}, {sessions} sessioni, {tocEntries} voci di TOC.");
            if (cdTextLength > 0) result.Note($"Presente CD-Text ({cdTextLength} byte), non utilizzato.");

            if (scrambled != 0)
                result.Note("ATTENZIONE: il descrittore dichiara DataTracksScrambled=1. " +
                            "I settori dati sono ancora scramblati e questo lettore non li descrambla: " +
                            "il contenuto letto NON sarà utilizzabile.");

            string subPath = DiscImage.ResolveCaseInsensitive(Path.ChangeExtension(ccdPath, ".sub"));
            if (subPath != null && File.Exists(subPath))
                result.Note($"Trovato il file subchannel \"{Path.GetFileName(subPath)}\", non utilizzato.");

            // dimensione del settore nel .img: quasi sempre 2352
            int sectorSize = 2352;
            if (imgLength % 2352 != 0)
            {
                if (imgLength % 2448 == 0) sectorSize = 2448;
                else if (imgLength % 2048 == 0) sectorSize = 2048;
                else
                {
                    sectorSize = RawSectorSource.ProbeRawSectorSize(imgPath);
                    result.Note($"Il file IMG non è multiplo di 2352 byte: usati settori da {sectorSize} byte.");
                }
            }
            long imgSectors = imgLength / sectorSize;

            // voci di TOC
            var entries = new List<(int session, int point, long plba, int control)>();
            long leadOutLba = -1;
            int firstTrack = -1, lastTrack = -1;

            foreach (var kv in ini)
            {
                if (!kv.Key.StartsWith("entry ", StringComparison.Ordinal)) continue;
                var section = kv.Value;

                int point = GetHex(section, "point", -1);
                if (point < 0) continue;
                int session = GetIntFrom(section, "session", 1);
                int control = GetHex(section, "control", 0);
                long plba = GetLongFrom(section, "plba", long.MinValue);

                if (plba == long.MinValue)
                {
                    // alcuni CCD danno solo PMin/PSec/PFrame
                    int pm = GetIntFrom(section, "pmin", -1);
                    int ps = GetIntFrom(section, "psec", -1);
                    int pf = GetIntFrom(section, "pframe", -1);
                    if (pm >= 0 && ps >= 0 && pf >= 0) plba = (pm * 60L + ps) * 75L + pf - 150;
                }

                if (point == 0xA0) { firstTrack = GetIntFrom(section, "pmin", -1); continue; }
                if (point == 0xA1) { lastTrack = GetIntFrom(section, "pmin", -1); continue; }
                if (point == 0xA2) { if (plba != long.MinValue) leadOutLba = plba; continue; }
                if (point < 1 || point > 99) continue;
                if (plba == long.MinValue) continue;

                entries.Add((session, point, plba, control));
            }

            if (entries.Count == 0) { result.Dispose(); return false; }
            entries.Sort((a, b) => a.plba != b.plba ? a.plba.CompareTo(b.plba) : a.point.CompareTo(b.point));

            if (firstTrack > 0 && lastTrack > 0)
                result.Note($"TOC: tracce da {firstTrack} a {lastTrack}" +
                            (leadOutLba >= 0 ? $", lead-out a LBA {leadOutLba}." : "."));

            if (leadOutLba < 0)
            {
                leadOutLba = imgSectors;
                result.Note("Il lead-out (Point 0xA2) non è dichiarato: la fine dell'ultima traccia " +
                            "è stata dedotta dalla dimensione del file IMG.");
            }

            img = new FileBlockSource(imgPath, 2048);
            result.Own(img);

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                long end = i + 1 < entries.Count ? entries[i + 1].plba : leadOutLba;
                long sectors = end - e.plba;
                if (sectors <= 0)
                {
                    result.Note($"Traccia {e.point}: lunghezza nulla o negativa nel TOC, saltata.");
                    continue;
                }

                int mode = GetInt(ini, $"track {e.point}", "mode", -1);
                // se il MODE non c'è, il bit 2 del campo Control distingue dati da audio
                bool audio = mode >= 0 ? mode == 0 : (e.control & 0x04) == 0;

                long byteOffset = e.plba * (long)sectorSize;
                if (byteOffset < 0 || byteOffset >= imgLength)
                {
                    result.Note($"Traccia {e.point}: LBA {e.plba} fuori dal file IMG, saltata.");
                    continue;
                }

                long available = (imgLength - byteOffset) / sectorSize;
                if (sectors > available)
                {
                    result.Note($"Traccia {e.point}: dichiarati {sectors} settori ma il file IMG ne contiene " +
                                $"{available}, la traccia è stata accorciata.");
                    sectors = available;
                }
                if (sectors <= 0) continue;

                var track = new ImageTrack
                {
                    Number = e.point,
                    Session = e.session <= 0 ? 1 : e.session,
                    IsAudio = audio,
                    StartLba = e.plba,
                    Sectors = sectors,
                    SectorSize = sectorSize
                };

                if (audio)
                {
                    track.DataOffset = 0;
                    track.UserDataSize = sectorSize >= 2352 ? 2352 : sectorSize;
                }
                else if (sectorSize == 2048) { track.DataOffset = 0; track.UserDataSize = 2048; }
                else { track.DataOffset = -1; track.UserDataSize = 2048; }

                result.Tracks.Add(track);
                result.Attach(track, img, byteOffset);
            }

            if (result.Tracks.Count == 0) { result.Dispose(); return false; }

            result.Note($"CloneCD letto: {result.Tracks.Count} tracce da \"{Path.GetFileName(imgPath)}\" " +
                        $"({imgSectors} settori da {sectorSize} byte).");
            image = result;
            return true;
        }
        catch
        {
            result?.Dispose();
            img?.Dispose();
            return false;
        }
    }

    // ---------------------------------------------------------------- INI

    /// <summary>Legge un INI in sezioni e chiavi, tutto in minuscolo per i confronti.</summary>
    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        try
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            result[""] = current;

            byte[] bytes = File.ReadAllBytes(path);
            string text = Encoding.Latin1.GetString(bytes);

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r').Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    string name = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    // normalizza gli spazi: "Session  1" e "Session 1" sono la stessa sezione
                    name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    if (!result.TryGetValue(name, out current))
                    {
                        current = new Dictionary<string, string>(StringComparer.Ordinal);
                        result[name] = current;
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();
                current[key] = value;
            }
            return result;
        }
        catch { return null; }
    }

    private static int GetInt(Dictionary<string, Dictionary<string, string>> ini, string section, string key, int fallback)
        => ini.TryGetValue(section, out var s) ? GetIntFrom(s, key, fallback) : fallback;

    private static int GetIntFrom(Dictionary<string, string> section, string key, int fallback)
    {
        long v = GetLongFrom(section, key, long.MinValue);
        return v == long.MinValue || v > int.MaxValue || v < int.MinValue ? fallback : (int)v;
    }

    private static long GetLongFrom(Dictionary<string, string> section, string key, long fallback)
    {
        if (section == null || !section.TryGetValue(key, out string raw)) return fallback;
        raw = raw.Trim();
        if (raw.Length == 0) return fallback;

        bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) raw = raw.Substring(2);

        return hex
            ? (long.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h) ? h : fallback)
            : (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long d) ? d : fallback);
    }

    /// <summary>Legge un valore scritto come 0xNN (Point, ADR, Control).</summary>
    private static int GetHex(Dictionary<string, string> section, string key, int fallback)
    {
        if (section == null || !section.TryGetValue(key, out string raw)) return fallback;
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(2);
        return int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }
}
