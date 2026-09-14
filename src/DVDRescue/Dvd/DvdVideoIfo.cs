using DVDRescue.Core;

namespace DVDRescue.Dvd;

/// <summary>Una cella: l'unità minima di riproduzione, un intervallo contiguo di settori nel VOB.</summary>
public sealed class DvdCell
{
    public int VobId;
    public int CellId;
    /// <summary>Primo settore assoluto sul disco (LBA da 2048 byte).</summary>
    public long FirstSector;
    /// <summary>Ultimo settore assoluto sul disco, incluso.</summary>
    public long LastSector;
    public double Seconds;
    /// <summary>La cella fa parte di un blocco ad angoli multipli (ne teniamo solo il primo).</summary>
    public bool IsAngleBlock;

    public long SectorCount => LastSector >= FirstSector ? LastSector - FirstSector + 1 : 0;

    public override string ToString()
        => $"VOB {VobId} cella {CellId}: {FirstSector}-{LastSector} ({Seconds:0.00}s)";
}

/// <summary>Un capitolo (PTT): nel DVD-Video corrisponde a un "programma" dentro una PGC.</summary>
public sealed class DvdChapter
{
    public int Number;
    public List<DvdCell> Cells = new();
    public double Seconds;

    public override string ToString() => $"Capitolo {Number}: {Cells.Count} celle, {Seconds:0.00}s";
}

/// <summary>Un titolo del disco, come lo elenca la TT_SRPT della VIDEO_TS.IFO.</summary>
public sealed class DvdTitle
{
    /// <summary>Numero globale del titolo sul disco (1..99).</summary>
    public int TitleNumber;
    /// <summary>Title set (VTS) che lo contiene (1..99).</summary>
    public int VtsNumber;
    /// <summary>Numero del titolo dentro il suo VTS (TTN).</summary>
    public int TitleInVts;
    public double Seconds;

    public List<DvdChapter> Chapters = new();
    public List<DvdCell> Cells = new();

    /// <summary>Es. "720x576 PAL 4:3".</summary>
    public string VideoFormat;
    public List<string> Audio = new();

    /// <summary>Settori totali occupati dalle celle del titolo.</summary>
    public long SectorCount
    {
        get { long n = 0; foreach (var c in Cells) n += c.SectorCount; return n; }
    }

    public override string ToString()
        => $"Titolo {TitleNumber} (VTS {VtsNumber}#{TitleInVts}): {Chapters.Count} capitoli, " +
           $"{Cells.Count} celle, {Seconds:0.00}s";
}

/// <summary>Esito della lettura delle IFO di un DVD-Video.</summary>
public sealed class DvdVideoStructure
{
    public List<DvdTitle> Titles = new();
    /// <summary>Diagnostica in italiano per l'utente: cosa si è letto, cosa si è dovuto arrangiare.</summary>
    public List<string> Notes = new();
    public bool IsValid;
}

/// <summary>
/// Lettura delle strutture di navigazione DVD-Video (VIDEO_TS.IFO + VTS_xx_0.IFO).
///
/// Serve a evitare la frammentazione: cercando i pack MPEG e tagliando sulle discontinuità
/// dell'SCR un solo filmato si spezza in decine di pezzi, perché l'orologio riparte a ogni
/// cella. Le IFO dicono invece esattamente dove comincia e finisce ogni titolo.
///
/// Tutti i campi delle IFO sono BIG ENDIAN. Gli offset sono verificati contro
/// ifo_types.h di libdvdread. Il codice non lancia mai eccezioni: le IFO di un disco
/// rovinato sono spazzatura e vanno trattate come tali.
/// </summary>
public static class DvdVideoIfo
{
    public const int SectorSize = 2048;

    /// <summary>Tetto di sicurezza sulla dimensione di una IFO (le vere stanno sotto il megabyte).</summary>
    private const int MaxIfoBytes = 8 * 1024 * 1024;

    private const string VmgSignature = "DVDVIDEO-VMG";
    private const string VtsSignature = "DVDVIDEO-VTS";

    // ---- VMGI_MAT ----
    private const int VmgiLastSector = 0x1C;
    private const int VmgNrOfTitleSets = 0x3E;
    private const int VmgTtSrpt = 0xC4;      // settore, relativo all'inizio della IFO

    // ---- VTSI_MAT ----
    private const int VtsLastSector = 0x0C;
    private const int VtsiLastSector = 0x1C;
    private const int VtsTtVobs = 0xC4;      // settore, relativo all'inizio del VTS
    private const int VtsPttSrpt = 0xC8;     // settore, relativo all'inizio della IFO
    private const int VtsPgcit = 0xCC;       // settore, relativo all'inizio della IFO
    private const int VtsVideoAttr = 0x200;  // 2 byte
    private const int VtsNrAudioStreams = 0x202; // il conteggio vero sta a 0x203; 0x202 è zero, quindi
                                                 // leggerlo come U16BE dà lo stesso valore ed è più tollerante
    private const int VtsAudioAttr = 0x204;  // 8 byte per traccia, max 8 tracce

    // ---- PGC ----
    private const int PgcNrOfPrograms = 0x02;
    private const int PgcNrOfCells = 0x03;
    private const int PgcPlaybackTime = 0x04;
    private const int PgcNextPgcNr = 0x9C;
    private const int PgcProgramMapOffset = 0xE6;
    private const int PgcCellPlaybackOffset = 0xE8;
    private const int PgcCellPositionOffset = 0xEA;

    private const int CellPlaybackSize = 24;
    private const int CellPositionSize = 4;

    // =====================================================================================
    // API
    // =====================================================================================

