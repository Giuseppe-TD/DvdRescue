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
                }
            }

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
                ThoroughMode = thorough
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

    /// <summary>
    /// Trova l'ultimo settore davvero leggibile. Va chiamata solo prima di una scansione:
    /// ogni sondaggio a vuoto costa secondi, quindi il numero di tentativi è tenuto basso.
    /// </summary>
    public static long VerifyWrittenLimit(OpticalBlockSource source, long declared,
                                          Action<string> log, CancellationToken ct)
    {
        log ??= _ => { };
        if (declared <= 0) return -1;

        var watch = Stopwatch.StartNew();

        if (!source.ProbeSector(0))
        {
            log("Nemmeno il primo settore è leggibile: disco vuoto o illeggibile.");
            return -1;
        }

        if (source.ProbeSector(declared))
        {
            log($"L'ultimo settore dichiarato ({declared}) è leggibile: limite confermato in {watch.ElapsedMilliseconds} ms.");
            return declared;
        }

        log("L'ultimo settore dichiarato non è leggibile: cerco il limite reale (pochi tentativi).");

        long low = 0, high = declared;
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
