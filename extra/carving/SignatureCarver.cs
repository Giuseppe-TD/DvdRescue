using System.Diagnostics;
using System.Globalization;
using DVDRescue.Core;

namespace DVDRescue.Carving;

/// <summary>Un file individuato dal carver.</summary>
public sealed class CarvedFile
{
    public FileSignature Signature;
    public long Offset;
    public long Length;

    /// <summary>False quando la lunghezza è una stima (footer assente, struttura interrotta).</summary>
    public bool LengthIsExact;

    public string SuggestedName;

    // ---- oltre il contratto minimo ----

    /// <summary>Estensione effettiva (può differire da quella della firma: docx, heic, nef...).</summary>
    public string Extension;

    /// <summary>Vero se il file sta dentro un altro file riconosciuto (thumbnail, allegato).</summary>
    public bool IsEmbedded;

    /// <summary>Offset del contenitore, -1 se il file è autonomo.</summary>
    public long ContainerOffset = -1;

    public long End => Offset + Length;

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} @ {1} ({2} byte{3})",
            SuggestedName ?? Signature?.Name, Offset, Length, LengthIsExact ? "" : ", stimati");
}

/// <summary>
/// Motore di carving per firma: ignora il filesystem e cerca i file direttamente nei byte.
/// Scansione a finestre grandi con coda di sovrapposizione, ricerca multi-pattern con
/// filtro a due byte, calcolo della lunghezza dalla struttura di ogni formato.
/// </summary>
public sealed class SignatureCarver
{
    /// <summary>Firme attive. Per difetto tutte quelle del catalogo.</summary>
    public List<FileSignature> Signatures { get; set; } = SignatureLibrary.All;

    /// <summary>Sotto questa lunghezza il risultato viene scartato (le firme possono alzarla).</summary>
    public long MinLength { get; set; } = 512;

    /// <summary>Dimensione della finestra di scansione: almeno 1 MB.</summary>
    public int WindowSize { get; set; } = 8 << 20;

    /// <summary>Forza la ricerca byte per byte ignorando l'allineamento delle firme (lenta, completa).</summary>
    public bool ByteByByteScan { get; set; }

    /// <summary>Tiene in elenco anche i file contenuti in altri file, marcandoli.</summary>
    public bool KeepEmbedded { get; set; }

    /// <summary>Aggiunge l'offset al nome suggerito.</summary>
    public bool IncludeOffsetInName { get; set; }

    /// <summary>Tetto difensivo sul numero di risultati.</summary>
    public int MaxResults { get; set; } = 2_000_000;

    public long LastBytesScanned { get; private set; }
    public TimeSpan LastDuration { get; private set; }
    public double LastMbPerSecond { get; private set; }
    public int LastEmbeddedCount { get; private set; }
    public int LastCandidateCount { get; private set; }

    // ---------------- tabella dei pattern ----------------

    private sealed class Pat
    {
        public FileSignature Sig;
        public int SigIndex;
        public byte[] Bytes;        // dal primo byte fisso in poi
        public byte[] Mask;         // null se tutti i byte sono fissi
        public int Skip;            // byte che precedono il primo byte fisso
        public int Second = -1;     // secondo e terzo byte fissi, per scartare senza chiamate
        public int Third = -1;
    }

    /// <summary>
    /// Le firme sono raggruppate per allineamento: quelle a 512 o 2048 vengono provate solo
    /// sui confini di settore (una posizione ogni 512), quelle a 1 passano dal ciclo caldo.
    /// È qui che sta la differenza fra minuti e ore su un disco intero.
    /// </summary>
    private sealed class ScanGroup
    {
        public int Align;
        public int AlignMask;       // Align-1 se potenza di due, altrimenti -1
        public int[] Skips;
        public byte[] Bitmap;       // 65536 bit sui primi due byte
        public Pat[][] Buckets;
        public int MaxSkip;
    }

    private ScanGroup[] _groups;
    private int _maxSkip;
    private int _maxPat;