    /// <summary>
    /// Legge la struttura di un DVD-Video.
    /// </summary>
    /// <param name="source">Sorgente dei byte del disco/immagine.</param>
    /// <param name="resolveFileOffset">
    /// Risolve un percorso logico (es. "VIDEO_TS/VTS_01_0.IFO") nell'offset in byte dentro
    /// <paramref name="source"/>. Deve restituire un valore negativo se il file non esiste.
    /// Lo fornisce il chiamante, che ha già letto UDF/ISO 9660.
    /// </param>
    /// <param name="log">Log diagnostico, può essere null.</param>
    public static DvdVideoStructure Parse(IBlockSource source, Func<string, long> resolveFileOffset,
                                          Action<string> log)
    {
        var result = new DvdVideoStructure();
        void Log(string m) { try { log?.Invoke(m); } catch { } }
        void Note(string m) { result.Notes.Add(m); Log(m); }

        if (source == null || resolveFileOffset == null)
        {
            Note("Sorgente o risolutore dei percorsi mancante: impossibile leggere le IFO.");
            return result;
        }

        try
        {
            ParseCore(source, resolveFileOffset, result, Log, Note);
        }
        catch (Exception ex)
        {
            Note("Errore imprevisto nella lettura delle IFO: " + ex.Message);
        }

        result.IsValid = result.Titles.Count > 0;
        if (!result.IsValid && result.Notes.Count == 0)
            Note("Nessun titolo ricavato dalle IFO: struttura assente o illeggibile.");

        return result;
    }

    private static void ParseCore(IBlockSource source, Func<string, long> resolve,
                                  DvdVideoStructure result, Action<string> log, Action<string> note)
    {
        // ---- 1. VIDEO_TS.IFO, con ripiego sul backup ----
        var vmg = LoadIfo(source, resolve, VmgCandidates(), VmgSignature, log);
        if (vmg == null)
        {
            note("VIDEO_TS.IFO e VIDEO_TS.BUP non leggibili o senza firma DVDVIDEO-VMG.");
            // Si può ancora tentare la scansione diretta dei VTS.
        }
        else
        {
            if (vmg.FromBackup)
                note($"VIDEO_TS.IFO illeggibile: usato il backup {vmg.Path} (è identico, esiste per questo).");
            else
                log($"VIDEO_TS.IFO letta da {vmg.Path} ({vmg.Data.Length} byte).");
        }

        // ---- 2. TT_SRPT: l'elenco dei titoli del disco ----
        var entries = vmg != null ? ReadTitleSearchPointers(vmg, note, log) : null;

        int declaredVts = vmg != null ? U16(vmg.Data, VmgNrOfTitleSets) : 0;
        if (vmg != null && declaredVts > 0 && declaredVts <= 99)
            log($"Il VMG dichiara {declaredVts} title set.");

        if (entries == null || entries.Count == 0)
        {
            note("TT_SRPT assente o incoerente: si ricostruiscono i titoli scandendo i VTS.");
            entries = ScanTitleSets(source, resolve, declaredVts, note, log);
        }

        if (entries.Count == 0)
        {
            note("Nessun VTS individuato: il disco non sembra un DVD-Video, oppure VIDEO_TS è perduta.");
            return;
        }

        // ---- 3. Un VTS alla volta (le IFO dei VTS sono grandi: meglio non tenerle tutte aperte) ----
        var byVts = new SortedDictionary<int, List<TitlePointer>>();
        foreach (var e in entries)
        {
            if (!byVts.TryGetValue(e.VtsNumber, out var l)) byVts[e.VtsNumber] = l = new List<TitlePointer>();
            l.Add(e);
        }

        foreach (var kv in byVts)
        {
            int vtsn = kv.Key;
            try
            {
                ParseTitleSet(source, resolve, vtsn, kv.Value, result, note, log);
            }
            catch (Exception ex)
            {
                note($"VTS {vtsn}: errore nella lettura ({ex.Message}); il title set viene saltato.");
            }
        }

        result.Titles.Sort((a, b) => a.TitleNumber.CompareTo(b.TitleNumber));

        int senza = 0;
        foreach (var t in result.Titles) if (t.Cells.Count == 0) senza++;
        if (senza > 0)
            note($"{senza} titoli su {result.Titles.Count} sono senza celle: le PGC relative sono illeggibili.");
    }

    // =====================================================================================
    // TT_SRPT (Title Search Pointer Table) — elenco globale dei titoli
    // =====================================================================================

    private sealed class TitlePointer
    {
        public int TitleNumber;
        public int VtsNumber;
        public int TitleInVts;      // TTN
        public int ChapterCount;    // numero di PTT dichiarato
        public int Angles;
        public long VtsStartSector; // dal TT_SRPT, -1 se ignoto
    }

    private static List<TitlePointer> ReadTitleSearchPointers(IfoImage vmg, Action<string> note, Action<string> log)
    {
        var list = new List<TitlePointer>();
        byte[] d = vmg.Data;

        long ttSrptSector = U32(d, VmgTtSrpt);
        if (ttSrptSector <= 0 || ttSrptSector * SectorSize >= d.Length)
        {
            note($"Puntatore alla TT_SRPT fuori dalla IFO (settore {ttSrptSector}).");
            return list;
        }

        int at = (int)(ttSrptSector * SectorSize);
        int count = U16(d, at);
        long lastByte = U32(d, at + 4);

        if (count == 0 || count > 99)
        {
            note($"TT_SRPT dichiara {count} titoli: valore fuori dai limiti del formato (1..99).");
            return list;
        }

        // last_byte è l'ultimo byte valido della tabella, relativo al suo inizio.
        long needed = 8L + count * 12L;
        if (lastByte + 1 < needed) log($"TT_SRPT: last_byte ({lastByte}) più corto delle {count} voci; si legge comunque.");
        if (at + needed > d.Length)
        {
            note("TT_SRPT troncata rispetto alla dimensione della IFO.");
            count = Math.Max(0, (d.Length - at - 8) / 12);
            if (count == 0) return list;
        }

        for (int i = 0; i < count; i++)
        {
            int o = at + 8 + i * 12;
            var tp = new TitlePointer
            {
                TitleNumber = i + 1,
                Angles = U8(d, o + 1),
                ChapterCount = U16(d, o + 2),
                VtsNumber = U8(d, o + 6),
                TitleInVts = U8(d, o + 7),
                VtsStartSector = U32(d, o + 8)
            };

            if (tp.VtsNumber < 1 || tp.VtsNumber > 99 || tp.TitleInVts < 1 || tp.TitleInVts > 99)
            {
                note($"TT_SRPT voce {i + 1}: VTS {tp.VtsNumber} / TTN {tp.TitleInVts} non plausibili; voce scartata.");
                continue;
            }
            if (tp.ChapterCount > 999) tp.ChapterCount = 0; // il formato ammette al massimo 99 PTT
            list.Add(tp);
        }

        if (list.Count > 0)
            log($"TT_SRPT: {list.Count} titoli su {count} voci.");
        return list;
    }

