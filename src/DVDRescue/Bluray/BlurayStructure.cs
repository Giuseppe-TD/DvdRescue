using DVDRescue.Core;

namespace DVDRescue.Bluray;

/// <summary>Un clip (file .m2ts/.MTS) con la sua collocazione sul supporto.</summary>
public sealed class BluClip
{
    /// <summary>Percorso logico del file, come compare nel filesystem.</summary>
    public string FileName;

    /// <summary>Offset in byte sulla sorgente; -1 se il file non è stato trovato.</summary>
    public long Offset = -1;

    public long Length;
    public double Seconds;
    public string Codec;
    public string Resolution;

    public override string ToString() => $"{FileName} {Seconds:F1}s {Length / 1048576.0:F0} MB";
}

/// <summary>Un titolo: una playlist, oppure un singolo file quando le playlist mancano.</summary>
public sealed class BluTitle
{
    /// <summary>Numero originale (ordine dei file di playlist / dei clip), da 1.</summary>
    public int Number;

    /// <summary>Nome del file di playlist; vuoto se il titolo è stato ricostruito dai clip.</summary>
    public string PlaylistName;

    public double Seconds;
    public List<BluClip> Clips = new();

    /// <summary>Capitoli in secondi dall'inizio del titolo.</summary>
    public List<double> ChapterMarks = new();

    public override string ToString()
        => $"#{Number} {(string.IsNullOrEmpty(PlaylistName) ? Clips.FirstOrDefault()?.FileName : PlaylistName)} " +
           $"{Seconds:F1}s, {Clips.Count} clip, {ChapterMarks.Count} capitoli";
}

/// <summary>Esito della lettura di un disco ad alta definizione.</summary>
public sealed class BlurayStructure
{
    /// <summary>"BDMV" (Blu-ray video), "BDAV" (registratori) o "AVCHD" (videocamere).</summary>
    public string Kind;

    /// <summary>Titoli ordinati per durata decrescente; <see cref="BluTitle.Number"/> resta l'originale.</summary>
    public List<BluTitle> Titles = new();

    /// <summary>Note in italiano su cosa si è letto e su cosa non si è potuto leggere.</summary>
    public List<string> Notes = new();

    public bool IsValid;
}

/// <summary>
/// Lettura delle strutture di navigazione dei dischi ad alta definizione.
///
/// Tutte le strutture Blu-ray sono <b>big endian</b> e i tempi delle playlist sono
/// su un orologio a 45 kHz.
///
/// L'ordine di preferenza è: playlist (.mpls/.rpls/.vpls) → informazioni di clip
/// (.clpi) → misura diretta dello stream con <see cref="TsCarver"/>. Se le playlist
/// mancano o sono illeggibili — che è il caso più comune sui dischi rotti o non
/// finalizzati — i titoli si costruiscono dai file .m2ts/.MTS ordinati per nome.
/// </summary>
public static class BlurayReader
{
    /// <summary>Tetto di lettura per un file di struttura: le playlist vere stanno in poche decine di KB.</summary>
    private const int MaxStructureBytes = 8 << 20;

