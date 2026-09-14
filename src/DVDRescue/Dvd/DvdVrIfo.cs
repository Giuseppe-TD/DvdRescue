using DVDRescue.Core;

namespace DVDRescue.Dvd;

/// <summary>Un programma DVD-VR: una registrazione, cioè quello che l'utente chiama "un filmato".</summary>
public sealed class VrProgram
{
    /// <summary>Numero d'ordine, 1-based.</summary>
    public int Number;
    /// <summary>Data e ora di registrazione, se il registratore l'ha scritta.</summary>
    public DateTime? Recorded;
    public double Seconds;
    /// <summary>Etichetta scritta dal registratore (spesso il nome del canale o un titolo).</summary>
    public string Label;
    /// <summary>
    /// Intervalli di settori assoluti (inclusivi) nel VRO. Normalmente uno solo per programma:
    /// i VOBU di un programma sono contigui.
    /// </summary>
    public List<(long FirstSector, long LastSector)> Ranges = new();

    public long SectorCount
    {
        get { long n = 0; foreach (var r in Ranges) if (r.LastSector >= r.FirstSector) n += r.LastSector - r.FirstSector + 1; return n; }
    }

    public override string ToString()
        => $"Programma {Number}: {(Recorded.HasValue ? Recorded.Value.ToString("yyyy-MM-dd HH:mm:ss") : "senza data")}, " +
           $"{Seconds:0.0}s, {SectorCount} settori" + (string.IsNullOrEmpty(Label) ? "" : $", \"{Label}\"");
}

/// <summary>
/// Esito della lettura di VR_MANGR.IFO.
/// Attenzione: <see cref="Programs"/> può contenere qualcosa anche con <see cref="IsValid"/> a false —
/// succede quando si ricavano le date ma non gli intervalli. Le date restano utili per dare
/// un nome ai file estratti con altri metodi.
/// </summary>
public sealed class DvdVrStructure
{
    public List<VrProgram> Programs = new();
    public List<string> Notes = new();
    public bool IsValid;
}

/// <summary>
/// Lettura di VR_MANGR.IFO (DVD-VR: videocamere e registratori da tavolo, cartella DVD_RTAV).
///
/// ATTENZIONE: il formato DVD-VR non è documentato pubblicamente come il DVD-Video.
/// Questa implementazione segue la struttura ricostruita da dvd-vr (Pádraig Brady, GPL),
/// l'unica descrizione pubblica affidabile, e NON è mai stata verificata su dischi reali
/// dentro questo progetto. Per questo ogni campo viene validato: se qualcosa non torna si
/// restituisce IsValid=false con una nota, invece di consegnare al chiamante divisioni
/// inventate. Meglio dichiarare di non aver capito che spezzare male un filmato.
///
/// Tutto il file è big endian.
/// </summary>
public static class DvdVrIfo
{
    public const int SectorSize = 2048;

    private const string Signature = "DVD_RTR_VMG0";

    // ---- RTAV_VMGI_MAT (512 byte a offset 0) ----
    private const int MatSize = 512;
    private const int MatVmgEa = 12;        // u32, ultimo indirizzo del VMG
    private const int MatVmgiEa = 28;       // u32
    private const int MatVersion = 32;      // u16
    private const int MatDiscInfo1 = 98;    // 64 byte
    private const int MatDiscInfo2 = 162;   // 64 byte
    private const int MatPgitSa = 256;      // u32, inizio della tabella info dei programmi (M_AVFIT)
    private const int MatDefPsiSa = 304;    // u32, inizio delle etichette (ORG_PGCI / program set info)

    // ---- PGITI, all'indirizzo MatPgitSa ----
    private const int PgitiSize = 8;
    private const int PgitiNrOfPgi = 2;          // u8
    private const int PgitiNrOfVobFormats = 3;   // u8
    private const int PgitiEa = 4;               // u32

    private const int VobFormatSize = 60;
    private const int PgiGiSize = 2;             // u16 nr_of_programs

    // ---- VVOB ----
    private const int VvobSize = 21;
    private const int VvobAttr = 0;              // u16
    private const int VvobTimestamp = 2;         // 5 byte
    private const int VvobFormatId = 8;          // u8
    private const int VvobStartPtm = 9;          // u32 + u16
    private const int VvobEndPtm = 15;           // u32 + u16
    private const int AdjVobSize = 12;