    /// <summary>
    /// Ripiego quando la TT_SRPT è perduta: si cercano direttamente i VTS_xx_0.IFO
    /// e si prende un titolo per ogni PGC d'ingresso trovata.
    /// </summary>
    private static List<TitlePointer> ScanTitleSets(IBlockSource source, Func<string, long> resolve,
                                                    int declaredVts, Action<string> note, Action<string> log)
    {
        var list = new List<TitlePointer>();
        int limit = declaredVts >= 1 && declaredVts <= 99 ? declaredVts : 99;
        int titleNumber = 0;
        int found = 0;

        for (int v = 1; v <= limit; v++)
        {
            var img = LoadIfo(source, resolve, VtsCandidates(v), VtsSignature, log);
            if (img == null) continue;
            found++;

            int ttn = 0;
            foreach (var srp in ReadPgcPointers(img, note))
            {
                if ((srp.EntryId & 0x80) == 0) continue; // non è una PGC d'ingresso: fa parte di un titolo
                ttn++;
                list.Add(new TitlePointer
                {
                    TitleNumber = ++titleNumber,
                    VtsNumber = v,
                    TitleInVts = (srp.EntryId & 0x7F) > 0 ? (srp.EntryId & 0x7F) : ttn,
                    ChapterCount = 0,
                    Angles = 1,
                    VtsStartSector = img.FromBackup ? -1 : img.ByteOffset / SectorSize
                });
            }
        }

        if (found > 0) note($"Scansione diretta: {found} title set trovati, {list.Count} titoli ricostruiti.");
        return list;
    }

    // =====================================================================================
    // VTS
    // =====================================================================================

    private static void ParseTitleSet(IBlockSource source, Func<string, long> resolve, int vtsn,
                                      List<TitlePointer> pointers, DvdVideoStructure result,
                                      Action<string> note, Action<string> log)
    {
        var img = LoadIfo(source, resolve, VtsCandidates(vtsn), VtsSignature, log);
        if (img == null)
        {
            note($"VTS {vtsn}: né VTS_{vtsn:00}_0.IFO né il suo .BUP sono leggibili; {pointers.Count} titoli perduti.");
            return;
        }
        if (img.FromBackup)
            note($"VTS {vtsn}: IFO illeggibile, usato il backup VTS_{vtsn:00}_0.BUP.");

        byte[] d = img.Data;

        // --- Settore d'inizio del VTS sul disco ---
        long vtsStart = ResolveVtsStartSector(img, resolve, vtsn, pointers, note, log);
        if (vtsStart < 0)
        {
            note($"VTS {vtsn}: impossibile stabilire dove comincia sul disco; gli intervalli sarebbero sbagliati.");
            return;
        }

        long vobStart = U32(d, VtsTtVobs);   // settore dei VOB del titolo, relativo all'inizio del VTS
        long vtsLast = U32(d, VtsLastSector);
        long vtsiLast = U32(d, VtsiLastSector);

        if (vobStart <= 0 || vobStart > vtsLast || vobStart < vtsiLast)
        {
            note($"VTS {vtsn}: puntatore ai VOB incoerente (VTSTT_VOBS={vobStart}, " +
                 $"VTSI fino a {vtsiLast}, VTS fino a {vtsLast}); title set saltato.");
            return;
        }

        long vobBase = vtsStart + vobStart;   // settore assoluto del primo VOB del titolo
        long vtsEnd = vtsStart + vtsLast;     // ultimo settore assoluto del VTS

        string videoFormat = DescribeVideo(U16(d, VtsVideoAttr));
        var audio = DescribeAudio(d, note, vtsn);

        // --- PGCIT: tutte le PGC del title set ---
        var pgcs = ReadAllPgcs(img, note, log, vtsn);
        if (pgcs.Count == 0)
        {
            note($"VTS {vtsn}: nessuna PGC leggibile nella VTS_PGCIT.");
            return;
        }

        // --- PTT_SRPT: quali PGC/programmi compongono ciascun titolo ---
        var pttByTitle = ReadPartOfTitleTable(img, note, log, vtsn);

        foreach (var tp in pointers)
        {
            var title = new DvdTitle
            {
                TitleNumber = tp.TitleNumber,
                VtsNumber = vtsn,
                TitleInVts = tp.TitleInVts,
                VideoFormat = videoFormat,
                Audio = new List<string>(audio)
            };

            var chapters = BuildChapters(tp, pttByTitle, pgcs, note, log, vtsn);
            if (chapters.Count == 0)
            {
                note($"Titolo {tp.TitleNumber} (VTS {vtsn}#{tp.TitleInVts}): nessun capitolo ricostruibile.");
                result.Titles.Add(title);
                continue;
            }

            // Traduzione in settori assoluti + filtro sugli angoli.
            long sourceSectors = source.Length / SectorSize;
            double totale = 0;
            int scartate = 0, troncate = 0;

            foreach (var ch in chapters)
            {
                var chapter = new DvdChapter { Number = ch.Number };
                foreach (var c in ch.Cells)
                {
                    long first = vobBase + c.FirstSector;
                    long last = vobBase + c.LastSector;

                    // Fuori dal VTS o inizio irrecuperabile: la cella non serve a niente.
                    if (c.LastSector < c.FirstSector || first < vobBase || first > vtsEnd ||
                        (sourceSectors > 0 && first >= sourceSectors))
                    {
                        scartate++;
                        continue;
                    }

                    // Fine oltre il VTS o oltre l'immagine: si tiene la parte leggibile
                    // invece di buttare via tutta la cella. Su un disco troncato è quasi
                    // sempre l'unico modo di recuperare qualcosa.
                    if (last > vtsEnd) { last = vtsEnd; troncate++; }
                    if (sourceSectors > 0 && last >= sourceSectors) { last = sourceSectors - 1; troncate++; }
                    if (last < first) { scartate++; continue; }

                    var cell = new DvdCell
                    {
                        VobId = c.VobId,
                        CellId = c.CellId,
                        FirstSector = first,
                        LastSector = last,
                        Seconds = c.Seconds,
                        IsAngleBlock = c.IsAngleBlock
                    };
                    chapter.Cells.Add(cell);
                    chapter.Seconds += c.Seconds;
                    title.Cells.Add(cell);
                }
                totale += chapter.Seconds;
                title.Chapters.Add(chapter);
            }

            if (scartate > 0)
                note($"Titolo {tp.TitleNumber}: {scartate} celle con intervalli fuori dal VTS, scartate.");
            if (troncate > 0)
                note($"Titolo {tp.TitleNumber}: {troncate} celle finiscono oltre la fine del disco " +
                     "e sono state accorciate; il titolo sarà incompleto.");

            // Durata: se il titolo copre PGC intere, la PGC_PB_TIME è più attendibile della
            // somma delle celle (tiene conto delle pause e degli angoli). Se però abbiamo
            // buttato via delle celle, quella durata è una promessa che non possiamo mantenere.
            double pgcTime = scartate == 0 && troncate == 0 ? SumWholePgcTime(chapters, pgcs) : 0;
            title.Seconds = pgcTime > 0 ? pgcTime : totale;

            if (pgcTime > 0 && totale > 0 && Math.Abs(pgcTime - totale) > 1.0)
                note($"Titolo {tp.TitleNumber}: durata dichiarata dalla PGC {pgcTime:0.0}s, " +
                     $"somma delle celle {totale:0.0}s; si usa la prima.");

            if (tp.ChapterCount > 0 && title.Chapters.Count != tp.ChapterCount)
                note($"Titolo {tp.TitleNumber}: la TT_SRPT dichiara {tp.ChapterCount} capitoli, " +
                     $"ne sono stati ricostruiti {title.Chapters.Count}.");

            result.Titles.Add(title);
            log($"  {title}");
        }
    }

