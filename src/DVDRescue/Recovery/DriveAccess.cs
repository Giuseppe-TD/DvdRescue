using System.Diagnostics;
using DVDRescue.Native;

namespace DVDRescue.Recovery;

/// <summary>
/// Apertura del lettore e, solo quando serve davvero, individuazione dell'area scritta.
///
/// La differenza di velocità sta tutta in quel "quando serve davvero": interrogare il lettore
/// costa qualche decimo di secondo, mentre cercare a tentoni l'ultimo settore leggibile costa
/// minuti, perché ogni sondaggio a vuoto fa ritentare il lettore. Se il disco ha un filesystem
/// e le sue strutture di navigazione — il caso normale — quel limite non serve a nulla:
/// si leggono le IFO e si sa già dove stanno i video. Per questo la ricerca viene rimandata
/// al momento in cui l'unica strada rimasta è la scansione dei settori.
/// </summary>
public static class DriveAccess
{
    public sealed class DriveOpenResult
    {
        public OpticalDrive Drive;
        public OpticalBlockSource Source;

        /// <summary>Stima dichiarata dal lettore, non ancora verificata leggendo.</summary>
        public long EstimatedLastSector = -1;

        public bool LimitVerified;
        public string MediaText = "";
        public string DiscStatusText = "";
        public List<string> Notes = new();

        /// <summary>
        /// I tratti che il lettore dichiara scritti. Su un DVD-R di videocamera sono più di uno,
        /// separati da zone mai scritte: saperlo evita sia di cercare il video dove non c'è, sia
        /// di dare il disco per illeggibile perché non risponde all'inizio.
        /// </summary>
        public List<SectorRange> Written = new();

        /// <summary>Primo settore dichiarato scritto, -1 se non si sa.</summary>
        public long FirstWrittenSector => Written.Count > 0 ? Written[0].First : -1;
    }

    public static DriveOpenResult Open(string driveLetter, int readSpeedKbPerSec, bool thorough,
                                       Action<string> log, CancellationToken ct)
    {
        log ??= _ => { };
        var watch = Stopwatch.StartNew();

        var drive = OpticalDrive.Open(driveLetter);
        var result = new DriveOpenResult { Drive = drive };

        try
        {
            if (drive.OpenedAsPhysicalDevice)
                log("Il volume non era accessibile: uso direttamente il device fisico del lettore.");

            if (readSpeedKbPerSec > 0)
            {
                bool set = drive.SetReadSpeed(readSpeedKbPerSec);
                log(set
                    ? $"Velocità di lettura limitata a circa {readSpeedKbPerSec / 1385.0:F0}x."
                    : "Il lettore non ha accettato la limitazione di velocità.");
            }

            if (!drive.TestUnitReady())
            {
                log("Attendo che il lettore avvii il disco...");
                for (int i = 0; i < 20 && !drive.TestUnitReady(); i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(1000);
                }
            }

            var info = drive.ReadDiscInformation();
            if (info != null)
            {
                result.DiscStatusText = info.StatusText;
                log($"Stato del disco: {info.StatusText}, {info.Sessions} sessione/i, tracce {info.FirstTrack}–{info.LastTrackLastSession}.");
                if (info.DiscStatus == 0)
                    result.Notes.Add("Il lettore dichiara il disco vuoto: con i dischi delle videocamere capita. Si prova comunque a leggere.");
            }

            var physical = drive.ReadDvdPhysicalInfo();
            if (physical != null)
            {
                result.MediaText = $"{physical.BookTypeText}, {physical.Layers} strato/i";
                log($"Supporto: {physical.BookTypeText}, area dati {physical.DataAreaStart}–{physical.DataAreaEnd}.");
            }

            long lastSector = -1;

            if (info != null)
            {
                int firstTrack = Math.Max(1, info.FirstTrack);
                int lastTrack = Math.Max(firstTrack, info.LastTrackLastSession);

                for (int t = firstTrack; t <= Math.Min(lastTrack, 99); t++)
                {
                    ct.ThrowIfCancellationRequested();
                    var track = drive.ReadTrackInformation(t);
                    if (track == null || track.Blank) continue;

                    long end = track.EstimatedLastWrittenLba;
                    log($"Traccia {t}: inizio {track.TrackStart}, dimensione {track.TrackSize}, ultimo scritto {(end >= 0 ? end.ToString() : "n/d")}.");
                    if (end > lastSector) lastSector = end;

                    if (end >= track.TrackStart && track.TrackStart >= 0)
                        result.Written.Add(new SectorRange(track.TrackStart, end));
                }
            }

            // Tratti scritti in ordine e senza sovrapposizioni: da qui in poi sono loro a dire
            // dove guardare, invece dell'intervallo "da zero fino alla fine" che su un disco a
            // più bordi comprende decine di megabyte mai scritti.
            result.Written = Merge(result.Written);

            if (result.Written.Count > 0)
            {
                long written = result.Written.Sum(r => r.Count);
                log($"Tratti scritti: {string.Join(", ", result.Written)} " +
                    $"({written * 2048.0 / 1048576.0:F0} MB effettivi).");

                if (result.Written[0].First > 0)
                    result.Notes.Add($"I dati non cominciano dall'inizio del disco ma dal settore " +
                                     $"{result.Written[0].First}: è normale sui DVD scritti a più riprese.");
            }

            // La calibrazione va fatta adesso, non prima: senza sapere dove sta la roba proverebbe
            // a leggere il settore 0, che su questi dischi non risponde, e si ridurrebbe a fidarsi
            // di quello che dichiara il driver.
            drive.CalibrateTransferSize(log, result.Written.Select(r => r.First));

            if (lastSector <= 0)
            {
                long capacity = drive.ReadCapacity();
                if (capacity > 0)
                {
                    lastSector = capacity;
                    log($"Le tracce non dicono nulla di utile: uso la capacità dichiarata ({capacity} settori).");
                }
                else
                {
                    lastSector = 2_295_104;
                    log("Il lettore non fornisce dati: parto dalla capacità di un DVD a strato singolo.");
                }
            }

            var source = new OpticalBlockSource(drive, lastSector + 1, ownsDrive: false)
            {
                Effort = thorough ? ReadEffort.Thorough : ReadEffort.Fast,
                SpeedChosenByUser = readSpeedKbPerSec > 0,
                Log = log
            };

            result.Source = source;
            result.EstimatedLastSector = lastSector;

            log($"Area dichiarata: {lastSector + 1} settori, circa {(lastSector + 1) * 2048.0 / 1048576.0:F0} MB " +
                $"(interrogazione completata in {watch.ElapsedMilliseconds} ms).");

            return result;
        }
        catch
        {
            drive.Dispose();
            throw;
        }
    }