    private void BuildIndex(List<FileSignature> sigs)
    {
        _maxSkip = 0;
        _maxPat = 1;

        var byAlign = new Dictionary<int, List<Pat>>();

        for (int si = 0; si < sigs.Count; si++)
        {
            var s = sigs[si];
            if (s?.Header == null || s.Header.Length == 0) continue;

            byte[] h = s.Header;
            byte[] m = s.HeaderMask != null && s.HeaderMask.Length == h.Length ? s.HeaderMask : null;

            int lead = 0;
            if (m != null) while (lead < h.Length && m[lead] == 0) lead++;
            if (lead >= h.Length) continue;                          // firma tutta mascherata: inutile

            int len = h.Length - lead;
            var bytes = new byte[len];
            Array.Copy(h, lead, bytes, 0, len);
            byte[] mask = null;
            if (m != null)
            {
                mask = new byte[len];
                Array.Copy(m, lead, mask, 0, len);
                bool allFixed = true;
                for (int i = 0; i < len; i++) if (mask[i] != 0xFF) { allFixed = false; break; }
                if (allFixed) mask = null;
            }

            var p = new Pat
            {
                Sig = s,
                SigIndex = si,
                Bytes = bytes,
                Mask = mask,
                Skip = Math.Max(0, s.HeaderOffset) + lead
            };
            if (len > 1 && (mask == null || mask[1] == 0xFF)) p.Second = bytes[1];
            if (len > 2 && (mask == null || mask[2] == 0xFF)) p.Third = bytes[2];

            int align = ByteByByteScan ? 1 : Math.Max(1, s.Alignment);
            _maxSkip = Math.Max(_maxSkip, p.Skip);
            _maxPat = Math.Max(_maxPat, len);

            if (!byAlign.TryGetValue(align, out var list)) byAlign[align] = list = new List<Pat>();
            list.Add(p);
        }

        var groups = new List<ScanGroup>();
        foreach (var kv in byAlign)
        {
            var g = new ScanGroup { Align = kv.Key, Bitmap = new byte[8192], Buckets = new Pat[256][] };
            g.AlignMask = (g.Align & (g.Align - 1)) == 0 ? g.Align - 1 : -1;

            var lists = new List<Pat>[256];
            var skips = new SortedSet<int>();

            foreach (var p in kv.Value)
            {
                skips.Add(p.Skip);
                g.MaxSkip = Math.Max(g.MaxSkip, p.Skip);

                byte[] bytes = p.Bytes, mask = p.Mask;
                int len = bytes.Length;
                byte b0 = bytes[0];
                byte m0 = mask == null ? (byte)0xFF : mask[0];
                byte b1 = len > 1 ? bytes[1] : (byte)0x00;
                byte m1 = len > 1 ? (mask == null ? (byte)0xFF : mask[1]) : (byte)0x00;

                for (int v0 = 0; v0 < 256; v0++)
                {
                    if ((v0 & m0) != (b0 & m0)) continue;
                    (lists[v0] ??= new List<Pat>()).Add(p);
                    for (int v1 = 0; v1 < 256; v1++)
                    {
                        if ((v1 & m1) != (b1 & m1)) continue;
                        int v = (v0 << 8) | v1;
                        g.Bitmap[v >> 3] |= (byte)(1 << (v & 7));
                    }
                }
            }

            for (int i = 0; i < 256; i++) g.Buckets[i] = lists[i]?.ToArray();
            g.Skips = new int[skips.Count];
            skips.CopyTo(g.Skips);
            groups.Add(g);
        }

        groups.Sort((a, b) => a.Align.CompareTo(b.Align));
        _groups = groups.ToArray();
    }

    // ---------------- scansione ----------------