    /// <summary>
    /// Dove comincia il VTS sul disco. Se abbiamo letto la IFO primaria con la firma giusta,
    /// il suo offset È il settore d'inizio: è un dato autoverificato e batte la TT_SRPT.
    /// </summary>
    private static long ResolveVtsStartSector(IfoImage img, Func<string, long> resolve, int vtsn,
                                              List<TitlePointer> pointers, Action<string> note, Action<string> log)
    {
        long fromSrpt = -1;
        foreach (var p in pointers) if (p.VtsStartSector > 0) { fromSrpt = p.VtsStartSector; break; }

        if (!img.FromBackup)
        {
            long s = img.ByteOffset / SectorSize;
            // Nelle immagini ISO i due valori differiscono quasi sempre di una costante, perché
            // chi ha creato l'immagine ha aggiunto i propri descrittori davanti ai file. Non è
            // un guasto: vale il filesystem, visto che da lì la IFO è stata letta per davvero.
            if (fromSrpt > 0 && fromSrpt != s)
                log($"VTS {vtsn}: la TT_SRPT lo colloca al settore {fromSrpt}, il filesystem al {s}; " +
                    "si usa il filesystem.");
            return s;
        }

        // Letto dal backup. Il .BUP sta in FONDO al title set, non all'inizio: la sua
        // posizione da sola non basta. In ordine di affidabilità:

        // 1. dove il filesystem dice che sta la IFO primaria. Anche se il suo contenuto è
        //    illeggibile, la voce di directory può essere intatta: è il dato migliore.
        long primary = TryResolve(resolve, VtsPrimaryCandidates(vtsn));
        if (primary >= 0)
        {
            log($"VTS {vtsn}: inizio dedotto dalla posizione di VTS_{vtsn:00}_0.IFO nel filesystem.");
            return primary / SectorSize;
        }

        // 2. la posizione del backup meno la lunghezza del title set, che il backup stesso
        //    dichiara. Anche questo parte da una posizione reale nel filesystem, quindi vale
        //    più di quello che si era ripromesso chi ha masterizzato il disco.
        long vtsLast = U32(img.Data, VtsLastSector);
        long vtsiLast = U32(img.Data, VtsiLastSector);
        if (vtsLast > vtsiLast && vtsLast < 1L << 32)
        {
            long bupSector = img.ByteOffset / SectorSize;
            long start = bupSector - (vtsLast - vtsiLast);
            if (start >= 0)
            {
                note($"VTS {vtsn}: inizio dedotto dalla posizione del backup (settore {start}); " +
                     "verificare gli intervalli se l'estrazione dà risultati strani.");
                return start;
            }
        }

        // 3. il valore scritto nella TT_SRPT. Ultima spiaggia: nelle immagini è quasi sempre
        //    sfasato di una costante rispetto alla posizione reale dei file.
        if (fromSrpt > 0)
        {
            note($"VTS {vtsn}: inizio preso dalla TT_SRPT (settore {fromSrpt}), non confermato " +
                 "dal filesystem; gli intervalli potrebbero essere spostati.");
            return fromSrpt;
        }
        return -1;
    }

    // =====================================================================================
    // PGCIT / PGC
    // =====================================================================================

    private struct PgcPointer
    {
        public int EntryId;
        public int BlockMode;
        public int BlockType;
        public long StartByte;   // relativo all'inizio della PGCIT
    }

