using DVDRescue.Core;

namespace DVDRescue.Bluray;

/// <summary>Impacchettamento dei Transport Stream: 188 puro, 192 (M2TS) o 204 (con parità).</summary>
public enum TsPacketing
{
    None = 0,
    /// <summary>Transport Stream puro (.ts, DVB, broadcast).</summary>
    Ts188 = 188,
    /// <summary>M2TS: 4 byte di arrival timestamp davanti a ogni pacchetto (.m2ts, .MTS).</summary>
    M2ts192 = 192,
    /// <summary>Transport Stream con 16 byte di codice di correzione in coda.</summary>
    Ts204 = 204
}

/// <summary>Tratto continuo di Transport Stream individuato su un supporto.</summary>
public sealed class TsSegment
{
    /// <summary>Offset in byte del primo pacchetto (per M2TS è l'inizio dei 4 byte di timestamp).</summary>
    public long StartOffset;

    /// <summary>Lunghezza in byte: estraendo questi byte si ottiene un file riproducibile.</summary>
    public long Length;

    /// <summary>Durata in secondi ricavata dai PCR (o dai PTS, se i PCR mancano).</summary>
    public double Seconds;

    /// <summary>188, 192 o 204.</summary>
    public int PacketSize;

    public string VideoCodec;
    public string Resolution;
    public List<string> Audio = new();

    /// <summary>Note in italiano su cosa è stato trovato o su cosa non torna.</summary>
    public List<string> Notes = new();

    /// <summary>True se dentro il segmento c'è stato un tratto illeggibile poi riagganciato.</summary>
    public bool HasGaps;

    public override string ToString()
        => $"@{StartOffset} {Length / 1048576.0:F1} MB, {Seconds:F2}s, {PacketSize}B, " +
           $"{VideoCodec ?? "video ?"} {Resolution ?? ""}".TrimEnd();
}

/// <summary>
/// Riconosce e analizza gli stream MPEG-2 Transport Stream: è il formato di tutti i
/// supporti video ad alta definizione (Blu-ray BDMV, BDAV dei registratori, AVCHD
/// delle videocamere) e di molte registrazioni digitali terrestri.
///
/// Serve per due cose:
///  - leggere e misurare un file .m2ts/.MTS già individuato dal filesystem;
///  - ritrovare il video direttamente sui settori quando il filesystem non c'è più
///    (disco non finalizzato, UDF distrutta, cartelle parziali).
///
/// Criterio di fondo: <b>meglio un segmento lungo che venti frammenti</b>. La
/// frammentazione eccessiva rende il materiale recuperato inutilizzabile, quindi le
/// interruzioni brevi vengono riagganciate e il segmento continua; si taglia solo
/// quando c'è una prova solida di confine (orologio che riparte, tabelle cambiate,
/// buco troppo lungo).
/// </summary>
public sealed class TsCarver
{
    // ------------------------------------------------------------ parametri

    /// <summary>Pacchetti consecutivi allineati richiesti per confermare un aggancio.</summary>
    public int MinPacketsToConfirm { get; set; } = 8;

    /// <summary>Byte non riconosciuti tollerati dentro un segmento prima di chiuderlo.</summary>
    public long MaxGapBytes { get; set; } = 4L << 20;      // 4 MB

    /// <summary>Salto dell'orologio oltre il quale si considera iniziata un'altra registrazione.</summary>
    public double PcrJumpSeconds { get; set; } = 10.0;

    /// <summary>Segmenti più corti di così vengono scartati (agganci spurii, frammenti).</summary>
    public long MinSegmentBytes { get; set; } = 256L << 10;

    /// <summary>Sotto questa soglia <see cref="MeasureDuration"/> scorre tutto il file.</summary>
    public long FullScanBytes { get; set; } = 96L << 20;

    /// <summary>Finestra di stream elementare video analizzata in un colpo solo.</summary>
    public int VideoProbeBytes { get; set; } = 512 << 10;

    /// <summary>
    /// Quanto stream elementare si è disposti a scorrere in cerca dell'intestazione di
    /// sequenza. Se l'aggancio è avvenuto a metà di un GOP l'intestazione arriva solo
    /// al GOP successivo, quindi una finestra sola non basta.
    /// </summary>
    public long VideoScanBudget { get; set; } = 32L << 20;

    private const int ChunkBytes = 4 << 20;
    private const int MaxPacket = 204;
    private const int ExtendRun = 64;

    /// <summary>PCR e PTS girano su 33 bit; il PCR completo è base*300 + estensione a 27 MHz.</summary>
    private const long PcrModulo = (1L << 33) * 300;
    private const long PtsModulo = 1L << 33;

    // ------------------------------------------------------------ riconoscimento

    /// <summary>
    /// Riconosce l'impacchettamento cercando il sync byte 0x47 ripetuto con passo
    /// costante. Servono <see cref="MinPacketsToConfirm"/> pacchetti consecutivi
    /// allineati: un solo 0x47 non dice nulla, su dati casuali ce n'è uno ogni 256 byte.
    /// Il buffer deve contenere almeno una decina di pacchetti (≈ 2 KB) perché la
    /// conferma sia possibile.
    /// </summary>
    public TsPacketing DetectPacketing(byte[] buffer, int offset, int length)
    {
        int size = FindLock(buffer, offset, length, out _);
        return size switch
        {
            188 => TsPacketing.Ts188,
            192 => TsPacketing.M2ts192,
            204 => TsPacketing.Ts204,
            _ => TsPacketing.None
        };
    }