    // ---- VOBU_MAP ----
    private const int VobuMapSize = 10;
    private const int VobuMapNrTimeInfo = 0;     // u16
    private const int VobuMapNrVobuInfo = 2;     // u16
    private const int VobuMapVobOffset = 6;      // u32, in settori dall'inizio del VRO
    private const int TimeInfoSize = 7;
    private const int VobuInfoSize = 3;

    // ---- PSI (etichette) ----
    private const int PsiGiSize = 4;
    private const int PsiGiNrOfPsi = 1;          // u8
    private const int PsiSize = 140;
    private const int PsiNrOfPrograms = 2;       // u16
    private const int PsiLabel = 4;              // 64 byte
    private const int PsiTitle = 68;             // 64 byte

    // ---- Limiti di plausibilità ----
    private const int MaxPrograms = 1999;
    private const int MaxVobFormats = 16;
    private const int MinYear = 1995;            // il DVD-VR è del 2000; sotto il 1995 è spazzatura
    private const long MaxVroSectors = 8L * 1024 * 1024 * 1024 / SectorSize;  // ~8 GB: oltre non esiste
    private const double PtmClock = 90000.0;     // i PTM del DVD sono in unità da 1/90000 s

    /// <summary>
    /// Legge VR_MANGR.IFO.
    /// </summary>
    /// <param name="ifoData">Contenuto completo del file VR_MANGR.IFO (o del suo backup VR_MANGR.BUP).</param>
    /// <param name="vroStartSector">Settore d'inizio di VR_MOVIE.VRO, per rendere assoluti gli intervalli.</param>
    /// <param name="log">Log diagnostico, può essere null.</param>
    public static DvdVrStructure Parse(byte[] ifoData, long vroStartSector, Action<string> log)
    {
        var r = new DvdVrStructure();
        void Log(string m) { try { log?.Invoke(m); } catch { } }
        void Note(string m) { r.Notes.Add(m); Log(m); }

        try
        {
            ParseCore(ifoData, vroStartSector, r, Log, Note);
        }
        catch (Exception ex)
        {
            Note("Errore imprevisto nella lettura di VR_MANGR.IFO: " + ex.Message);
            r.IsValid = false;
        }

        if (!r.IsValid && r.Notes.Count == 0)
            Note("VR_MANGR.IFO non interpretabile: struttura non riconosciuta.");

        return r;
    }