    private static List<PgcPointer> ReadPgcPointers(IfoImage img, Action<string> note)
    {
        var list = new List<PgcPointer>();
        byte[] d = img.Data;

        long pgcitSector = U32(d, VtsPgcit);
        if (pgcitSector <= 0 || pgcitSector * SectorSize >= d.Length) return list;

        int at = (int)(pgcitSector * SectorSize);
        int count = U16(d, at);
        if (count == 0 || count > 999) return list;
        if (at + 8 + count * 8 > d.Length) count = Math.Max(0, (d.Length - at - 8) / 8);

        for (int i = 0; i < count; i++)
        {
            int o = at + 8 + i * 8;
            byte flags = U8(d, o + 1);
            list.Add(new PgcPointer
            {
                EntryId = U8(d, o),
                BlockMode = (flags >> 6) & 3,
                BlockType = (flags >> 4) & 3,
                StartByte = U32(d, o + 4)
            });
        }
        return list;
    }

    private sealed class CellRaw
    {
        public int VobId, CellId;
        public long FirstSector, LastSector;  // relativi all'inizio dei VOB del titolo
        public double Seconds;
        public bool IsAngleBlock;
        public int BlockMode, BlockType;
    }

    private sealed class PgcRaw
    {
        public int Number;              // 1-based, come lo indicano i PTT
        public int EntryId;             // bit 7 = PGC d'ingresso, bit 0-6 = titolo (TTN) nel VTS
        public int NrPrograms;
        public double Seconds;
        public int NextPgc;
        public List<int> ProgramFirstCell = new();  // cella d'ingresso di ogni programma (1-based)
        public List<CellRaw> Cells = new();         // tutte le celle, angoli compresi
    }

    private static Dictionary<int, PgcRaw> ReadAllPgcs(IfoImage img, Action<string> note, Action<string> log, int vtsn)
    {
        var map = new Dictionary<int, PgcRaw>();
        byte[] d = img.Data;

        long pgcitSector = U32(d, VtsPgcit);
        if (pgcitSector <= 0 || pgcitSector * SectorSize >= d.Length)
        {
            note($"VTS {vtsn}: puntatore alla VTS_PGCIT fuori dalla IFO (settore {pgcitSector}).");
            return map;
        }
        int pgcitAt = (int)(pgcitSector * SectorSize);

        var pointers = ReadPgcPointers(img, note);
        if (pointers.Count == 0)
        {
            note($"VTS {vtsn}: VTS_PGCIT vuota o incoerente.");
            return map;
        }

        for (int i = 0; i < pointers.Count; i++)
        {
            long abs = pgcitAt + pointers[i].StartByte;
            if (pointers[i].StartByte <= 0 || abs + 0xEC > d.Length)
            {
                note($"VTS {vtsn}: PGC {i + 1} punta fuori dalla IFO; saltata.");
                continue;
            }
            var pgc = ReadPgc(d, (int)abs, i + 1, note, vtsn);
            if (pgc != null) { pgc.EntryId = pointers[i].EntryId; map[i + 1] = pgc; }
        }

        log($"VTS {vtsn}: {map.Count} PGC lette su {pointers.Count} dichiarate.");
        return map;
    }

    private static PgcRaw ReadPgc(byte[] d, int at, int number, Action<string> note, int vtsn)
    {
        var pgc = new PgcRaw { Number = number };

        int nrPrograms = U8(d, at + PgcNrOfPrograms);
        int nrCells = U8(d, at + PgcNrOfCells);
        pgc.NrPrograms = nrPrograms;
        pgc.Seconds = ReadDvdTime(d, at + PgcPlaybackTime);
        pgc.NextPgc = U16(d, at + PgcNextPgcNr);

        if (nrCells == 0)
            return pgc;   // PGC vuota (menu senza celle): legittima, semplicemente non contiene video

        int mapOff = U16(d, at + PgcProgramMapOffset);
        int playOff = U16(d, at + PgcCellPlaybackOffset);
        int posOff = U16(d, at + PgcCellPositionOffset);

        // Mappa dei programmi: un byte per programma, la cella d'ingresso.
        if (mapOff > 0 && at + mapOff + nrPrograms <= d.Length)
        {
            for (int p = 0; p < nrPrograms; p++)
            {
                int c = U8(d, at + mapOff + p);
                pgc.ProgramFirstCell.Add(c >= 1 && c <= nrCells ? c : (p == 0 ? 1 : 0));
            }
        }
        if (pgc.ProgramFirstCell.Count == 0)
        {
            // Senza mappa si assume un programma per cella: meglio troppi capitoli che nessuno.
            note($"VTS {vtsn}: PGC {number} senza mappa dei programmi; un capitolo per cella.");
            for (int c = 1; c <= nrCells; c++) pgc.ProgramFirstCell.Add(c);
            pgc.NrPrograms = nrCells;
        }

        // Cell Playback Information
        if (playOff <= 0 || at + playOff + nrCells * CellPlaybackSize > d.Length)
        {
            note($"VTS {vtsn}: PGC {number}, tabella delle celle fuori dalla IFO; PGC scartata.");
            return null;
        }

        bool posOk = posOff > 0 && at + posOff + nrCells * CellPositionSize <= d.Length;
        if (!posOk)
            note($"VTS {vtsn}: PGC {number} senza Cell Position Information; VOB ID e Cell ID non disponibili.");

        for (int c = 0; c < nrCells; c++)
        {
            int o = at + playOff + c * CellPlaybackSize;
            byte b0 = U8(d, o);

            var cell = new CellRaw
            {
                BlockMode = (b0 >> 6) & 3,
                BlockType = (b0 >> 4) & 3,
                Seconds = ReadDvdTime(d, o + 4),
                FirstSector = U32(d, o + 8),
                LastSector = U32(d, o + 20)
            };
            cell.IsAngleBlock = cell.BlockType == 1;

            if (posOk)
            {
                int po = at + posOff + c * CellPositionSize;
                cell.VobId = U16(d, po);
                cell.CellId = U8(d, po + 3);
            }
            else
            {
                cell.VobId = 1;
                cell.CellId = c + 1;
            }

            pgc.Cells.Add(cell);
        }

        return pgc;
    }