    /// <summary>
    /// Cerca le firme fra <paramref name="startByte"/> (compreso) e <paramref name="endByte"/>
    /// (escluso), calcola le lunghezze e risolve le sovrapposizioni.
    /// </summary>
    public List<CarvedFile> Scan(IBlockSource source, long startByte, long endByte,
                                 IProgress<string> progress, CancellationToken ct)
    {
        var result = new List<CarvedFile>();
        if (source == null) return result;

        var sigs = new List<FileSignature>();
        foreach (var s in Signatures ?? SignatureLibrary.All)
            if (s?.Header != null && s.Header.Length > 0) sigs.Add(s);
        if (sigs.Count == 0) return result;

        long total = CarveIO.SourceLength(source);
        if (total <= 0) return result;
        if (startByte < 0) startByte = 0;
        if (endByte <= 0 || endByte > total) endByte = total;
        if (startByte >= endByte) return result;

        BuildIndex(sigs);

        int step = Math.Max(1 << 20, WindowSize);
        int overlap = Math.Max(4096, _maxSkip + _maxPat + 8);
        var buf = new byte[step + overlap];

        var hits = new List<(long Off, int Sig)>();
        var sw = Stopwatch.StartNew();
        long scanned = 0;
        long nextReport = 0;
        long span = endByte - startByte;

        Report(progress, string.Format(CultureInfo.CurrentCulture,
            "Avvio scansione: {0} MB da analizzare, {1} firme attive, finestra {2} MB",
            span >> 20, sigs.Count, step >> 20));

        for (long win = startByte; win < endByte; win += step)
        {
            if (ct.IsCancellationRequested) break;

            int want = (int)Math.Min(buf.Length, total - win);
            if (want <= 0) break;
            int valid = CarveIO.Read(source, win, buf, want);
            if (valid <= 0)
            {
                // zona illeggibile: i byte restano a zero, si tira dritto
                valid = want;
            }

            long windowEnd = Math.Min(win + step, endByte);
            int startLimit = (int)(windowEnd - win);                 // posizioni di inizio ammesse

            ScanWindow(buf, valid, win, startByte, endByte, startLimit, hits);

            scanned += Math.Min(step, endByte - win);

            if (sw.ElapsedMilliseconds >= nextReport && progress != null)
            {
                nextReport = sw.ElapsedMilliseconds + 2000;
                double mb = scanned / 1048576.0;
                double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                Report(progress, string.Format(CultureInfo.CurrentCulture,
                    "Analizzati {0:F0} MB su {1:F0} ({2:F1}%) - {3} candidati - {4:F1} MB/s",
                    mb, span / 1048576.0, 100.0 * scanned / span, hits.Count, mb / sec));
            }

            if (hits.Count >= MaxResults)
            {
                Report(progress, "Raggiunto il tetto dei risultati: scansione interrotta.");
                break;
            }
        }

        sw.Stop();
        LastBytesScanned = scanned;
        LastDuration = sw.Elapsed;
        LastMbPerSecond = scanned / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        LastCandidateCount = hits.Count;

        Report(progress, string.Format(CultureInfo.CurrentCulture,
            "Scansione conclusa: {0} candidati in {1:F1} s ({2:F1} MB/s)",
            hits.Count, LastDuration.TotalSeconds, LastMbPerSecond));

        if (hits.Count == 0) return result;

        hits.Sort((a, b) => a.Off != b.Off ? a.Off.CompareTo(b.Off) : a.Sig.CompareTo(b.Sig));

        var measured = Measure(source, hits, sigs, endByte, progress, ct);
        var final = Resolve(measured, endByte, progress);

        Report(progress, string.Format(CultureInfo.CurrentCulture,
            "Esito: {0} file recuperabili, {1} inclusi in altri file, {2:F0} MB analizzati a {3:F1} MB/s",
            final.Count, LastEmbeddedCount, scanned / 1048576.0, LastMbPerSecond));

        return final;
    }

    private void ScanWindow(byte[] buf, int valid, long winStart, long startByte, long endByte,
                            int startLimit, List<(long, int)> hits)
    {
        foreach (var g in _groups)
        {
            if (g.Align <= 1) ScanFree(g, buf, valid, winStart, startByte, endByte, startLimit, hits);
            else ScanAligned(g, buf, valid, winStart, startByte, endByte, startLimit, hits);
            if (hits.Count >= MaxResults) return;
        }
    }

    /// <summary>Ciclo caldo: filtro a due byte sulla tabella di bit, poi confronto completo.</summary>
    private void ScanFree(ScanGroup g, byte[] buf, int valid, long winStart,
                          long startByte, long endByte, int startLimit, List<(long, int)> hits)
    {
        int scanLimit = Math.Min(valid - 1, startLimit + g.MaxSkip);
        if (scanLimit <= 0) return;

        byte[] bitmap = g.Bitmap;
        var buckets = g.Buckets;
        int v = buf[0];

        for (int j = 0; j < scanLimit; j++)
        {
            v = ((v << 8) | buf[j + 1]) & 0xFFFF;
            if ((bitmap[v >> 3] & (1 << (v & 7))) == 0) continue;

            var bucket = buckets[v >> 8];
            if (bucket == null) continue;

            for (int k = 0; k < bucket.Length; k++)
            {
                var p = bucket[k];
                if (p.Second >= 0 && buf[j + 1] != p.Second) continue;
                if (p.Third >= 0 && (j + 2 >= valid || buf[j + 2] != p.Third)) continue;
                if (TryMatch(p, buf, j, valid, winStart, startByte, endByte, startLimit, hits))
                    if (hits.Count >= MaxResults) return;
            }
        }
    }