    private static void ParseCore(byte[] d, long vroStartSector, DvdVrStructure r,
                                  Action<string> log, Action<string> note)
    {
        if (d == null || d.Length < MatSize)
        {
            note($"VR_MANGR.IFO troppo corto ({d?.Length ?? 0} byte): servono almeno {MatSize}.");
            return;
        }
        if (vroStartSector < 0)
        {
            note("Settore d'inizio del VRO non valido.");
            return;
        }

        if (ByteUtils.Ascii(d, 0, 12) != Signature)
        {
            note($"Firma \"{Signature}\" assente: il file non è una VR_MANGR.IFO.");
            return;
        }

        int version = U16(d, MatVersion);
        long vmgEa = U32(d, MatVmgEa);
        log($"DVD-VR versione {version >> 8}.{version & 0xFF}, VMG fino al byte {vmgEa}.");

        string disc = Latin1(d, MatDiscInfo2, 64);
        if (string.IsNullOrWhiteSpace(disc)) disc = Latin1(d, MatDiscInfo1, 64);
        if (!string.IsNullOrWhiteSpace(disc)) log("Etichetta del disco: " + disc);

        // ---- Tabella info dei programmi ----
        long pgitSa = U32(d, MatPgitSa);
        if (pgitSa < MatSize || pgitSa + PgitiSize > d.Length)
        {
            note($"Puntatore alla tabella dei programmi fuori dal file (byte {pgitSa}, file {d.Length} byte).");
            return;
        }
        int pgiti = (int)pgitSa;

        int nrOfPgi = U8(d, pgiti + PgitiNrOfPgi);
        int nrOfVobFormats = U8(d, pgiti + PgitiNrOfVobFormats);
        long pgitEa = U32(d, pgiti + PgitiEa);

        if (nrOfPgi == 0)
        {
            note("La tabella dei programmi dichiara zero sottotabelle: niente da leggere.");
            return;
        }
        if (nrOfPgi > 1)
            note($"Il file dichiara {nrOfPgi} tabelle di programmi; si usa solo la prima (come fa dvd-vr).");

        if (nrOfVobFormats == 0 || nrOfVobFormats > MaxVobFormats)
        {
            note($"Numero di formati VOB non plausibile ({nrOfVobFormats}); struttura non riconosciuta.");
            return;
        }

        int pgiGi = pgiti + PgitiSize + nrOfVobFormats * VobFormatSize;
        if (pgiGi + PgiGiSize > d.Length)
        {
            note("Tabella dei formati VOB oltre la fine del file.");
            return;
        }

        int nrOfPrograms = U16(d, pgiGi);
        if (nrOfPrograms == 0 || nrOfPrograms > MaxPrograms)
        {
            note($"Numero di programmi non plausibile ({nrOfPrograms}); struttura non riconosciuta.");
            return;
        }

        int saTable = pgiGi + PgiGiSize;
        if (saTable + nrOfPrograms * 4 > d.Length)
        {
            note($"La tabella dei {nrOfPrograms} puntatori ai programmi esce dal file.");
            return;
        }
        log($"{nrOfPrograms} programmi dichiarati, {nrOfVobFormats} formati VOB.");

        // ---- Etichette (facoltative: molti dischi non ne hanno) ----
        var labels = ReadLabels(d, U32(d, MatDefPsiSa), nrOfPrograms, note, log);

        // ---- Un programma alla volta ----
        int withRange = 0, withDate = 0, stimate = 0;
        int maxYear = DateTime.Now.Year + 1;
        var ranges = new List<(long First, long Last)>();

        for (int p = 0; p < nrOfPrograms; p++)
        {
            var prog = new VrProgram { Number = p + 1 };
            if (labels != null && p < labels.Count) prog.Label = labels[p];

            long vvobSa = U32(d, saTable + p * 4);
            long vvob = pgitSa + vvobSa;      // i puntatori sono relativi all'inizio della tabella, non al file

            if (vvobSa <= 0 || vvob + VvobSize > d.Length)
            {
                note($"Programma {p + 1}: puntatore alle info del VOB fuori dal file; programma saltato.");
                r.Programs.Add(prog);
                continue;
            }
            int at = (int)vvob;

            // Data e ora di registrazione
            prog.Recorded = ParseTimestamp(d, at + VvobTimestamp, MinYear, maxYear);
            if (prog.Recorded.HasValue) withDate++;

            // Durata dai PTM di inizio/fine del VOB
            long startPtm = U32(d, at + VvobStartPtm);
            long endPtm = U32(d, at + VvobEndPtm);
            if (endPtm > startPtm)
            {
                double sec = (endPtm - startPtm) / PtmClock;
                if (sec > 0 && sec < 24 * 3600) prog.Seconds = sec;
            }

            // Mappa dei VOBU
            int vobAttr = U16(d, at + VvobAttr);
            int skip = ((vobAttr & 0x80) != 0 ? AdjVobSize : 0) + 2;
            int map = at + VvobSize + skip;

            if (map + VobuMapSize > d.Length)
            {
                note($"Programma {p + 1}: mappa dei VOBU oltre la fine del file.");
                r.Programs.Add(prog);
                continue;
            }

            int nrTimeInfo = U16(d, map + VobuMapNrTimeInfo);
            int nrVobuInfo = U16(d, map + VobuMapNrVobuInfo);
            long vobOffset = U32(d, map + VobuMapVobOffset);

            int vobuInfo = map + VobuMapSize + nrTimeInfo * TimeInfoSize;
            if (nrVobuInfo == 0)
            {
                note($"Programma {p + 1}: zero VOBU dichiarati.");
                r.Programs.Add(prog);
                continue;
            }
            if (vobuInfo + (long)nrVobuInfo * VobuInfoSize > d.Length)
            {
                note($"Programma {p + 1}: la tabella dei {nrVobuInfo} VOBU esce dal file " +
                     "(struttura non riconosciuta o IFO troncata).");
                r.Programs.Add(prog);
                continue;
            }

            long sectors = 0;
            bool badSize = false;
            for (int v = 0; v < nrVobuInfo; v++)
            {
                int size = U16(d, vobuInfo + v * VobuInfoSize + 1) & 0x03FF;   // dimensione del VOBU in settori
                if (size == 0) { badSize = true; break; }
                sectors += size;
            }

            if (badSize || sectors <= 0 || sectors > MaxVroSectors)
            {
                note($"Programma {p + 1}: dimensioni dei VOBU incoerenti ({sectors} settori); intervallo non usabile.");
                r.Programs.Add(prog);
                continue;
            }
            if (vobOffset < 0 || vobOffset + sectors > MaxVroSectors)
            {
                note($"Programma {p + 1}: il VOB comincia al settore {vobOffset} del VRO, fuori da un DVD plausibile.");
                r.Programs.Add(prog);
                continue;
            }

            long first = vroStartSector + vobOffset;
            long last = first + sectors - 1;

            prog.Ranges.Add((first, last));
            ranges.Add((first, last));
            withRange++;

            // Se i PTM non hanno dato niente resta solo una stima grossolana: un VOBU dura
            // mezzo secondo circa. Va detto, non spacciato per una durata letta.
            if (prog.Seconds <= 0) { prog.Seconds = nrVobuInfo * 0.5; stimate++; }

            r.Programs.Add(prog);
            log($"  {prog}");
        }

        // ---- Verdetto ----
        // I programmi non sono per forza in ordine dentro il VRO: dopo cancellazioni e
        // nuove registrazioni lo spazio viene riusato. Quindi non si pretende che siano
        // crescenti, ma due programmi non possono occupare gli stessi settori.
        bool overlap = false;
        ranges.Sort((a, b) => a.First.CompareTo(b.First));
        for (int i = 1; i < ranges.Count; i++)
            if (ranges[i].First <= ranges[i - 1].Last) { overlap = true; break; }

        if (overlap)
            note("Gli intervalli di alcuni programmi si sovrappongono: struttura letta male, dati non affidabili.");

        if (pgitEa > 0 && pgitEa + 1 < d.Length - pgitSa)
            log($"pgit_ea dichiara {pgitEa + 1} byte di tabella, il file ne ha {d.Length - pgitSa} da lì in poi.");

        if (withRange == 0)
        {
            if (withDate > 0)
                note($"Nessun intervallo di settori ricavabile, ma {withDate} date di registrazione su " +
                     $"{nrOfPrograms} programmi sono valide: utili per nominare i file, non per tagliarli.");
            else
                note("Non è stato ricavato nulla di utilizzabile da VR_MANGR.IFO.");
            r.IsValid = false;
            return;
        }

        if (overlap || withRange < r.Programs.Count)
        {
            note($"Ricavati {withRange} intervalli su {r.Programs.Count} programmi: lettura parziale, " +
                 "non ci si può fidare della divisione.");
            r.IsValid = false;
            return;
        }

        if (stimate > 0)
            note($"{stimate} programmi su {r.Programs.Count} non dichiarano i tempi di presentazione: " +
                 "la loro durata è stimata dal numero di VOBU (mezzo secondo l'uno), non letta.");

        r.IsValid = true;
        note($"VR_MANGR.IFO letta: {r.Programs.Count} programmi, {withDate} con data. " +
             "Struttura DVD-VR NON verificata su dischi reali: controllare il risultato.");
    }