    /// <summary>Unisce e ordina i tratti, fondendo quelli attaccati o sovrapposti.</summary>
    public static List<SectorRange> Merge(IEnumerable<SectorRange> ranges)
    {
        var sorted = ranges.Where(r => r.Last >= r.First).OrderBy(r => r.First).ToList();
        var merged = new List<SectorRange>();

        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.First <= merged[^1].Last + 1)
            {
                merged[^1] = new SectorRange(merged[^1].First, Math.Max(merged[^1].Last, range.Last));
                continue;
            }
            merged.Add(range);
        }

        return merged;
    }

    /// <summary>
    /// Trova l'ultimo settore davvero leggibile. Va chiamata solo prima di una scansione:
    /// ogni sondaggio a vuoto costa secondi, quindi il numero di tentativi è tenuto basso.
    /// </summary>
    /// <param name="written">
    /// Tratti che il lettore dichiara scritti. Senza di questi si finisce per giudicare il disco
    /// dal settore 0, che su un DVD-R di videocamera non risponde quasi mai — e si scarta come
    /// illeggibile un disco pieno di riprese.
    /// </param>
    public static long VerifyWrittenLimit(OpticalBlockSource source, long declared,
                                          IReadOnlyList<SectorRange> written,
                                          Action<string> log, CancellationToken ct)
    {
        log ??= _ => { };
        if (declared <= 0) return -1;

        var watch = Stopwatch.StartNew();

        // Un punto d'appoggio leggibile: prima gli inizi dei tratti dichiarati scritti, poi
        // l'inizio del disco. Basta che ne risponda uno.
        long anchor = -1;

        if (written != null)
            foreach (var range in written)
            {
                ct.ThrowIfCancellationRequested();
                if (source.ProbeSector(range.First)) { anchor = range.First; break; }
            }

        if (anchor < 0 && source.ProbeSector(0)) anchor = 0;

        if (anchor < 0)
        {
            log("Nessun settore leggibile fra quelli dichiarati scritti: disco vuoto o illeggibile.");
            return -1;
        }

        if (anchor > 0)
            log($"L'inizio del disco non risponde, ma il settore {anchor} sì: i dati cominciano da lì.");

        if (source.ProbeSector(declared))
        {
            log($"L'ultimo settore dichiarato ({declared}) è leggibile: limite confermato in {watch.ElapsedMilliseconds} ms.");
            return declared;
        }

        log("L'ultimo settore dichiarato non è leggibile: cerco il limite reale (pochi tentativi).");

        long low = anchor, high = declared;
        int probes = 0;
        const int maxProbes = 14;     // precisione di circa 1/16000 del disco: più che sufficiente

        while (low + 1 < high && probes < maxProbes)
        {
            ct.ThrowIfCancellationRequested();
            long middle = low + (high - low) / 2;
            probes++;

            if (source.ProbeSector(middle)) low = middle; else high = middle;
        }

        log($"Limite individuato al settore {low} con {probes} sondaggi in {watch.ElapsedMilliseconds} ms.");
        return low;
    }
}