    private static readonly int[] Sizes = { 188, 192, 204 };

    /// <summary>
    /// Cerca il primo aggancio nel buffer. Ritorna la dimensione del pacchetto (0 se
    /// niente) e in <paramref name="unitStart"/> l'indice dell'inizio dell'unità:
    /// per M2TS è 4 byte prima del sync, perché l'unità comprende il timestamp.
    /// </summary>
    private int FindLock(byte[] buffer, int offset, int length, out int unitStart)
    {
        unitStart = -1;
        if (buffer == null) return 0;

        int need = Math.Max(2, MinPacketsToConfirm);
        int end = offset + length;
        if (offset < 0 || length < 0 || end > buffer.Length) return 0;

        for (int p = offset; p <= end - need * 188; p++)
        {
            if (buffer[p] != 0x47) continue;

            int best = 0, bestRun = 0;
            foreach (int s in Sizes)
            {
                if (p + (need - 1) * s >= end) continue;
                int run = AlignedRun(buffer, p, end, s, need);
                if (run >= need && run > bestRun) { bestRun = run; best = s; }
            }

            if (best == 0) continue;

            // per M2TS l'unità comincia 4 byte prima: se non ci stanno, salta al pacchetto dopo
            int start = best == 192 ? p - 4 : p;
            if (start < offset) start += best;
            unitStart = start;
            return best;
        }

        return 0;
    }

    /// <summary>Come <see cref="FindLock"/> ma vincolato a un passo già noto.</summary>
    private int FindLockOfSize(byte[] buffer, int offset, int length, int size, out int unitStart)
    {
        unitStart = -1;
        int need = Math.Max(2, MinPacketsToConfirm);
        int end = offset + length;
        if (buffer == null || offset < 0 || end > buffer.Length) return -1;

        for (int p = offset; p <= end - need * size; p++)
        {
            if (buffer[p] != 0x47) continue;
            if (AlignedRun(buffer, p, end, size, need) < need) continue;

            int start = size == 192 ? p - 4 : p;
            if (start < offset) start += size;
            unitStart = start;
            return start;
        }
        return -1;
    }

    /// <summary>
    /// Quanti pacchetti consecutivi si agganciano a partire da <paramref name="p"/> con
    /// passo <paramref name="size"/>. Oltre al sync byte controlla i campi che non
    /// possono avere certi valori, così i dati strutturati (bitmap, archivi, zone
    /// piene di 0x47) non passano per Transport Stream.
    /// </summary>
    private static int AlignedRun(byte[] b, int p, int end, int size, int need)
    {
        int run = 0, bad = 0;
        for (int k = 0; k < ExtendRun; k++)
        {
            int q = p + k * size;
            if (q + 4 > end) break;
            if (b[q] != 0x47) break;

            // transport_error_indicator acceso o adaptation_field_control = 0 (riservato)
            if ((b[q + 1] & 0x80) != 0 || ((b[q + 3] >> 4) & 0x03) == 0) bad++;
            run++;
        }

        if (run < need) return run;
        // tolleriamo qualche pacchetto strano, ma non una maggioranza
        return bad * 4 > run ? 0 : run;
    }

    // ------------------------------------------------------------ scansione

    /// <summary>
    /// Percorre la sorgente da <paramref name="startByte"/> a <paramref name="endByte"/>
    /// (escluso) e ritorna i tratti di Transport Stream trovati, uno per registrazione.
    /// Si taglia solo su prove concrete di confine: sync perso per oltre
    /// <see cref="MaxGapBytes"/>, orologio che salta oltre <see cref="PcrJumpSeconds"/>,
    /// oppure PMT sostituita da un'altra diversa (confermata due volte).
    /// </summary>
    public List<TsSegment> Scan(IBlockSource source, long startByte, long endByte,
                                IProgress<string> progress, CancellationToken ct)
    {
        var result = new List<TsSegment>();
        if (source == null) return result;

        long limit = source.Length > 0 ? source.Length : long.MaxValue;
        if (startByte < 0) startByte = 0;
        if (endByte <= 0 || endByte > limit) endByte = limit;
        if (endByte <= startByte) return result;

        int lockNeed = Math.Max(2, MinPacketsToConfirm) * MaxPacket + 64;
        var w = new Window(source, startByte, endByte, Math.Max(ChunkBytes, lockNeed * 4));

        Seg cur = null;
        long nextReport = startByte;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (progress != null && w.Abs >= nextReport)
                {
                    nextReport = w.Abs + (64L << 20);
                    int found = result.Count + (cur != null ? 1 : 0);
                    progress.Report($"Analisi Transport Stream — {w.Abs / 1048576.0:F0} MB, " +
                                    $"{found} segment{(found == 1 ? "o" : "i")}");
                }

                // ---- fuori da un segmento: cerca l'aggancio
                if (cur == null)
                {
                    if (!w.Ensure(lockNeed)) break;

                    int size = FindLock(w.Buffer, w.Cursor, w.Available, out int unit);
                    if (size == 0)
                    {
                        int adv = Math.Max(1, w.Available - lockNeed);
                        w.Skip(adv);
                        continue;
                    }

                    w.Skip(unit - w.Cursor);
                    cur = new Seg(w.Abs, size, this);
                    continue;
                }

                // ---- dentro un segmento: consuma pacchetto per pacchetto
                int ps = cur.PacketSize;
                int so = ps == 192 ? 4 : 0;

                if (!w.Ensure(ps))
                {
                    Close(result, cur, cur.LastPacketEnd > 0 ? cur.LastPacketEnd : w.Abs);
                    cur = null;
                    break;
                }

                int s = w.Cursor + so;
                if (w.Buffer[s] == 0x47)
                {
                    long unitAbs = w.Abs;
                    var action = cur.Feed(w.Buffer, s, unitAbs, ps);

                    if (action != Split.No)
                    {
                        long hint = cur.BoundaryHint(unitAbs);
                        long closeAt = action == Split.Here ? hint : cur.GapStart;
                        long restart = action == Split.AtGap ? cur.GapEnd : hint;
                        Close(result, cur, closeAt);
                        cur = new Seg(restart, ps, this);
                        cur.Feed(w.Buffer, s, unitAbs, ps);
                    }

                    w.Skip(ps);
                    continue;
                }

                // ---- sync perso: prima di tagliare si prova a riagganciare
                long gapStart = w.Abs;
                long searched = 0;
                bool relocked = false;

                while (searched <= MaxGapBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!w.Ensure(lockNeed)) break;

                    if (FindLockOfSize(w.Buffer, w.Cursor, w.Available, ps, out int unit2) >= 0)
                    {
                        w.Skip(unit2 - w.Cursor);
                        relocked = true;
                        break;
                    }

                    int adv = Math.Max(1, w.Available - lockNeed);
                    w.Skip(adv);
                    searched += adv;
                }