    // =====================================================================================
    // Etichette dei programmi (Program Set Information)
    // =====================================================================================

    private static List<string> ReadLabels(byte[] d, long psiSa, int nrOfPrograms,
                                           Action<string> note, Action<string> log)
    {
        if (psiSa < MatSize || psiSa + PsiGiSize > d.Length) return null;
        int gi = (int)psiSa;

        int nrOfPsi = U8(d, gi + PsiGiNrOfPsi);
        if (nrOfPsi == 0 || nrOfPsi > MaxPrograms) return null;
        if (gi + PsiGiSize + (long)nrOfPsi * PsiSize > d.Length)
        {
            log("Tabella delle etichette troncata: i programmi resteranno senza nome.");
            return null;
        }

        // I programmi si susseguono linearmente nei "program set": è l'ipotesi di dvd-vr,
        // verificata da lui su tutti i dischi che ha visto.
        var labels = new List<string>(nrOfPrograms);
        int set = 0, inSet = 0, countInSet = 0;
        string current = null;

        for (int p = 0; p < nrOfPrograms; p++)
        {
            while (inSet >= countInSet && set < nrOfPsi)
            {
                int at = gi + PsiGiSize + set * PsiSize;
                countInSet = U16(d, at + PsiNrOfPrograms);
                if (countInSet <= 0 || countInSet > MaxPrograms) countInSet = 1;

                current = Latin1(d, at + PsiLabel, 64);
                if (string.IsNullOrWhiteSpace(current)) current = Latin1(d, at + PsiTitle, 64);
                if (string.IsNullOrWhiteSpace(current)) current = null;

                inSet = 0;
                set++;
            }
            if (set > nrOfPsi) break;

            labels.Add(current);
            inSet++;
        }

        while (labels.Count < nrOfPrograms) labels.Add(null);
        return labels;
    }