    /// <summary>Scansione sui soli confini di allineamento: una posizione ogni 512 o 2048 byte.</summary>
    private void ScanAligned(ScanGroup g, byte[] buf, int valid, long winStart,
                             long startByte, long endByte, int startLimit, List<(long, int)> hits)
    {
        long firstAbs = ((winStart + g.Align - 1) / g.Align) * g.Align;
        int first = (int)(firstAbs - winStart);
        if (first < 0) return;

        byte[] bitmap = g.Bitmap;
        var buckets = g.Buckets;

        foreach (int skip in g.Skips)
        {
            for (int start = first; start < startLimit; start += g.Align)
            {
                int j = start + skip;
                if (j + 1 >= valid) break;

                int v = (buf[j] << 8) | buf[j + 1];
                if ((bitmap[v >> 3] & (1 << (v & 7))) == 0) continue;

                var bucket = buckets[v >> 8];
                if (bucket == null) continue;

                for (int k = 0; k < bucket.Length; k++)
                {
                    var p = bucket[k];
                    if (p.Skip != skip) continue;
                    if (p.Second >= 0 && buf[j + 1] != p.Second) continue;
                    if (p.Third >= 0 && (j + 2 >= valid || buf[j + 2] != p.Third)) continue;
                    if (TryMatch(p, buf, j, valid, winStart, startByte, endByte, startLimit, hits))
                        if (hits.Count >= MaxResults) return;
                }
            }
        }
    }

    /// <summary>Confronto completo del pattern e validazione; aggiunge il candidato se regge.</summary>
    private static bool TryMatch(Pat p, byte[] buf, int j, int valid, long winStart,
                                 long startByte, long endByte, int startLimit, List<(long, int)> hits)
    {
        int startIdx = j - p.Skip;
        if (startIdx < 0 || startIdx >= startLimit) return false;

        long abs = winStart + startIdx;
        if (abs < startByte || abs >= endByte) return false;

        byte[] pb = p.Bytes;
        if (j + pb.Length > valid) return false;

        if (p.Mask == null)
        {
            int i = 1;
            while (i < pb.Length && buf[j + i] == pb[i]) i++;
            if (i != pb.Length) return false;
        }
        else
        {
            byte[] pm = p.Mask;
            int i = 1;
            while (i < pb.Length && (buf[j + i] & pm[i]) == (pb[i] & pm[i])) i++;
            if (i != pb.Length) return false;
        }

        if (p.Sig.Validate != null)
        {
            int avail = valid - startIdx;
            if (avail <= 0) return false;
            bool ok;
            try { ok = p.Sig.Validate(buf, startIdx, avail); }
            catch { ok = false; }
            if (!ok) return false;
        }

        hits.Add((abs, p.SigIndex));
        return true;
    }

    // ---------------- calcolo delle lunghezze ----------------

    private List<CarvedFile> Measure(IBlockSource source, List<(long Off, int Sig)> hits,
                                     List<FileSignature> sigs, long endByte,
                                     IProgress<string> progress, CancellationToken ct)
    {
        var cache = new CachingSource(source, 1 << 20);
        var list = new List<CarvedFile>(hits.Count);
        var sw = Stopwatch.StartNew();
        long nextReport = 2000;

        for (int i = 0; i < hits.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var (off, si) = hits[i];
            var sig = sigs[si];

            long len = 0;
            bool exact = false;

            if (sig.MeasureLength != null)
            {
                long m;
                try { m = sig.MeasureLength(cache, off); }
                catch { m = 0; }
                if (m > 0) { len = m; exact = true; }
                else if (m < 0) len = -m;
            }

            if (len == 0 && sig.Footer != null && sig.Footer.Length > 0)
            {
                long limit = Math.Min(sig.MaxLength, endByte - off);
                long hit;
                try { hit = CarveIO.Find(cache, off, limit, sig.Footer); }
                catch { hit = -1; }
                if (hit >= 0)
                {
                    long end = hit + sig.Footer.Length;
                    if (sig.AbsorbTrailingEol) end += CarveIO.TrailingEol(cache, end);
                    len = end - off;
                    exact = true;
                }
            }

            if (len > sig.MaxLength) { len = sig.MaxLength; exact = false; }
            if (off + len > endByte) { len = endByte - off; exact = false; }
            if (len < 0) len = 0;

            string ext = sig.Extension;
            if (sig.ClassifyExtension != null && len > 0)
            {
                string e = null;
                try { e = sig.ClassifyExtension(cache, off); }
                catch { }
                if (!string.IsNullOrEmpty(e)) ext = e;
            }

            list.Add(new CarvedFile
            {
                Signature = sig,
                Offset = off,
                Length = len,
                LengthIsExact = exact,
                Extension = ext
            });

            if (progress != null && sw.ElapsedMilliseconds >= nextReport)
            {
                nextReport = sw.ElapsedMilliseconds + 2000;
                Report(progress, string.Format(CultureInfo.CurrentCulture,
                    "Calcolo delle lunghezze: {0} di {1} candidati", i + 1, hits.Count));
            }
        }

        return list;
    }