    // =====================================================================================
    // VTS_PTT_SRPT — la mappa titolo → (PGC, programma) di ogni capitolo
    // =====================================================================================

    private struct PttEntry { public int Pgcn; public int Pgn; }

    private static Dictionary<int, List<PttEntry>> ReadPartOfTitleTable(IfoImage img, Action<string> note,
                                                                       Action<string> log, int vtsn)
    {
        var map = new Dictionary<int, List<PttEntry>>();
        byte[] d = img.Data;

        long srptSector = U32(d, VtsPttSrpt);
        if (srptSector <= 0 || srptSector * SectorSize >= d.Length)
        {
            note($"VTS {vtsn}: VTS_PTT_SRPT assente; i capitoli si ricavano dai programmi delle PGC.");
            return map;
        }

        int at = (int)(srptSector * SectorSize);
        int titles = U16(d, at);
        long lastByte = U32(d, at + 4);

        if (titles == 0 || titles > 99)
        {
            note($"VTS {vtsn}: VTS_PTT_SRPT dichiara {titles} titoli, valore non plausibile.");
            return map;
        }
        if (at + 8 + titles * 4 > d.Length)
        {
            note($"VTS {vtsn}: VTS_PTT_SRPT troncata.");
            return map;
        }

        // Tabella di offset (uno per titolo), relativi all'inizio della VTS_PTT_SRPT.
        var offsets = new long[titles];
        for (int i = 0; i < titles; i++) offsets[i] = U32(d, at + 8 + i * 4);

        for (int i = 0; i < titles; i++)
        {
            long start = offsets[i];
            long end = i + 1 < titles ? offsets[i + 1] : lastByte + 1;

            if (start < 8 || end <= start || at + end > d.Length)
            {
                note($"VTS {vtsn}: voce {i + 1} della VTS_PTT_SRPT con offset incoerenti; saltata.");
                continue;
            }

            int n = (int)((end - start) / 4);
            if (n <= 0 || n > 999) continue;

            var ptts = new List<PttEntry>(n);
            for (int k = 0; k < n; k++)
            {
                int o = (int)(at + start + k * 4);
                var e = new PttEntry { Pgcn = U16(d, o), Pgn = U16(d, o + 2) };
                if (e.Pgcn == 0 || e.Pgn == 0) break;   // fine effettiva della lista
                ptts.Add(e);
            }
            if (ptts.Count > 0) map[i + 1] = ptts;
        }

        log($"VTS {vtsn}: VTS_PTT_SRPT con {map.Count} titoli.");
        return map;
    }

    private sealed class ChapterRaw
    {
        public int Number;
        public int Pgcn;
        public int FirstProgram, LastProgram;
        public List<CellRaw> Cells = new();
    }

    private static List<ChapterRaw> BuildChapters(TitlePointer tp, Dictionary<int, List<PttEntry>> pttByTitle,
                                                  Dictionary<int, PgcRaw> pgcs, Action<string> note,
                                                  Action<string> log, int vtsn)
    {
        var chapters = new List<ChapterRaw>();

        if (pttByTitle.TryGetValue(tp.TitleInVts, out var ptts) && ptts.Count > 0)
        {
            for (int k = 0; k < ptts.Count; k++)
            {
                int pgcn = ptts[k].Pgcn;
                if (!pgcs.TryGetValue(pgcn, out var pgc))
                {
                    note($"Titolo {tp.TitleNumber}: capitolo {k + 1} rimanda alla PGC {pgcn}, che non esiste.");
                    continue;
                }

                int first = ptts[k].Pgn;
                int last = (k + 1 < ptts.Count && ptts[k + 1].Pgcn == pgcn)
                           ? ptts[k + 1].Pgn - 1
                           : pgc.NrPrograms;

                var ch = new ChapterRaw { Number = k + 1, Pgcn = pgcn, FirstProgram = first, LastProgram = last };
                CollectCells(pgc, first, last, ch.Cells);
                chapters.Add(ch);
            }
        }
        else
        {
            // Ripiego: il titolo è la PGC d'ingresso con quel TTN, più le PGC concatenate.
            var pgc = FindEntryPgc(pgcs, tp.TitleInVts);
            if (pgc == null)
            {
                note($"Titolo {tp.TitleNumber}: nessuna PGC d'ingresso per TTN {tp.TitleInVts}.");
                return chapters;
            }

            int guard = 0;
            int number = 0;
            var visited = new HashSet<int>();
            while (pgc != null && guard++ < 256 && visited.Add(pgc.Number))
            {
                for (int p = 1; p <= pgc.NrPrograms; p++)
                {
                    var ch = new ChapterRaw { Number = ++number, Pgcn = pgc.Number, FirstProgram = p, LastProgram = p };
                    CollectCells(pgc, p, p, ch.Cells);
                    chapters.Add(ch);
                }
                pgc = pgc.NextPgc > 0 && pgcs.TryGetValue(pgc.NextPgc, out var nx) ? nx : null;
            }
            if (chapters.Count > 0)
                log($"Titolo {tp.TitleNumber}: capitoli ricavati dai programmi (VTS_PTT_SRPT non usabile).");
        }

        return chapters;
    }

    private static PgcRaw FindEntryPgc(Dictionary<int, PgcRaw> pgcs, int ttn)
    {
        // 1. La PGC che dichiara di essere l'ingresso proprio di questo titolo.
        foreach (var kv in pgcs)
        {
            var p = kv.Value;
            if ((p.EntryId & 0x80) != 0 && (p.EntryId & 0x7F) == ttn && p.Cells.Count > 0) return p;
        }

        // 2. La n-esima PGC d'ingresso, se il campo entry_id non porta il numero del titolo.
        int seen = 0;
        foreach (var kv in pgcs)
        {
            var p = kv.Value;
            if ((p.EntryId & 0x80) == 0 || p.Cells.Count == 0) continue;
            if (++seen == ttn) return p;
        }

        // 3. La n-esima PGC con del video dentro: nessuna PGC si dichiara d'ingresso.
        seen = 0;
        foreach (var kv in pgcs)
        {
            if (kv.Value.Cells.Count == 0) continue;
            if (++seen == ttn) return kv.Value;
        }
        return null;
    }