    // =====================================================================================
    // Data e ora
    // =====================================================================================

    /// <summary>
    /// Timestamp DVD-VR: 5 byte compattati, 40 bit in tutto.
    /// anno 14 bit | mese 4 | giorno 5 | ora 5 | minuti 6 | secondi 6.
    /// </summary>
    internal static DateTime? ParseTimestamp(byte[] d, int o, int minYear, int maxYear)
    {
        if (d == null || o < 0 || o + 5 > d.Length) return null;

        int b0 = d[o], b1 = d[o + 1], b2 = d[o + 2], b3 = d[o + 3], b4 = d[o + 4];

        int year = ((b0 << 8) | b1) >> 2;
        int month = ((b1 & 0x03) << 2) | (b2 >> 6);
        int day = (b2 & 0x3E) >> 1;
        int hour = ((b2 & 0x01) << 4) | (b3 >> 4);
        int minute = ((b3 & 0x0F) << 2) | (b4 >> 6);
        int second = b4 & 0x3F;

        if (year == 0) return null;                       // il registratore non ha scritto la data
        if (year < minYear || year > maxYear) return null;
        if (month < 1 || month > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        if (hour > 23 || minute > 59 || second > 59) return null;

        try { return new DateTime(year, month, day, hour, minute, second); }
        catch { return null; }
    }

    /// <summary>Scrive un timestamp nel formato DVD-VR. Serve ai test e a costruire file di prova.</summary>
    internal static void WriteTimestamp(byte[] d, int o, DateTime t)
    {
        if (d == null || o < 0 || o + 5 > d.Length) return;

        long v = ((long)t.Year << 26) | ((long)t.Month << 22) | ((long)t.Day << 17) |
                 ((long)t.Hour << 12) | ((long)t.Minute << 6) | (uint)t.Second;

        d[o] = (byte)(v >> 32);
        d[o + 1] = (byte)(v >> 24);
        d[o + 2] = (byte)(v >> 16);
        d[o + 3] = (byte)(v >> 8);
        d[o + 4] = (byte)v;
    }

    // =====================================================================================
    // Letture di base con controllo dei limiti
    // =====================================================================================

    private static byte U8(byte[] b, int o) => (uint)o < (uint)b.Length ? b[o] : (byte)0;

    private static int U16(byte[] b, int o)
        => o >= 0 && o + 2 <= b.Length ? ByteUtils.U16BE(b, o) : 0;

    private static long U32(byte[] b, int o)
        => o >= 0 && o + 4 <= b.Length ? ByteUtils.U32BE(b, o) : 0L;

    private static string Latin1(byte[] b, int o, int len)
    {
        if (o < 0 || len <= 0 || o + len > b.Length) return null;
        string s = ByteUtils.Latin1(b, o, len);
        if (string.IsNullOrWhiteSpace(s)) return null;

        // Le etichette non sono sempre terminate da NUL e possono contenere spazzatura.
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) { if (c < 32) break; sb.Append(c); }
        s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }
}