    /// <summary>
    /// Ricostruisce i titoli di un disco HD.
    /// </summary>
    /// <param name="source">Sorgente su cui stanno i byte dei file.</param>
    /// <param name="files">
    /// Percorso logico → (offset in byte, lunghezza). Si presume che ogni file occupi
    /// un tratto contiguo: è vero per i file A/V su UDF, non sempre per i file piccoli.
    /// </param>
    /// <param name="log">Callback di tracciatura, può essere null.</param>
    public static BlurayStructure Parse(IBlockSource source,
                                        IReadOnlyDictionary<string, (long Offset, long Length)> files,
                                        Action<string> log)
    {
        var st = new BlurayStructure();
        void Log(string m) { try { log?.Invoke(m); } catch { } }

        try
        {
            if (files == null || files.Count == 0)
            {
                st.Notes.Add("Nessun file elencato: impossibile riconoscere la struttura del disco.");
                return st;
            }

            // ---- normalizzazione dei percorsi (maiuscolo, separatori uniformi)
            var norm = new Dictionary<string, string>(StringComparer.Ordinal);   // CHIAVE -> percorso originale
            foreach (var kv in files)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                string k = kv.Key.Replace('\\', '/').TrimStart('/').ToUpperInvariant();
                if (k.Length > 0) norm[k] = kv.Key;
            }

            st.Kind = DetectKind(norm.Keys, st.Notes);
            Log($"Tipo di disco riconosciuto: {st.Kind ?? "sconosciuto"}");

            var streams = norm.Keys
                .Where(k => k.EndsWith(".M2TS", StringComparison.Ordinal) ||
                            k.EndsWith(".MTS", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            var playlists = norm.Keys
                .Where(k => k.EndsWith(".MPLS", StringComparison.Ordinal) ||
                            k.EndsWith(".RPLS", StringComparison.Ordinal) ||
                            k.EndsWith(".VPLS", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            var clipInfos = norm.Keys
                .Where(k => k.EndsWith(".CLPI", StringComparison.Ordinal))
                .ToList();

            Log($"{streams.Count} file di stream, {playlists.Count} playlist, {clipInfos.Count} file di clip info");

            // indice: identificativo del clip ("00000") -> chiave del file di stream
            var byClipId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in streams)
            {
                string id = BaseName(s);
                if (!byClipId.ContainsKey(id)) byClipId[id] = s;
            }
            var clpiById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in clipInfos)
            {
                string id = BaseName(c);
                if (!clpiById.ContainsKey(id)) clpiById[id] = c;
            }

            var carver = new TsCarver();
            var clipCache = new Dictionary<string, BluClip>(StringComparer.Ordinal);

            // ---- 1) titoli dalle playlist
            int number = 0;
            int badPlaylists = 0;

            foreach (var plKey in playlists)
            {
                number++;
                byte[] data = ReadWhole(source, files, norm, plKey, MaxStructureBytes);
                if (data == null || data.Length < 40)
                {
                    badPlaylists++;
                    st.Notes.Add($"Playlist {norm[plKey]} illeggibile o troppo corta.");
                    continue;
                }

                var mpls = Mpls.Parse(data, data.Length);
                if (mpls == null || mpls.Items.Count == 0)
                {
                    badPlaylists++;
                    st.Notes.Add($"Playlist {norm[plKey]} non interpretabile" +
                                 (mpls?.Problem != null ? $" ({mpls.Problem})" : "") + ".");
                    continue;
                }

                if (mpls.Problem != null) st.Notes.Add($"Playlist {norm[plKey]}: {mpls.Problem}");

                var title = new BluTitle { Number = number, PlaylistName = norm[plKey] };
                double acc = 0;

                foreach (var it in mpls.Items)
                {
                    string key = byClipId.TryGetValue(it.ClipId, out var sk) ? sk : null;
                    var clip = key != null
                        ? GetClip(clipCache, source, files, norm, key, clpiById, carver, st.Notes)
                        : new BluClip { FileName = it.ClipId + ".M2TS", Offset = -1 };

                    if (key == null)
                        st.Notes.Add($"Clip {it.ClipId} citato da {norm[plKey]} ma non presente sul disco.");

                    // copia propria del clip: playlist diverse possono usarne tratti diversi
                    clip = Clone(clip);

                    double seg = (it.OutTime - it.InTime) / 45000.0;
                    if (seg > 0 && seg < 24 * 3600) clip.Seconds = seg;

                    acc += clip.Seconds;
                    title.Clips.Add(clip);
                }

                title.Seconds = acc;

                foreach (var mk in mpls.Marks)
                {
                    if (mk.Type != 0x01) continue;                // 0x01 = capitolo, 0x02 = punto di collegamento
                    if (mk.ItemId < 0 || mk.ItemId >= mpls.Items.Count) continue;

                    double before = 0;
                    for (int i = 0; i < mk.ItemId; i++)
                        before += (mpls.Items[i].OutTime - mpls.Items[i].InTime) / 45000.0;

                    double t = before + (mk.Time - mpls.Items[mk.ItemId].InTime) / 45000.0;
                    if (t >= -0.001 && t <= title.Seconds + 1.0) title.ChapterMarks.Add(Math.Max(0, t));
                }
                title.ChapterMarks.Sort();

                if (title.Seconds <= 0)
                {
                    // playlist con tempi assurdi: si misura lo stream
                    title.Seconds = title.Clips.Sum(c => c.Seconds);
                    if (title.Seconds <= 0)
                    {
                        st.Notes.Add($"Playlist {norm[plKey]}: durata non ricavabile, titolo scartato.");
                        continue;
                    }
                }

                st.Titles.Add(title);
                Log($"Titolo {number}: {norm[plKey]} — {title.Seconds:F1}s, " +
                    $"{title.Clips.Count} clip, {title.ChapterMarks.Count} capitoli");
            }

            if (playlists.Count > 0 && badPlaylists == playlists.Count)
                st.Notes.Add("Nessuna playlist leggibile: si passa alla ricostruzione dai file di stream.");

            // ---- 2) ripiego: un titolo per file di stream
            if (st.Titles.Count == 0 && streams.Count > 0)
            {
                st.Notes.Add(playlists.Count == 0
                    ? "Playlist assenti: titoli ricostruiti dai file di stream, in ordine di nome."
                    : "Playlist inutilizzabili: titoli ricostruiti dai file di stream, in ordine di nome.");

                int n = 0;
                foreach (var key in streams)
                {
                    n++;
                    var clip = GetClip(clipCache, source, files, norm, key, clpiById, carver, st.Notes);
                    if (clip.Length <= 0) continue;

                    var t = new BluTitle { Number = n, PlaylistName = "", Seconds = clip.Seconds };
                    t.Clips.Add(clip);
                    st.Titles.Add(t);
                    Log($"Titolo {n} da {clip.FileName}: {clip.Seconds:F1}s " +
                        $"{clip.Codec} {clip.Resolution}");
                }
            }

            // ---- 3) niente di niente
            if (st.Titles.Count == 0)
            {
                if (streams.Count == 0)
                    st.Notes.Add("Nessun file .m2ts/.MTS trovato: il disco non contiene stream video HD, " +
                                 "oppure il filesystem è troppo danneggiato per elencarli. " +
                                 "Provare la scansione grezza dei settori con TsCarver.Scan.");
                st.IsValid = false;
                return st;
            }

            // ---- ordinamento per durata, numero originale conservato
            st.Titles = st.Titles.OrderByDescending(t => t.Seconds).ThenBy(t => t.Number).ToList();
            st.IsValid = st.Titles.Any(t => t.Seconds > 0);

            if (!st.IsValid)
                st.Notes.Add("Titoli individuati ma senza durata utilizzabile.");

            if (string.IsNullOrEmpty(st.Kind))
            {
                st.Kind = streams.Any(s => s.EndsWith(".MTS", StringComparison.Ordinal)) ? "AVCHD" : "BDMV";
                st.Notes.Add($"Tipo di disco non dichiarato dalle cartelle: presunto {st.Kind} " +
                             "dall'estensione dei file di stream.");
            }
        }
        catch (Exception ex)
        {
            st.Notes.Add("Lettura interrotta da dati non validi: " + ex.Message);
            st.IsValid = st.Titles.Count > 0 && st.Titles.Any(t => t.Seconds > 0);
        }

        return st;
    }

    // ------------------------------------------------------------ riconoscimento

    private static string DetectKind(IEnumerable<string> keys, List<string> notes)
    {
        bool avchd = false, bdav = false, bdmv = false;
        bool indexBdmv = false, infoBdav = false;

        foreach (var k in keys)
        {
            if (k.Contains("AVCHD/BDMV/", StringComparison.Ordinal) ||
                k.StartsWith("AVCHD/", StringComparison.Ordinal) ||
                k.Contains("/AVCHD/", StringComparison.Ordinal)) avchd = true;

            if (k.StartsWith("BDAV/", StringComparison.Ordinal) ||
                k.Contains("/BDAV/", StringComparison.Ordinal)) bdav = true;

            if (k.StartsWith("BDMV/", StringComparison.Ordinal) ||
                k.Contains("/BDMV/", StringComparison.Ordinal)) bdmv = true;

            if (k.EndsWith("BDMV/INDEX.BDMV", StringComparison.Ordinal)) indexBdmv = true;
            if (k.EndsWith("BDAV/INFO.BDAV", StringComparison.Ordinal)) infoBdav = true;
        }

        if (avchd)
        {
            if (!indexBdmv) notes.Add("AVCHD senza index.bdmv: registrazione non chiusa dalla videocamera.");
            return "AVCHD";
        }
        if (bdav)
        {
            if (!infoBdav) notes.Add("Cartella BDAV senza info.bdav: registrazione non finalizzata.");
            return "BDAV";
        }
        if (bdmv)
        {
            if (!indexBdmv) notes.Add("Cartella BDMV senza index.bdmv: disco non finalizzato o indice perduto.");
            return "BDMV";
        }
        return null;
    }

    private static string BaseName(string key)
    {
        int slash = key.LastIndexOf('/');
        int dot = key.LastIndexOf('.');
        int from = slash + 1;
        int len = dot > slash ? dot - from : key.Length - from;
        return len > 0 ? key.Substring(from, len) : key;
    }

    private static BluClip Clone(BluClip c) => new()
    {
        FileName = c.FileName,
        Offset = c.Offset,
        Length = c.Length,
        Seconds = c.Seconds,
        Codec = c.Codec,
        Resolution = c.Resolution
    };

    // ------------------------------------------------------------ clip

    private static BluClip GetClip(Dictionary<string, BluClip> cache, IBlockSource source,
                                   IReadOnlyDictionary<string, (long Offset, long Length)> files,
                                   Dictionary<string, string> norm, string key,
                                   Dictionary<string, string> clpiById, TsCarver carver,
                                   List<string> notes)
    {
        if (cache.TryGetValue(key, out var hit)) return hit;

        var entry = files[norm[key]];
        var clip = new BluClip { FileName = norm[key], Offset = entry.Offset, Length = entry.Length };
        cache[key] = clip;

        string id = BaseName(key);

        // 1) informazioni dal .clpi, se c'è
        if (clpiById.TryGetValue(id, out var clpiKey))
        {
            var data = ReadWhole(source, files, norm, clpiKey, MaxStructureBytes);
            var info = Clpi.Parse(data, data?.Length ?? 0);
            if (info != null)
            {
                if (info.Seconds > 0) clip.Seconds = info.Seconds;
                clip.Codec = info.Codec;
                clip.Resolution = info.Resolution;
                if (info.Problem != null) notes.Add($"Clip info {norm[clpiKey]}: {info.Problem}");
            }
            else notes.Add($"Clip info {norm[clpiKey]} non interpretabile.");
        }

        // 2) quello che manca si ricava dallo stream
        if (clip.Length > 0 && (clip.Seconds <= 0 || clip.Codec == null || clip.Resolution == null))
        {
            try
            {
                var seg = carver.Analyze(source, clip.Offset, clip.Length, 0);
                if (clip.Seconds <= 0) clip.Seconds = seg.Seconds;
                clip.Codec ??= seg.VideoCodec;
                clip.Resolution ??= seg.Resolution;
                if (seg.PacketSize == 0)
                    notes.Add($"{clip.FileName}: nessun Transport Stream riconoscibile all'inizio del file.");
            }
            catch (Exception ex) { notes.Add($"{clip.FileName}: analisi fallita ({ex.Message})."); }
        }
        else if (clip.Length <= 0)
        {
            notes.Add($"{clip.FileName}: lunghezza sconosciuta o nulla.");
        }

        return clip;
    }

    private static byte[] ReadWhole(IBlockSource source,
                                    IReadOnlyDictionary<string, (long Offset, long Length)> files,
                                    Dictionary<string, string> norm, string key, int cap)
    {
        try
        {
            if (source == null || !norm.TryGetValue(key, out var original)) return null;
            if (!files.TryGetValue(original, out var e)) return null;
            if (e.Length <= 0 || e.Offset < 0) return null;

            int want = (int)Math.Min(e.Length, cap);
            var buf = new byte[want];
            int got = source.ReadBytes(e.Offset, want, buf, 0);
            if (got <= 0) return null;
            if (got < want) Array.Resize(ref buf, got);
            return buf;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ playlist (.mpls/.rpls/.vpls)

    /// <summary>
    /// Playlist Blu-ray. Intestazione "MPLS" + versione, poi due indirizzi: la
    /// PlayList (elenco dei PlayItem) e la PlayListMark (i capitoli). Le .rpls e .vpls
    /// dei registratori BDAV usano la stessa famiglia di strutture.
    /// </summary>
    private sealed class Mpls
    {
        public sealed class Item { public string ClipId; public string Codec; public long InTime, OutTime; }
        public sealed class Mark { public int Type; public int ItemId; public long Time; }

        public readonly List<Item> Items = new();
        public readonly List<Mark> Marks = new();
        public string Version;
        public string Problem;

        public static Mpls Parse(byte[] d, int len)
        {
            try
            {
                if (d == null || len < 40) return null;

                string magic = ByteUtils.Ascii(d, 0, 4);
                var m = new Mpls { Version = ByteUtils.Ascii(d, 4, 4) };

                if (magic != "MPLS")
                {
                    // qualche registratore mette una sigla diversa sulle .rpls/.vpls
                    if (magic != "MPLC" && magic != "BDAV") return null;
                    m.Problem = $"intestazione \"{magic}\" invece di \"MPLS\", lettura tentata lo stesso";
                }

                if (m.Version != "0100" && m.Version != "0200" && m.Version != "0300")
                    m.Problem = (m.Problem == null ? "" : m.Problem + "; ") +
                                $"versione \"{m.Version}\" non prevista";

                long plStart = ByteUtils.U32BE(d, 8);
                long pmStart = ByteUtils.U32BE(d, 12);

                if (plStart < 40 || plStart + 10 > len)
                {
                    m.Problem = (m.Problem == null ? "" : m.Problem + "; ") + "indirizzo della PlayList fuori dal file";
                    return m;
                }

                int p = (int)plStart;
                int nItems = ByteUtils.U16BE(d, p + 6);
                int q = p + 10;

                for (int i = 0; i < nItems; i++)
                {
                    if (q + 2 > len) { m.Problem = "elenco dei PlayItem troncato"; break; }
                    int itemLen = ByteUtils.U16BE(d, q);
                    int s = q + 2;
                    if (itemLen < 20 || s + itemLen > len) { m.Problem = "PlayItem di lunghezza incoerente"; break; }

                    var it = new Item
                    {
                        ClipId = ByteUtils.Ascii(d, s, 5),
                        Codec = ByteUtils.Ascii(d, s + 5, 4),
                        InTime = ByteUtils.U32BE(d, s + 12),
                        OutTime = ByteUtils.U32BE(d, s + 16)
                    };

                    if (it.ClipId.Length > 0) m.Items.Add(it);
                    q = s + itemLen;
                }

                if (pmStart >= 40 && pmStart + 6 <= len)
                {
                    int r = (int)pmStart;
                    int nMarks = ByteUtils.U16BE(d, r + 4);
                    int mp = r + 6;
                    for (int i = 0; i < nMarks; i++)
                    {
                        if (mp + 14 > len) { m.Problem = "elenco dei capitoli troncato"; break; }
                        m.Marks.Add(new Mark
                        {
                            Type = d[mp + 1],
                            ItemId = ByteUtils.U16BE(d, mp + 2),
                            Time = ByteUtils.U32BE(d, mp + 4)
                        });
                        mp += 14;
                    }
                }

                return m;
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------ clip info (.clpi)

    /// <summary>
    /// Informazioni di clip Blu-ray: intestazione "HDMV", poi gli indirizzi di
    /// SequenceInfo (da cui si ricava la durata) e ProgramInfo (codec e formato video).
    /// </summary>
    private sealed class Clpi
    {
        public double Seconds;
        public string Codec;
        public string Resolution;
        public string Problem;

        public static Clpi Parse(byte[] d, int len)
        {
            try
            {
                if (d == null || len < 40) return null;
                if (ByteUtils.Ascii(d, 0, 4) != "HDMV") return null;

                var c = new Clpi();
                string ver = ByteUtils.Ascii(d, 4, 4);
                if (ver != "0100" && ver != "0200" && ver != "0300")
                    c.Problem = $"versione \"{ver}\" non prevista";

                long seqStart = ByteUtils.U32BE(d, 8);
                long progStart = ByteUtils.U32BE(d, 12);

                // --- SequenceInfo: length(32) + riservato(8) + numero di sequenze ATC(8)
                //     poi, per ogni sequenza STC, presentation_start_time/end_time a 45 kHz
                if (seqStart >= 40 && seqStart + 6 <= len)
                {
                    int p = (int)seqStart + 6;
                    int nAtc = d[p - 1];
                    long first = -1, last = -1;

                    for (int a = 0; a < nAtc && p + 6 <= len; a++)
                    {
                        p += 4;                                  // SPN_ATC_start
                        int nStc = d[p];
                        p += 2;                                  // number_of_STC_sequences + offset_STC_id
                        for (int s = 0; s < nStc && p + 14 <= len; s++)
                        {
                            long start = ByteUtils.U32BE(d, p + 6);
                            long end = ByteUtils.U32BE(d, p + 10);
                            if (first < 0) first = start;
                            last = end;
                            p += 14;
                        }
                    }

                    if (first >= 0 && last > first)
                    {
                        double sec = (last - first) / 45000.0;
                        if (sec > 0 && sec < 24 * 3600) c.Seconds = sec;
                    }
                }

                // --- ProgramInfo: length(32) + riservato(8) + numero di programmi(8)
                if (progStart >= 40 && progStart + 6 <= len)
                {
                    int p = (int)progStart + 6;
                    int nProg = d[p - 1];

                    for (int g = 0; g < nProg && p + 8 <= len; g++)
                    {
                        p += 6;                                  // SPN + program_map_PID
                        int nStreams = d[p];
                        p += 2;                                  // number_of_streams + reserved

                        for (int s = 0; s < nStreams && p + 3 <= len; s++)
                        {
                            p += 2;                              // stream_PID
                            int sciLen = d[p];
                            if (sciLen < 1 || p + 1 + sciLen > len) { p = len; break; }

                            int type = d[p + 1];
                            if (TsCarver.VideoInfo.IsVideo(type) && c.Codec == null)
                            {
                                c.Codec = TsCarver.VideoInfo.StreamName(type);
                                if (sciLen >= 2) c.Resolution = VideoFormat(d[p + 2] >> 4);
                            }
                            p += 1 + sciLen;
                        }
                    }
                }

                if (c.Seconds <= 0 && c.Codec == null) return null;
                return c;
            }
            catch { return null; }
        }

        /// <summary>Codici video_format delle strutture Blu-ray.</summary>
        private static string VideoFormat(int v) => v switch
        {
            1 => "720x480",     // 480i
            2 => "720x576",     // 576i
            3 => "720x480",     // 480p
            4 => "1920x1080",   // 1080i
            5 => "1280x720",    // 720p
            6 => "1920x1080",   // 1080p
            7 => "720x576",     // 576p
            8 => "3840x2160",   // 2160p
            _ => null
        };
    }
}