    /// <summary>
    /// Celle dei programmi <paramref name="firstProgram"/>..<paramref name="lastProgram"/>.
    /// I blocchi ad angoli multipli sono ridotti al primo angolo.
    /// </summary>
    private static void CollectCells(PgcRaw pgc, int firstProgram, int lastProgram, List<CellRaw> outCells)
    {
        if (pgc == null || pgc.Cells.Count == 0) return;
        if (firstProgram < 1) firstProgram = 1;
        if (lastProgram > pgc.ProgramFirstCell.Count) lastProgram = pgc.ProgramFirstCell.Count;
        if (lastProgram < firstProgram) return;

        int firstCell = CellOfProgram(pgc, firstProgram);
        int lastCell = lastProgram + 1 <= pgc.ProgramFirstCell.Count
                       ? CellOfProgram(pgc, lastProgram + 1) - 1
                       : pgc.Cells.Count;
        if (lastCell < firstCell || lastCell > pgc.Cells.Count) lastCell = pgc.Cells.Count;

        bool inAngleBlock = false;
        for (int c = firstCell; c <= lastCell; c++)
        {
            var cell = pgc.Cells[c - 1];

            if (cell.BlockType == 1) // blocco ad angoli
            {
                // block_mode: 1 = prima cella del blocco, 2 = dentro, 3 = ultima.
                if (cell.BlockMode == 1) { inAngleBlock = true; outCells.Add(cell); continue; }
                if (inAngleBlock) { if (cell.BlockMode == 3) inAngleBlock = false; continue; } // angoli 2..9: saltati
                outCells.Add(cell);  // blocco malformato: si tiene comunque qualcosa
                continue;
            }

            inAngleBlock = false;
            outCells.Add(cell);
        }
    }

    private static int CellOfProgram(PgcRaw pgc, int program)
    {
        if (program < 1 || program > pgc.ProgramFirstCell.Count) return pgc.Cells.Count + 1;
        int c = pgc.ProgramFirstCell[program - 1];
        return c >= 1 ? c : 1;
    }

    /// <summary>Somma delle PGC_PB_TIME delle PGC coperte per intero dai capitoli del titolo.</summary>
    private static double SumWholePgcTime(List<ChapterRaw> chapters, Dictionary<int, PgcRaw> pgcs)
    {
        var covered = new Dictionary<int, HashSet<int>>();
        foreach (var ch in chapters)
        {
            if (!covered.TryGetValue(ch.Pgcn, out var set)) covered[ch.Pgcn] = set = new HashSet<int>();
            for (int p = ch.FirstProgram; p <= ch.LastProgram; p++) set.Add(p);
        }

        double sum = 0;
        foreach (var kv in covered)
        {
            if (!pgcs.TryGetValue(kv.Key, out var pgc)) return 0;
            if (pgc.NrPrograms <= 0 || kv.Value.Count != pgc.NrPrograms) return 0; // PGC coperta solo in parte
            if (pgc.Seconds <= 0) return 0;
            sum += pgc.Seconds;
        }
        return sum;
    }

    // =====================================================================================
    // Attributi video/audio
    // =====================================================================================

    private static readonly int[,] PalSizes = { { 720, 576 }, { 704, 576 }, { 352, 576 }, { 352, 288 } };
    private static readonly int[,] NtscSizes = { { 720, 480 }, { 704, 480 }, { 352, 480 }, { 352, 240 } };

    /// <summary>
    /// video_attr_t, 2 byte big endian:
    /// mpeg_version(2) video_format(2) display_aspect_ratio(2) permitted_df(2) |
    /// line21_cc_1(1) line21_cc_2(1) unknown(1) bit_rate(1) picture_size(2) letterboxed(1) film_mode(1)
    /// </summary>
    private static string DescribeVideo(int attr)
    {
        int videoFormat = (attr >> 12) & 3;     // 0 = NTSC, 1 = PAL
        int aspect = (attr >> 10) & 3;          // 0 = 4:3, 3 = 16:9
        int pictureSize = (attr >> 2) & 3;
        bool letterboxed = ((attr >> 1) & 1) != 0;

        bool pal = videoFormat == 1;
        int w = pal ? PalSizes[pictureSize, 0] : NtscSizes[pictureSize, 0];
        int h = pal ? PalSizes[pictureSize, 1] : NtscSizes[pictureSize, 1];

        string ar = aspect == 3 ? "16:9" : aspect == 0 ? "4:3" : "?";
        string s = $"{w}x{h} {(pal ? "PAL" : "NTSC")} {ar}";
        if (letterboxed) s += " letterbox";
        return s;
    }

    private static readonly string[] AudioFormats =
        { "AC-3", "?", "MPEG-1", "MPEG-2", "LPCM", "?", "DTS", "?" };

    private static List<string> DescribeAudio(byte[] d, Action<string> note, int vtsn)
    {
        var list = new List<string>();
        int n = U16(d, VtsNrAudioStreams);   // byte alto a zero: equivale al conteggio a 0x203
        if (n > 8)
        {
            note($"VTS {vtsn}: {n} tracce audio dichiarate, il formato ne ammette 8; limitato a 8.");
            n = 8;
        }

        for (int i = 0; i < n; i++)
        {
            int o = VtsAudioAttr + i * 8;
            if (o + 8 > d.Length) break;

            int b0 = U8(d, o), b1 = U8(d, o + 1);
            string fmt = AudioFormats[(b0 >> 5) & 7];
            int channels = (b1 & 7) + 1;
            int freqCode = (b1 >> 4) & 3;
            string freq = freqCode == 0 ? "48kHz" : freqCode == 1 ? "96kHz" : "?";

            string lang = "";
            if (((b0 >> 2) & 3) == 1)
            {
                char c1 = (char)U8(d, o + 2), c2 = (char)U8(d, o + 3);
                if (char.IsLetter(c1) && char.IsLetter(c2)) lang = " " + c1 + c2;
            }

            list.Add($"{fmt} {channels}ch {freq}{lang}");
        }
        return list;
    }

