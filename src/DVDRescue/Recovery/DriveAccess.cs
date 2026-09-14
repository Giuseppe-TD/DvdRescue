using DVDRescue.Native;

namespace DVDRescue.Recovery;

/// <summary>
/// Apertura del lettore e individuazione dell'area realmente scritta.
/// Sui dischi non finalizzati il lettore dichiara spesso valori ottimistici
/// o non ne dichiara affatto, quindi il dato va verificato leggendo.
/// </summary>
public static class DriveAccess
{
    public sealed class DriveOpenResult
    {
        public OpticalDrive Drive;
        public OpticalBlockSource Source;
        public long LastWrittenSector = -1;
        public string MediaText = "";
        public string DiscStatusText = "";
        public List<string> Notes = new();
    }

    public static DriveOpenResult Open(string driveLetter, int readSpeedKbPerSec,
                                       Action<string> log, CancellationToken ct)
    {
        log ??= _ => { };
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

            long lastWritten = -1;

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
                    if (end > lastWritten) lastWritten = end;
                }
            }

            if (lastWritten <= 0)
            {
                long capacity = drive.ReadCapacity();
                if (capacity > 0)
                {
                    lastWritten = capacity;
                    log($"Nessun dato utile dalle tracce: uso la capacità dichiarata ({capacity} settori).");
                }
                else
                {
                    lastWritten = 2_295_104;
                    log("Nessun dato dal lettore: parto dalla capacità di un DVD a strato singolo.");
                }
            }

            var source = new OpticalBlockSource(drive, lastWritten + 1, ownsDrive: false);

            long verified = VerifyLastReadable(source, lastWritten, log, ct);
            if (verified > 0 && verified != lastWritten)
                log($"Limite corretto dopo la verifica: ultimo settore leggibile {verified} invece di {lastWritten}.");
            if (verified > 0) lastWritten = verified;

            source.SetTotalBlocks(lastWritten + 1);

            result.Source = source;
            result.LastWrittenSector = lastWritten;

            log($"Area utilizzabile: {lastWritten + 1} settori, circa {(lastWritten + 1) * 2048.0 / 1048576.0:F0} MB.");
            return result;
        }
        catch
        {
            drive.Dispose();
            throw;
        }
    }

    private static long VerifyLastReadable(OpticalBlockSource source, long declared,
                                           Action<string> log, CancellationToken ct)
    {
        if (declared <= 0) return -1;

        if (!source.ProbeSector(0))
        {
            log("Nemmeno il primo settore è leggibile: disco vuoto o illeggibile.");
            return -1;
        }

        if (source.ProbeSector(declared))
        {
            long high = declared;
            long step = 1024;
            long ceiling = Math.Max(declared * 2, 12_500_000);   // oltre un Blu-ray a doppio strato

            while (high + step < ceiling && source.ProbeSector(high + step))
            {
                ct.ThrowIfCancellationRequested();
                high += step;
                step *= 2;
            }

            if (high == declared) return declared;

            long low = high, top = Math.Min(high + step, ceiling);
            while (low + 1 < top)
            {
                ct.ThrowIfCancellationRequested();
                long middle = low + (top - low) / 2;
                if (source.ProbeSector(middle)) low = middle; else top = middle;
            }
            return low;
        }

        log("L'ultimo settore dichiarato non è leggibile: cerco il limite reale.");

        long lo = 0, hi = declared;
        int iterations = 0;

        while (lo + 1 < hi && iterations++ < 40)
        {
            ct.ThrowIfCancellationRequested();
            long middle = lo + (hi - lo) / 2;
            if (source.ProbeSector(middle)) lo = middle; else hi = middle;
            if (iterations % 6 == 0) log($"  ricerca del limite fra i settori {lo} e {hi}...");
        }

        return lo;
    }
}