    // ---------------- sovrapposizioni, stime, nomi ----------------

    private List<CarvedFile> Resolve(List<CarvedFile> all, long endByte, IProgress<string> progress)
    {
        LastEmbeddedCount = 0;
        var result = new List<CarvedFile>();
        if (all.Count == 0) return result;

        // stesso offset: vince la priorità più alta, poi la lunghezza esatta, poi la maggiore
        all.Sort((a, b) =>
        {
            int c = a.Offset.CompareTo(b.Offset);
            if (c != 0) return c;
            c = b.Signature.Priority.CompareTo(a.Signature.Priority);
            if (c != 0) return c;
            c = b.LengthIsExact.CompareTo(a.LengthIsExact);
            if (c != 0) return c;
            return b.Length.CompareTo(a.Length);
        });

        var unique = new List<CarvedFile>(all.Count);
        long lastOffset = -1;
        foreach (var f in all)
        {
            if (f.Offset == lastOffset) continue;
            unique.Add(f);
            lastOffset = f.Offset;
        }

        // contenimento: un file che comincia dentro un contenitore a lunghezza esatta è un incluso
        long containerEnd = -1, containerOff = -1;
        var kept = new List<CarvedFile>(unique.Count);

        foreach (var f in unique)
        {
            if (f.Offset > containerOff && f.Offset < containerEnd)
            {
                LastEmbeddedCount++;
                f.IsEmbedded = true;
                f.ContainerOffset = containerOff;
                if (KeepEmbedded) kept.Add(f);
                continue;
            }

            kept.Add(f);
            if (f.LengthIsExact && f.End > containerEnd)
            {
                containerEnd = f.End;
                containerOff = f.Offset;
            }
        }

        // le lunghezze incerte si fermano al file successivo
        long nextStart = endByte;
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            var f = kept[i];
            if (f.IsEmbedded) continue;

            if (!f.LengthIsExact)
            {
                long room = Math.Min(nextStart, endByte) - f.Offset;
                long cap = Math.Min(f.Signature.MaxLength, room);
                if (f.Length <= 0 || f.Length > cap) f.Length = cap;
            }
            if (f.Length > 0) nextStart = f.Offset;
        }

        // soglia minima e nomi progressivi
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in kept)
        {
            long min = f.Signature.MinLength > 0 ? f.Signature.MinLength : MinLength;
            if (f.Length < min) continue;

            string ext = string.IsNullOrEmpty(f.Extension) ? f.Signature.Extension : f.Extension;
            if (string.IsNullOrEmpty(ext)) ext = "bin";
            counters.TryGetValue(ext, out int n);
            counters[ext] = ++n;

            f.SuggestedName = IncludeOffsetInName
                ? string.Format(CultureInfo.InvariantCulture, "{0:D6}_0x{1:X10}.{2}", n, f.Offset, ext)
                : string.Format(CultureInfo.InvariantCulture, "{0:D6}.{1}", n, ext);

            result.Add(f);
        }

        result.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return result;
    }

    // ---------------- estrazione ----------------

    /// <summary>
    /// Copia i byte del file sul flusso di destinazione. Le zone illeggibili diventano zeri,
    /// così la posizione dei dati buoni non si sposta. Ritorna i byte scritti.
    /// </summary>
    public long Extract(IBlockSource source, CarvedFile file, Stream destination, CancellationToken ct)
    {
        if (source == null || file == null || destination == null) return 0;
        if (file.Length <= 0) return 0;

        long total = CarveIO.SourceLength(source);
        long remaining = file.Length;
        if (total > 0 && file.Offset + remaining > total) remaining = total - file.Offset;
        if (remaining <= 0) return 0;

        var buf = new byte[1 << 20];
        long written = 0;

        while (remaining > 0)
        {
            if (ct.IsCancellationRequested) break;

            int want = (int)Math.Min(buf.Length, remaining);
            int got = CarveIO.Read(source, file.Offset + written, buf, want);
            if (got <= 0)
            {
                Array.Clear(buf, 0, want);                           // settori persi: zeri
                got = want;
            }
            else if (got < want)
            {
                Array.Clear(buf, got, want - got);
                got = want;
            }

            destination.Write(buf, 0, got);
            written += got;
            remaining -= got;
        }

        return written;
    }

    /// <summary>Estrae direttamente su file, creando la cartella se serve.</summary>
    public long ExtractToFile(IBlockSource source, CarvedFile file, string path, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        return Extract(source, file, fs, ct);
    }

    private static void Report(IProgress<string> progress, string text)
    {
        try { progress?.Report(text); } catch { }
    }
}