    // =====================================================================================
    // Caricamento delle IFO (con ripiego sul .BUP)
    // =====================================================================================

    private sealed class IfoImage
    {
        public byte[] Data;
        public long ByteOffset;
        public bool FromBackup;
        public string Path;
    }

    private static IfoImage LoadIfo(IBlockSource source, Func<string, long> resolve,
                                    List<(string Path, bool Backup)> candidates, string signature,
                                    Action<string> log)
    {
        foreach (var (path, backup) in candidates)
        {
            long off;
            try { off = resolve(path); } catch { continue; }
            if (off < 0) continue;

            var data = ReadIfo(source, off, signature, log);
            if (data == null)
            {
                log?.Invoke($"{path}: presente ma senza firma {signature} o illeggibile.");
                continue;
            }
            return new IfoImage { Data = data, ByteOffset = off, FromBackup = backup, Path = path };
        }
        return null;
    }

    private static byte[] ReadIfo(IBlockSource source, long byteOffset, string signature, Action<string> log)
    {
        try
        {
            var head = new byte[SectorSize];
            if (source.ReadBytes(byteOffset, SectorSize, head, 0) < 64) return null;
            if (ByteUtils.Ascii(head, 0, 12) != signature) return null;

            // La IFO dichiara il proprio ultimo settore (VMGI_LAST_SECTOR / VTSI_LAST_SECTOR).
            long lastSector = U32(head, VmgiLastSector);   // stesso offset in VMGI e VTSI
            long size = (lastSector + 1) * SectorSize;

            if (size <= SectorSize || size > MaxIfoBytes)
            {
                log?.Invoke($"IFO con dimensione dichiarata assurda ({size} byte): si leggono 1 MB.");
                size = Math.Min(MaxIfoBytes, 1024 * 1024);
            }

            long available = source.Length - byteOffset;
            if (available > 0 && size > available) size = available;
            if (size <= SectorSize) return head;

            var buf = new byte[size];
            Array.Copy(head, buf, SectorSize);
            if (size > SectorSize)
                source.ReadBytes(byteOffset + SectorSize, (int)(size - SectorSize), buf, SectorSize);
            return buf;
        }
        catch { return null; }
    }

    private static long TryResolve(Func<string, long> resolve, List<string> paths)
    {
        foreach (var p in paths)
        {
            try { long o = resolve(p); if (o >= 0) return o; } catch { }
        }
        return -1;
    }

    // I chiamanti possono nominare i file in modi diversi (con o senza directory, con il ";1"
    // dell'ISO 9660, con lo slash o il backslash): si provano tutte le forme ragionevoli.
    private static List<string> Spellings(string dir, string name)
    {
        var list = new List<string>();
        foreach (string p in new[] { dir + "/" + name, "/" + dir + "/" + name, dir + "\\" + name,
                                     "\\" + dir + "\\" + name, name, "/" + name })
        {
            list.Add(p);
            list.Add(p + ";1");
        }
        return list;
    }

    private static List<(string, bool)> VmgCandidates()
    {
        var list = new List<(string, bool)>();
        foreach (var p in Spellings("VIDEO_TS", "VIDEO_TS.IFO")) list.Add((p, false));
        foreach (var p in Spellings("VIDEO_TS", "VIDEO_TS.BUP")) list.Add((p, true));
        return list;
    }

    private static List<string> VtsPrimaryCandidates(int vts) => Spellings("VIDEO_TS", $"VTS_{vts:00}_0.IFO");

    private static List<(string, bool)> VtsCandidates(int vts)
    {
        var list = new List<(string, bool)>();
        foreach (var p in VtsPrimaryCandidates(vts)) list.Add((p, false));
        foreach (var p in Spellings("VIDEO_TS", $"VTS_{vts:00}_0.BUP")) list.Add((p, true));
        return list;
    }

    // =====================================================================================
    // Letture di base, tutte con controllo dei limiti: una IFO rovinata non deve far saltare nulla
    // =====================================================================================

    private static byte U8(byte[] b, int o) => (uint)o < (uint)b.Length ? b[o] : (byte)0;

    private static int U16(byte[] b, int o)
        => o >= 0 && o + 2 <= b.Length ? ByteUtils.U16BE(b, o) : 0;

    private static long U32(byte[] b, int o)
        => o >= 0 && o + 4 <= b.Length ? ByteUtils.U32BE(b, o) : 0L;

    /// <summary>
    /// dvd_time_t: hh:mm:ss in BCD, più un byte con il frame rate nei due bit alti
    /// (1 = 25 fps, 3 = 29,97 fps) e i frame in BCD nei sei bassi.
    /// </summary>
    private static double ReadDvdTime(byte[] b, int o)
    {
        if (o < 0 || o + 4 > b.Length) return 0;

        int h = Bcd(b[o]), m = Bcd(b[o + 1]), s = Bcd(b[o + 2]);
        byte fu = b[o + 3];
        int frames = Bcd((byte)(fu & 0x3F));
        int rateCode = (fu >> 6) & 3;
        double fps = rateCode == 1 ? 25.0 : rateCode == 3 ? 30000.0 / 1001.0 : 0.0;

        if (h < 0 || m < 0 || s < 0 || h > 99 || m > 59 || s > 59) return 0;

        double total = h * 3600.0 + m * 60.0 + s;
        if (fps > 0 && frames >= 0 && frames < 31) total += frames / fps;
        return total;
    }

    /// <summary>Cifra BCD; -1 se il byte non è BCD valido.</summary>
    private static int Bcd(byte v)
    {
        int hi = (v >> 4) & 0x0F, lo = v & 0x0F;
        if (hi > 9 || lo > 9) return -1;
        return hi * 10 + lo;
    }
}