                if (!relocked)
                {
                    Close(result, cur, gapStart);
                    cur = null;
                    continue;
                }

                cur.GapStart = gapStart;
                cur.GapEnd = w.Abs;
                cur.PendingResync = true;
                cur.HasGaps = true;
            }

            if (cur != null)
                Close(result, cur, cur.LastPacketEnd > 0 ? cur.LastPacketEnd : endByte);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // dati corrotti: si tiene quello che si è già trovato
            progress?.Report("Analisi interrotta da dati non validi: " + ex.Message);
            if (cur != null) Close(result, cur, cur.LastPacketEnd > 0 ? cur.LastPacketEnd : w.Abs);
        }

        return result;
    }

    private void Close(List<TsSegment> into, Seg seg, long endAbs)
    {
        if (seg == null) return;
        var s = seg.Build(endAbs);
        if (s != null && s.Length >= MinSegmentBytes) into.Add(s);
    }

    // ------------------------------------------------------------ durata

    /// <summary>
    /// Durata in secondi di un tratto di Transport Stream, dai PCR (27 MHz) con
    /// gestione del giro di boa del contatore a 33 bit. Se i PCR mancano ripiega sui
    /// PTS del primo stream video. Per i tratti oltre <see cref="FullScanBytes"/> legge
    /// solo la testa e la coda: veloce, ma se dentro c'è una discontinuità non la vede.
    /// Ritorna 0 se non si ricava nulla.
    /// </summary>
    public double MeasureDuration(IBlockSource source, long startByte, long length, int packetSize)
    {
        if (source == null || length <= 0) return 0;

        try
        {
            if (packetSize != 188 && packetSize != 192 && packetSize != 204)
            {
                packetSize = (int)DetectPacketingAt(source, startByte, length);
                if (packetSize == 0) return 0;
            }

            if (length <= FullScanBytes)
                return ProbeRange(source, startByte, length, packetSize).Duration();

            // testa e coda: bastano il primo e l'ultimo PCR
            long win = Math.Min(16L << 20, length / 2);
            var head = ProbeRange(source, startByte, win, packetSize);

            long tailStart = startByte + length - win;
            // riallinea la coda su un confine di pacchetto rispetto all'inizio
            long delta = tailStart - startByte;
            tailStart = startByte + delta / packetSize * packetSize;
            var tail = ProbeRange(source, tailStart, win, packetSize);

            if (head.FirstPcr >= 0 && tail.LastPcr >= 0)
            {
                double d = Norm(tail.LastPcr - head.FirstPcr, PcrModulo) / 27_000_000.0;
                if (d > 0 && d < 24 * 3600) return d;
            }
            if (head.FirstPts >= 0 && tail.LastPts >= 0)
            {
                double d = Norm(tail.LastPts - head.FirstPts, PtsModulo) / 90_000.0;
                if (d > 0 && d < 24 * 3600) return d;
            }
            return head.Duration();
        }
        catch { return 0; }
    }

    /// <summary>
    /// Analizza un tratto già individuato (tipicamente un file .m2ts/.MTS) e ne ricava
    /// durata, codec, risoluzione e tracce audio. Non taglia in segmenti.
    /// </summary>
    public TsSegment Analyze(IBlockSource source, long startByte, long length, int packetSize)
    {
        var seg = new TsSegment { StartOffset = startByte, Length = length, PacketSize = packetSize };
        if (source == null || length <= 0) return seg;

        try
        {
            if (packetSize != 188 && packetSize != 192 && packetSize != 204)
            {
                packetSize = (int)DetectPacketingAt(source, startByte, length);
                if (packetSize == 0)
                {
                    seg.Notes.Add("Nessun Transport Stream riconosciuto all'inizio del file.");
                    return seg;
                }
            }
            seg.PacketSize = packetSize;

            // la testa basta per codec e risoluzione; la durata richiede tutto
            var probe = ProbeRange(source, startByte, Math.Min(length, FullScanBytes), packetSize);
            probe.Describe(seg);

            seg.Seconds = length <= FullScanBytes
                ? probe.Duration()
                : MeasureDuration(source, startByte, length, packetSize);
        }
        catch (Exception ex)
        {
            seg.Notes.Add("Analisi incompleta: " + ex.Message);
        }
        return seg;
    }

    /// <summary>Riconosce l'impacchettamento leggendo l'inizio di un tratto della sorgente.</summary>
    public TsPacketing DetectPacketingAt(IBlockSource source, long startByte, long length)
    {
        if (source == null) return TsPacketing.None;
        int want = (int)Math.Min(length <= 0 ? 64 << 10 : length, 256 << 10);
        if (want < 64) return TsPacketing.None;

        var buf = new byte[want];
        int got = source.ReadBytes(startByte, want, buf, 0);
        if (got <= 0) return TsPacketing.None;
        return DetectPacketing(buf, 0, got);
    }

    private static long Norm(long delta, long modulo)
    {
        if (delta < 0) delta += modulo;
        return delta;
    }

    /// <summary>Legge un tratto e lo dà in pasto a un <see cref="Seg"/> usato come sonda.</summary>
    private Seg ProbeRange(IBlockSource source, long startByte, long length, int packetSize)
    {
        var seg = new Seg(startByte, packetSize, this);
        int so = packetSize == 192 ? 4 : 0;

        var buf = new byte[Math.Max(ChunkBytes, packetSize * 64)];
        long pos = startByte;
        long end = startByte + length;
        bool aligned = false;

        while (pos < end)
        {
            int want = (int)Math.Min(buf.Length, end - pos);
            want -= want % packetSize;
            if (want < packetSize) break;

            int got = source.ReadBytes(pos, want, buf, 0);
            if (got < packetSize) break;
            got -= got % packetSize;

            int p = 0;
            if (!aligned)
            {
                // il tratto potrebbe non cominciare su un confine di pacchetto
                if (FindLockOfSize(buf, 0, got, packetSize, out int unit) < 0) { pos += got; continue; }
                p = unit;
                seg.Start = pos + unit;
                aligned = true;
            }

            for (; p + packetSize <= got; p += packetSize)
            {
                int s = p + so;
                if (buf[s] != 0x47) continue;                 // dentro la sonda si va avanti comunque
                seg.Feed(buf, s, pos + p, packetSize);
            }

            pos += got;
        }

        return seg;
    }

    // ------------------------------------------------------------ finestra scorrevole

    /// <summary>
    /// Buffer scorrevole sopra una <see cref="IBlockSource"/>: garantisce un minimo di
    /// byte disponibili davanti al cursore, così l'aggancio non si perde a cavallo dei
    /// blocchi letti.
    /// </summary>
    private sealed class Window
    {
        private readonly IBlockSource _src;
        private readonly long _end;
        private readonly byte[] _buf;
        private long _bufStart;
        private int _fill;
        private int _cursor;
        private bool _eof;

        public Window(IBlockSource src, long start, long end, int capacity)
        {
            _src = src; _end = end;
            _buf = new byte[capacity];
            _bufStart = start;
        }

        public byte[] Buffer => _buf;
        public int Cursor => _cursor;
        public int Available => _fill - _cursor;
        public long Abs => _bufStart + _cursor;

        public bool Ensure(int need)
        {
            if (Available >= need) return true;
            if (_eof) return Available >= need;

            if (_cursor > 0)
            {
                int keep = Available;
                if (keep > 0) Array.Copy(_buf, _cursor, _buf, 0, keep);
                _bufStart += _cursor;
                _fill = keep;
                _cursor = 0;
            }

            while (_fill < _buf.Length)
            {
                long from = _bufStart + _fill;
                if (from >= _end) { _eof = true; break; }

                int want = (int)Math.Min(_buf.Length - _fill, _end - from);
                if (want <= 0) { _eof = true; break; }

                int got = _src.ReadBytes(from, want, _buf, _fill);
                if (got < want)
                {
                    // zona illeggibile: si azzera e si tira dritto (niente 0x47 → buco)
                    if (got < 0) got = 0;
                    Array.Clear(_buf, _fill + got, want - got);
                }
                _fill += want;
            }

            return Available >= need;
        }

        public void Skip(int n)
        {
            if (n <= 0) return;
            _cursor += n;
            if (_cursor > _fill) _cursor = _fill;
        }
    }

    // ------------------------------------------------------------ stato di un segmento

    private enum Split { No, Here, AtGap }

    /// <summary>
    /// Stato accumulato mentre si percorre un segmento: orologio, tabelle PAT/PMT,
    /// inizio dello stream video per la risoluzione.
    /// </summary>
    private sealed class Seg
    {
        private readonly TsCarver _o;

        public long Start;
        public readonly int PacketSize;
        public long LastPacketEnd;

        public bool PendingResync;
        public long GapStart, GapEnd;
        public bool HasGaps;

        public long FirstPcr = -1, LastPcr = -1;
        public long FirstPts = -1, LastPts = -1;
        private long _prevPcr = -1;
        private double _accum;              // secondi accumulati sui salti sani

        private int _pcrPid = -1;
        private int _videoPid = -1, _videoType = -1;
        private readonly Dictionary<int, int> _streams = new();
        private readonly Dictionary<int, SectionBuf> _sections = new();
        private readonly HashSet<int> _pmtPids = new();
        private string _signature = "";
        private string _pendingSig;
        private int _pendingSigSeen;

        private byte[] _es;
        private int _esLen;
        private long _esScanned;
        private bool _esDone;
        private string _resolution;

        /// <summary>Offset dell'ultima PAT vista: una registrazione nuova comincia quasi sempre lì.</summary>
        public long LastPatAbs = -1;

        public Seg(long start, int packetSize, TsCarver owner)
        {
            Start = start; PacketSize = packetSize; _o = owner;
            _sections[0x0000] = new SectionBuf();
        }

        // -------------------------------------------------------- un pacchetto

        public Split Feed(byte[] b, int s, long unitAbs, int packetSize)
        {
            LastPacketEnd = unitAbs + packetSize;
            var split = Split.No;

            try
            {
                int pid = ((b[s + 1] & 0x1F) << 8) | b[s + 2];
                int afc = (b[s + 3] >> 4) & 0x03;
                if (afc == 0) return split;                   // valore riservato: pacchetto da buttare
                int payload = s + 4;
                int endPk = s + 188;

                if (afc == 2 || afc == 3)
                {
                    int afLen = b[s + 4];
                    if (afLen > 183) return split;
                    if (afLen >= 7)
                    {
                        byte flags = b[s + 5];
                        if ((flags & 0x10) != 0 && s + 12 <= endPk)
                        {
                            long baseV = ((long)b[s + 6] << 25) | ((long)b[s + 7] << 17) |
                                         ((long)b[s + 8] << 9) | ((long)b[s + 9] << 1) |
                                         (long)((b[s + 10] >> 7) & 1);
                            int ext = ((b[s + 10] & 0x01) << 8) | b[s + 11];
                            long pcr = baseV * 300 + ext;
                            if (_pcrPid < 0) _pcrPid = pid;
                            if (pid == _pcrPid) split = OnPcr(pcr);
                        }
                    }
                    payload = s + 5 + afLen;
                }
                if (afc == 2 || payload >= endPk) return split;

                bool pusi = (b[s + 1] & 0x40) != 0;

                if (pid == 0x0000)
                {
                    if (pusi) LastPatAbs = unitAbs;
                    OnSection(0x0000, b, payload, endPk, pusi, ParsePat);
                    return split;
                }

                if (_pmtPids.Contains(pid))
                {
                    OnSection(pid, b, payload, endPk, pusi, ParsePmt);
                    if (_pendingSigSeen >= 2 && _signature.Length > 0 && _pendingSig != _signature)
                    {
                        _signature = _pendingSig;
                        _pendingSig = null; _pendingSigSeen = 0;
                        return Split.Here;
                    }
                    return split;
                }

                if (pid == _videoPid) OnVideoPayload(b, payload, endPk, pusi);
            }
            catch { /* dati corrotti: il pacchetto si butta, il segmento continua */ }

            return split;
        }

        /// <summary>
        /// Dove tagliare quando l'orologio salta: se poco prima c'era una PAT si taglia
        /// lì, perché una registrazione nuova comincia praticamente sempre con la PAT.
        /// </summary>
        public long BoundaryHint(long unitAbs)
            => LastPatAbs > Start && unitAbs - LastPatAbs <= 64L * PacketSize ? LastPatAbs : unitAbs;

        private Split OnPcr(long pcr)
        {
            if (FirstPcr < 0) FirstPcr = pcr;
            LastPcr = pcr;

            if (_prevPcr >= 0)
            {
                long d = pcr - _prevPcr;
                if (d < 0) d += PcrModulo;                    // giro di boa dei 33 bit
                double sec = d / 27_000_000.0;

                if (sec > _o.PcrJumpSeconds)
                {
                    // orologio ripartito: è un'altra registrazione
                    var where = PendingResync ? Split.AtGap : Split.Here;
                    PendingResync = false;
                    _prevPcr = pcr;
                    return where;
                }
                _accum += sec;
            }

            PendingResync = false;
            _prevPcr = pcr;
            return Split.No;
        }

        // -------------------------------------------------------- sezioni PSI

        private sealed class SectionBuf
        {
            public byte[] Data = new byte[1024];
            public int Len;
            public int Want = -1;
        }

        private void OnSection(int pid, byte[] b, int from, int to, bool pusi, Action<byte[], int> parse)
        {
            if (!_sections.TryGetValue(pid, out var sb)) { sb = new SectionBuf(); _sections[pid] = sb; }

            int p = from;
            if (pusi)
            {
                if (p >= to) return;
                int ptr = b[p++];
                p += ptr;
                if (p >= to) return;
                sb.Len = 0;
                sb.Want = -1;
            }
            else if (sb.Want < 0) return;                     // sezione iniziata prima del nostro aggancio

            int n = to - p;
            if (n <= 0) return;
            if (sb.Len + n > sb.Data.Length) n = sb.Data.Length - sb.Len;
            if (n <= 0) { sb.Want = -1; return; }

            Array.Copy(b, p, sb.Data, sb.Len, n);
            sb.Len += n;

            if (sb.Want < 0)
            {
                if (sb.Len < 3) return;
                sb.Want = (((sb.Data[1] & 0x0F) << 8) | sb.Data[2]) + 3;
                if (sb.Want < 8 || sb.Want > sb.Data.Length) { sb.Want = -1; sb.Len = 0; return; }
            }

            if (sb.Len >= sb.Want)
            {
                try { parse(sb.Data, sb.Want); } catch { }
                sb.Len = 0;
                sb.Want = -1;
            }
        }

        private void ParsePat(byte[] d, int len)
        {
            if (d[0] != 0x00 || len < 12) return;
            int end = len - 4;                                 // via il CRC
            for (int p = 8; p + 4 <= end; p += 4)
            {
                int prog = (d[p] << 8) | d[p + 1];
                int pmt = ((d[p + 2] & 0x1F) << 8) | d[p + 3];
                if (prog == 0 || pmt == 0 || pmt == 0x1FFF) continue;
                if (_pmtPids.Add(pmt)) _sections[pmt] = new SectionBuf();
            }
        }

        private void ParsePmt(byte[] d, int len)
        {
            if (d[0] != 0x02 || len < 16) return;
            int end = len - 4;

            int pcrPid = ((d[8] & 0x1F) << 8) | d[9];
            int infoLen = ((d[10] & 0x0F) << 8) | d[11];
            int p = 12 + infoLen;
            if (p > end) return;

            var streams = new List<(int Pid, int Type)>();
            while (p + 5 <= end)
            {
                int type = d[p];
                int pid = ((d[p + 1] & 0x1F) << 8) | d[p + 2];
                int esLen = ((d[p + 3] & 0x0F) << 8) | d[p + 4];
                p += 5 + esLen;
                if (p > end) break;
                streams.Add((pid, type));
            }
            if (streams.Count == 0) return;

            var sig = string.Join(",", streams.Select(x => $"{x.Pid:X}:{x.Type:X}")) + $"|p{pcrPid:X}";

            if (_signature.Length == 0)
            {
                Apply(streams, pcrPid);
                _signature = sig;
                return;
            }

            if (sig == _signature) { _pendingSig = null; _pendingSigSeen = 0; return; }

            // cambio di PMT: si conferma due volte prima di tagliare, la corruzione inganna
            if (sig == _pendingSig) _pendingSigSeen++;
            else { _pendingSig = sig; _pendingSigSeen = 1; }
        }

        private void Apply(List<(int Pid, int Type)> streams, int pcrPid)
        {
            _streams.Clear();
            foreach (var st in streams) _streams[st.Pid] = st.Type;
            if (pcrPid != 0x1FFF && pcrPid != 0) _pcrPid = pcrPid;

            foreach (var st in streams)
            {
                if (!IsVideo(st.Type)) continue;
                _videoPid = st.Pid; _videoType = st.Type;
                break;
            }
        }

        // -------------------------------------------------------- video

        private void OnVideoPayload(byte[] b, int from, int to, bool pusi)
        {
            int p = from;
            if (pusi)
            {
                // inizio PES: 00 00 01 <stream_id>, poi si salta l'intestazione
                if (p + 9 > to) return;
                if (b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1) return;
                int hdr = b[p + 8];
                int flags = b[p + 7];
                if ((flags & 0x80) != 0 && p + 14 <= to)
                {
                    long pts = ((long)(b[p + 9] >> 1) & 0x07) << 30
                             | (long)b[p + 10] << 22
                             | ((long)b[p + 11] >> 1) << 15
                             | (long)b[p + 12] << 7
                             | ((long)b[p + 13] >> 1);
                    if (FirstPts < 0) FirstPts = pts;
                    LastPts = pts;
                }
                p += 9 + hdr;
                if (p >= to) return;
            }

            if (_esDone) return;
            if (_es == null) _es = new byte[Math.Max(64 << 10, _o.VideoProbeBytes)];

            // finestra scorrevole: quando si riempie la si analizza e si ricicla,
            // tenendo in coda qualche KB perché un'intestazione a cavallo non si perda
            int n = to - p;
            while (n > 0)
            {
                int room = _es.Length - _esLen;
                if (room <= 0)
                {
                    TryResolution();
                    if (_resolution != null) { _esDone = true; return; }

                    _esScanned += _esLen;
                    if (_esScanned >= _o.VideoScanBudget) { _esDone = true; return; }

                    int keep = Math.Min(_esLen, 4096);
                    Array.Copy(_es, _esLen - keep, _es, 0, keep);
                    _esLen = keep;
                    room = _es.Length - _esLen;
                }

                int take = Math.Min(room, n);
                Array.Copy(b, p, _es, _esLen, take);
                _esLen += take; p += take; n -= take;
            }
        }

        private void TryResolution()
        {
            if (_resolution != null || _es == null) return;
            // MPEG-1/2 dal sequence header, H.264 dallo SPS.
            // HEVC e VC-1 non sono analizzati: il codec si sa dalla PMT, la risoluzione no.
            _resolution = _videoType == 0x1B
                ? VideoInfo.H264Resolution(_es, _esLen)
                : _videoType == 0x24 || _videoType == 0xEA
                    ? null
                    : VideoInfo.Mpeg2Resolution(_es, _esLen);
        }

        // -------------------------------------------------------- risultato

        public double Duration()
        {
            if (_accum > 0) return _accum;
            if (FirstPcr >= 0 && LastPcr >= 0)
            {
                double d = Norm(LastPcr - FirstPcr, PcrModulo) / 27_000_000.0;
                if (d > 0 && d < 24 * 3600) return d;
            }
            if (FirstPts >= 0 && LastPts >= 0)
            {
                double d = Norm(LastPts - FirstPts, PtsModulo) / 90_000.0;
                if (d > 0 && d < 24 * 3600) return d;
            }
            return 0;
        }

        public void Describe(TsSegment seg)
        {
            TryResolution();
            seg.PacketSize = PacketSize;
            seg.VideoCodec = _videoType >= 0 ? VideoInfo.StreamName(_videoType) : null;
            seg.Resolution = _resolution;
            seg.HasGaps = HasGaps;

            foreach (var kv in _streams.OrderBy(k => k.Key))
                if (VideoInfo.IsAudio(kv.Value))
                    seg.Audio.Add($"{VideoInfo.StreamName(kv.Value)} (PID 0x{kv.Key:X4})");

            if (_videoType < 0)
                seg.Notes.Add("PMT non trovata: codec video non identificato.");
            else if (_videoType == 0x24 || _videoType == 0xEA)
                seg.Notes.Add($"Risoluzione {VideoInfo.StreamName(_videoType)} non ricavata dallo stream: " +
                              "va letta con ffprobe.");
            else if (_resolution == null)
                seg.Notes.Add("Risoluzione non ricavata dallo stream elementare.");
            if (FirstPcr < 0) seg.Notes.Add("Nessun PCR: durata stimata dai PTS.");
        }

        public TsSegment Build(long endAbs)
        {
            long len = endAbs - Start;
            if (len <= 0) return null;
            len -= len % PacketSize;
            if (len <= 0) return null;

            var seg = new TsSegment { StartOffset = Start, Length = len, Seconds = Duration() };
            Describe(seg);
            if (HasGaps) seg.Notes.Add("Il segmento contiene un tratto illeggibile riagganciato.");
            return seg;
        }

        private static bool IsVideo(int t) => VideoInfo.IsVideo(t);
    }

    // ------------------------------------------------------------ codec e risoluzione

    /// <summary>Nomi dei tipi di stream e lettura della risoluzione dallo stream elementare.</summary>
    internal static class VideoInfo
    {
        public static bool IsVideo(int t) =>
            t == 0x01 || t == 0x02 || t == 0x10 || t == 0x1B || t == 0x20 ||
            t == 0x24 || t == 0x42 || t == 0xD1 || t == 0xEA;

        public static bool IsAudio(int t) =>
            t == 0x03 || t == 0x04 || t == 0x0F || t == 0x11 || t == 0x1C ||
            t == 0x80 || t == 0x81 || t == 0x82 || t == 0x83 || t == 0x84 ||
            t == 0x85 || t == 0x86 || t == 0xA1 || t == 0xA2;

        public static string StreamName(int t) => t switch
        {
            0x01 => "MPEG-1 Video",
            0x02 => "MPEG-2 Video",
            0x03 => "MPEG-1 Audio",
            0x04 => "MPEG-2 Audio",
            0x0F => "AAC",
            0x10 => "MPEG-4 Visual",
            0x11 => "AAC LATM",
            0x1B => "H.264/AVC",
            0x1C => "LPCM",
            0x20 => "H.264 MVC",
            0x24 => "HEVC",
            0x42 => "AVS",
            0x80 => "LPCM",
            0x81 => "AC-3",
            0x82 => "DTS",
            0x83 => "Dolby TrueHD",
            0x84 => "Dolby Digital Plus",
            0x85 => "DTS-HD",
            0x86 => "DTS-HD Master Audio",
            0x90 => "Sottotitoli PGS",
            0x91 => "Menu interattivo",
            0x92 => "Sottotitoli testo",
            0xA1 => "Dolby Digital Plus (secondario)",
            0xA2 => "DTS-HD (secondario)",
            0xD1 => "Dirac",
            0xEA => "VC-1",
            _ => $"tipo 0x{t:X2}"
        };

        /// <summary>Risoluzione dal sequence header MPEG-2 (00 00 01 B3).</summary>
        public static string Mpeg2Resolution(byte[] es, int len)
        {
            if (es == null) return null;
            for (int i = 0; i + 7 < len; i++)
            {
                if (es[i] != 0 || es[i + 1] != 0 || es[i + 2] != 1 || es[i + 3] != 0xB3) continue;
                int w = (es[i + 4] << 4) | (es[i + 5] >> 4);
                int h = ((es[i + 5] & 0x0F) << 8) | es[i + 6];
                if (w >= 16 && w <= 8192 && h >= 16 && h <= 8192) return $"{w}x{h}";
            }
            return null;
        }

        /// <summary>Risoluzione dallo SPS H.264 (NAL di tipo 7), ritaglio compreso.</summary>
        public static string H264Resolution(byte[] es, int len)
        {
            if (es == null) return null;
            for (int i = 0; i + 5 < len; i++)
            {
                if (es[i] != 0 || es[i + 1] != 0 || es[i + 2] != 1) continue;
                int nal = es[i + 3];
                if ((nal & 0x80) != 0 || (nal & 0x1F) != 7) continue;

                int start = i + 4;
                int stop = start;
                while (stop + 2 < len && !(es[stop] == 0 && es[stop + 1] == 0 &&
                                           (es[stop + 2] == 1 || es[stop + 2] == 0))) stop++;
                if (stop <= start) continue;

                var r = ParseSps(es, start, Math.Min(stop + 2, len));
                if (r != null) return r;
            }
            return null;
        }

        private static string ParseSps(byte[] b, int from, int to)
        {
            try
            {
                // via i byte anti-emulazione 00 00 03
                var rbsp = new byte[to - from];
                int n = 0, zeros = 0;
                for (int i = from; i < to; i++)
                {
                    byte v = b[i];
                    if (zeros == 2 && v == 3) { zeros = 0; continue; }
                    zeros = v == 0 ? zeros + 1 : 0;
                    rbsp[n++] = v;
                }

                var r = new Bits(rbsp, n);
                int profile = r.U(8);
                r.U(8);                       // constraint flags + reserved
                r.U(8);                       // level_idc
                r.Ue();                       // seq_parameter_set_id

                int chroma = 1;
                if (profile == 100 || profile == 110 || profile == 122 || profile == 244 ||
                    profile == 44 || profile == 83 || profile == 86 || profile == 118 ||
                    profile == 128 || profile == 138 || profile == 139 || profile == 134 ||
                    profile == 135)
                {
                    chroma = r.Ue();
                    if (chroma == 3) r.U(1);
                    r.Ue(); r.Ue();           // bit depth luma / chroma
                    r.U(1);                   // qpprime_y_zero_transform_bypass_flag
                    if (r.U(1) == 1)          // seq_scaling_matrix_present_flag
                    {
                        int lists = chroma != 3 ? 8 : 12;
                        for (int i = 0; i < lists; i++)
                            if (r.U(1) == 1) SkipScalingList(r, i < 6 ? 16 : 64);
                    }
                }

                r.Ue();                       // log2_max_frame_num_minus4
                int poc = r.Ue();
                if (poc == 0) r.Ue();
                else if (poc == 1)
                {
                    r.U(1); r.Se(); r.Se();
                    int cyc = r.Ue();
                    if (cyc > 256) return null;
                    for (int i = 0; i < cyc; i++) r.Se();
                }

                r.Ue();                       // max_num_ref_frames
                r.U(1);                       // gaps_in_frame_num_value_allowed_flag

                int wMbs = r.Ue() + 1;
                int hUnits = r.Ue() + 1;
                int frameMbsOnly = r.U(1);
                if (frameMbsOnly == 0) r.U(1);
                r.U(1);                       // direct_8x8_inference_flag

                int width = wMbs * 16;
                int height = (2 - frameMbsOnly) * hUnits * 16;

                if (r.U(1) == 1)              // frame_cropping_flag
                {
                    int cl = r.Ue(), cr = r.Ue(), ctp = r.Ue(), cb = r.Ue();
                    int subW = chroma == 0 || chroma == 3 ? 1 : 2;
                    int subH = chroma == 0 || chroma == 2 || chroma == 3 ? 1 : 2;
                    int unitX = subW;
                    int unitY = subH * (2 - frameMbsOnly);
                    width -= unitX * (cl + cr);
                    height -= unitY * (ctp + cb);
                }

                if (r.Overrun) return null;
                if (width < 16 || width > 8192 || height < 16 || height > 8192) return null;
                return $"{width}x{height}";
            }
            catch { return null; }
        }

        private static void SkipScalingList(Bits r, int size)
        {
            int last = 8, next = 8;
            for (int i = 0; i < size; i++)
            {
                if (next != 0) { int delta = r.Se(); next = (last + delta + 256) % 256; }
                last = next == 0 ? last : next;
                if (r.Overrun) return;
            }
        }

        /// <summary>Lettore di bit con Exp-Golomb, difensivo sui limiti.</summary>
        private sealed class Bits
        {
            private readonly byte[] _b;
            private readonly int _len;
            private int _pos;
            public bool Overrun;

            public Bits(byte[] b, int len) { _b = b; _len = len; }

            public int U(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++)
                {
                    int byteIx = _pos >> 3;
                    if (byteIx >= _len) { Overrun = true; return v; }
                    v = (v << 1) | ((_b[byteIx] >> (7 - (_pos & 7))) & 1);
                    _pos++;
                }
                return v;
            }

            public int Ue()
            {
                int zeros = 0;
                while (U(1) == 0)
                {
                    if (Overrun || ++zeros > 31) { Overrun = true; return 0; }
                }
                if (zeros == 0) return 0;
                return (1 << zeros) - 1 + U(zeros);
            }

            public int Se()
            {
                int k = Ue();
                return (k & 1) != 0 ? (k + 1) / 2 : -(k / 2);
            }
        }
    }
}
