using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DVDRescue.Native;

public sealed class ScsiResult
{
    public bool Success;
    public byte ScsiStatus;
    public byte SenseKey;
    public byte Asc;
    public byte Ascq;
    public int Win32Error;
    public string Message = "";

    public override string ToString() =>
        Success ? "OK"
                : $"{Message} (status=0x{ScsiStatus:X2} sense={SenseKey:X1}/{Asc:X2}/{Ascq:X2} win32={Win32Error})";
}

public sealed class DiscInformation
{
    /// <summary>0 = vuoto, 1 = incompleto (non finalizzato), 2 = finalizzato, 3 = random access (RW formattato)</summary>
    public int DiscStatus;
    public int LastSessionStatus;
    public bool Erasable;
    public int FirstTrack;
    public int Sessions;
    public int FirstTrackLastSession;
    public int LastTrackLastSession;
    public long LastPossibleLeadOut = -1;

    public string StatusText => DiscStatus switch
    {
        0 => "vuoto",
        1 => "NON finalizzato (incompleto/appendable)",
        2 => "finalizzato",
        3 => "accesso casuale (RW formattato)",
        _ => "sconosciuto"
    };
}

public sealed class TrackInformation
{
    public int TrackNumber;
    public int SessionNumber;
    public bool Blank;
    public bool Damage;
    public bool NwaValid;
    public bool LraValid;
    public long TrackStart;
    public long NextWritableAddress;
    public long FreeBlocks;
    public long TrackSize;
    public long LastRecordedAddress;

    /// <summary>Ultimo LBA che dovrebbe contenere dati, in base alle informazioni fornite dal drive.</summary>
    public long EstimatedLastWrittenLba
    {
        get
        {
            if (LraValid && LastRecordedAddress > 0) return LastRecordedAddress;
            if (NwaValid && NextWritableAddress > TrackStart) return NextWritableAddress - 1;
            if (TrackSize > 0) return TrackStart + TrackSize - 1;
            return -1;
        }
    }
}

public sealed class DvdPhysicalInfo
{
    public int BookType;
    public int PartVersion;
    public int Layers;
    public long DataAreaStart;
    public long DataAreaEnd;
    public long Layer0End;

    public string BookTypeText => BookType switch
    {
        0 => "DVD-ROM",
        1 => "DVD-RAM",
        2 => "DVD-R",
        3 => "DVD-RW",
        4 => "HD DVD-ROM",
        9 => "DVD+RW",
        10 => "DVD+R",
        13 => "DVD+RW DL",
        14 => "DVD+R DL",
        _ => $"tipo 0x{BookType:X}"
    };
}

/// <summary>
/// Accesso raw a un'unità ottica tramite SCSI pass-through (comandi MMC).
/// È il meccanismo che permette di leggere i dischi che Windows non riesce a montare
/// perché non finalizzati.
/// </summary>
public sealed class OpticalDrive : IDisposable
{
    public const int SectorSize = 2048;

    private SafeFileHandle _handle;
    private AlignedBuffer _buffer;
    private readonly object _lock = new();

    public string DriveLetter { get; }

    private OpticalDrive(string driveLetter, SafeFileHandle handle)
    {
        DriveLetter = driveLetter;
        _handle = handle;
        _buffer = new AlignedBuffer(256 * 1024);
    }

    /// <summary>True se l'unità è stata aperta come device fisico invece che per lettera.</summary>
    public bool OpenedAsPhysicalDevice { get; private set; }

    /// <param name="driveLetter">Lettera di unità, es. "D" oppure "D:".</param>
    public static OpticalDrive Open(string driveLetter)
    {
        string letter = driveLetter.TrimEnd(':', '\\', '/').Trim();

        var handle = TryOpenPath($@"\\.\{letter}:", out int lastError);
        bool physical = false;

        if (handle == null)
        {
            // Windows può rifiutare il volume quando il disco non ha un filesystem montabile:
            // in quel caso si prova il device fisico, che risponde comunque ai comandi MMC.
            for (int i = 0; i < 10 && handle == null; i++)
            {
                var h = TryOpenPath($@"\\.\CdRom{i}", out _);
                if (h == null) continue;

                var candidate = new OpticalDrive(letter, h);
                if (candidate.TestUnitReady())
                {
                    handle = h;
                    physical = true;
                    candidate.OpenedAsPhysicalDevice = true;
                    candidate.AllowExtendedDasdIo();
                    return candidate;
                }

                candidate.Dispose();
            }
        }

        if (handle == null)
            throw new Win32Exception(lastError,
                $"Impossibile aprire l'unità {letter}: (errore {lastError}). " +
                "Avvia DVDRescue come amministratore e chiudi i programmi che stanno usando il lettore.");

        var drive = new OpticalDrive(letter, handle) { OpenedAsPhysicalDevice = physical };
        drive.AllowExtendedDasdIo();
        return drive;
    }

    private static SafeFileHandle TryOpenPath(string path, out int lastError)
    {
        lastError = 0;

        var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();

            // ripiego in sola lettura: su alcuni sistemi basta per READ(10)
            handle = NativeMethods.CreateFile(
                path,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);
        }

        if (handle.IsInvalid)
        {
            lastError = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private void AllowExtendedDasdIo()
    {
        try
        {
            NativeMethods.DeviceIoControl(_handle, NativeMethods.FSCTL_ALLOW_EXTENDED_DASD_IO,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ SCSI

    /// <summary>
    /// Esegue un CDB SCSI. Se dataIn è true i dati letti finiscono in <paramref name="data"/>.
    /// </summary>
    public ScsiResult Execute(byte[] cdb, byte[] data, int dataLength, bool dataIn, int timeoutSeconds = 20)
    {
        lock (_lock)
        {
            if (dataLength > _buffer.Size)
            {
                _buffer.Dispose();
                _buffer = new AlignedBuffer(Math.Max(dataLength, 64 * 1024));
            }

            if (!dataIn && data != null && dataLength > 0)
                Marshal.Copy(data, 0, _buffer.Pointer, dataLength);
            else if (dataLength > 0)
                _buffer.Clear(dataLength);

            var sptdwb = new NativeMethods.SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER
            {
                Spt = new NativeMethods.SCSI_PASS_THROUGH_DIRECT
                {
                    Length = (ushort)Marshal.SizeOf<NativeMethods.SCSI_PASS_THROUGH_DIRECT>(),
                    CdbLength = (byte)cdb.Length,
                    SenseInfoLength = 32,
                    DataIn = dataLength == 0
                        ? NativeMethods.SCSI_IOCTL_DATA_UNSPECIFIED
                        : (dataIn ? NativeMethods.SCSI_IOCTL_DATA_IN : NativeMethods.SCSI_IOCTL_DATA_OUT),
                    DataTransferLength = (uint)dataLength,
                    TimeOutValue = (uint)timeoutSeconds,
                    DataBuffer = dataLength == 0 ? IntPtr.Zero : _buffer.Pointer,
                    Cdb = new byte[16]
                },
                Sense = new byte[32]
            };

            Array.Copy(cdb, sptdwb.Spt.Cdb, Math.Min(cdb.Length, 16));
            sptdwb.Spt.SenseInfoOffset =
                (uint)Marshal.OffsetOf<NativeMethods.SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>("Sense").ToInt32();

            int structSize = Marshal.SizeOf<NativeMethods.SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>();
            IntPtr pSptd = Marshal.AllocHGlobal(structSize);
            var result = new ScsiResult();

            try
            {
                Marshal.StructureToPtr(sptdwb, pSptd, false);

                bool ok = NativeMethods.DeviceIoControl(
                    _handle,
                    NativeMethods.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                    pSptd, (uint)structSize,
                    pSptd, (uint)structSize,
                    out _, IntPtr.Zero);

                var back = Marshal.PtrToStructure<NativeMethods.SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>(pSptd);

                result.Win32Error = ok ? 0 : Marshal.GetLastWin32Error();
                result.ScsiStatus = back.Spt.ScsiStatus;

                if (back.Sense != null && back.Sense.Length >= 14)
                {
                    result.SenseKey = (byte)(back.Sense[2] & 0x0F);
                    result.Asc = back.Sense[12];
                    result.Ascq = back.Sense[13];
                }

                result.Success = ok && back.Spt.ScsiStatus == 0;

                if (!result.Success)
                    result.Message = DescribeSense(result);
                else if (dataIn && data != null && dataLength > 0)
                    _buffer.CopyTo(data, 0, dataLength);

                return result;
            }
            finally
            {
                Marshal.DestroyStructure<NativeMethods.SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>(pSptd);
                Marshal.FreeHGlobal(pSptd);
            }
        }
    }

    private static string DescribeSense(ScsiResult r)
    {
        if (r.SenseKey == 0 && r.Win32Error != 0)
            return $"errore di sistema {r.Win32Error}";

        return (r.SenseKey, r.Asc, r.Ascq) switch
        {
            (0x02, 0x3A, _) => "nessun disco nel lettore",
            (0x02, 0x04, _) => "lettore non pronto (disco in avvio)",
            (0x03, 0x11, _) => "errore di lettura non correggibile (settore danneggiato)",
            (0x03, 0x02, _) => "impossibile trovare il settore",
            (0x05, 0x20, _) => "comando non supportato dal lettore",
            (0x05, 0x21, _) => "indirizzo oltre la fine del disco",
            (0x05, 0x64, _) => "modalità illegale per questa traccia (area non scritta)",
            (0x05, 0x24, _) => "CDB non valido",
            (0x06, 0x28, _) => "disco cambiato",
            (0x08, _, _) => "area vuota / non registrata",
            _ => $"errore SCSI sense {r.SenseKey:X1}/{r.Asc:X2}/{r.Ascq:X2}"
        };
    }

    // --------------------------------------------------------------- comandi

    /// <summary>
    /// READ(10) — lettura di settori dati da 2048 byte.
    /// Il timeout conta: su un'area non scritta il lettore ritenta per conto suo prima di
    /// rispondere, quindi un valore alto trasforma qualche migliaio di settori vuoti in minuti.
    /// </summary>
    public ScsiResult ReadSectors(long lba, int count, byte[] destination, int destinationOffset,
                                  int timeoutSeconds = 10)
    {
        if (count <= 0) return new ScsiResult { Success = true };

        var cdb = new byte[10];
        cdb[0] = 0x28;
        cdb[2] = (byte)(lba >> 24);
        cdb[3] = (byte)(lba >> 16);
        cdb[4] = (byte)(lba >> 8);
        cdb[5] = (byte)lba;
        cdb[7] = (byte)(count >> 8);
        cdb[8] = (byte)count;

        int len = count * SectorSize;
        lock (_lock)
        {
            if (len > _buffer.Size)
            {
                _buffer.Dispose();
                _buffer = new AlignedBuffer(len);
            }
        }

        var res = Execute(cdb, null, len, true, timeoutSeconds);
        if (res.Success)
            lock (_lock) _buffer.CopyTo(destination, destinationOffset, len);

        return res;
    }

    /// <summary>READ CD (0xBE) in modalità dati: alcuni drive lo accettano dove READ(10) fallisce.</summary>
    public ScsiResult ReadSectorsAlternate(long lba, int count, byte[] destination, int destinationOffset,
                                           int timeoutSeconds = 10)
    {
        var cdb = new byte[12];
        cdb[0] = 0xBE;
        cdb[1] = 0x00;
        cdb[2] = (byte)(lba >> 24);
        cdb[3] = (byte)(lba >> 16);
        cdb[4] = (byte)(lba >> 8);
        cdb[5] = (byte)lba;
        cdb[6] = (byte)(count >> 16);
        cdb[7] = (byte)(count >> 8);
        cdb[8] = (byte)count;
        cdb[9] = 0x10; // solo User Data (2048 byte)

        int len = count * SectorSize;
        lock (_lock)
        {
            if (len > _buffer.Size)
            {
                _buffer.Dispose();
                _buffer = new AlignedBuffer(len);
            }
        }

        var res = Execute(cdb, null, len, true, timeoutSeconds);
        if (res.Success)
            lock (_lock) _buffer.CopyTo(destination, destinationOffset, len);

        return res;
    }

    /// <summary>READ DISC INFORMATION (0x51): dice se il disco è finalizzato o no.</summary>
    public DiscInformation ReadDiscInformation()
    {
        var data = new byte[34];
        var cdb = new byte[10];
        cdb[0] = 0x51;
        cdb[7] = 0;
        cdb[8] = (byte)data.Length;

        var res = Execute(cdb, data, data.Length, true, 15);
        if (!res.Success) return null;

        return new DiscInformation
        {
            DiscStatus = data[2] & 0x03,
            LastSessionStatus = (data[2] >> 2) & 0x03,
            Erasable = (data[2] & 0x10) != 0,
            FirstTrack = data[3],
            Sessions = data[4] | (data[9] << 8),
            FirstTrackLastSession = data[5] | (data[10] << 8),
            LastTrackLastSession = data[6] | (data[11] << 8),
            LastPossibleLeadOut = ReadBe32(data, 20)
        };
    }

    /// <summary>READ TRACK INFORMATION (0x52): start, dimensione e ultimo settore scritto di una traccia.</summary>
    public TrackInformation ReadTrackInformation(int trackNumber)
    {
        var data = new byte[36];
        var cdb = new byte[10];
        cdb[0] = 0x52;
        cdb[1] = 0x01;                       // address type = logical track number
        cdb[2] = (byte)(trackNumber >> 24);
        cdb[3] = (byte)(trackNumber >> 16);
        cdb[4] = (byte)(trackNumber >> 8);
        cdb[5] = (byte)trackNumber;
        cdb[7] = 0;
        cdb[8] = (byte)data.Length;

        var res = Execute(cdb, data, data.Length, true, 15);
        if (!res.Success) return null;

        return new TrackInformation
        {
            TrackNumber = data[2] | (data[32] << 8),
            SessionNumber = data[3] | (data[33] << 8),
            Damage = (data[5] & 0x20) != 0,
            Blank = (data[6] & 0x40) != 0,
            NwaValid = (data[7] & 0x01) != 0,
            LraValid = (data[7] & 0x02) != 0,
            TrackStart = ReadBe32(data, 8),
            NextWritableAddress = ReadBe32(data, 12),
            FreeBlocks = ReadBe32(data, 16),
            TrackSize = ReadBe32(data, 24),
            LastRecordedAddress = ReadBe32(data, 28)
        };
    }

    /// <summary>READ DVD STRUCTURE (0xAD) formato 0x00: inizio/fine dell'area dati fisica.</summary>
    public DvdPhysicalInfo ReadDvdPhysicalInfo()
    {
        var data = new byte[24];
        var cdb = new byte[12];
        cdb[0] = 0xAD;
        cdb[6] = 0x00;                 // layer
        cdb[7] = 0x00;                 // format: physical format information
        cdb[8] = (byte)(data.Length >> 8);
        cdb[9] = (byte)data.Length;

        var res = Execute(cdb, data, data.Length, true, 15);
        if (!res.Success) return null;

        // data[0..3] = header, la struttura fisica inizia a data[4]
        int b4 = data[4];
        int b6 = data[6];

        long start = ((long)data[9] << 16) | ((long)data[10] << 8) | data[11];
        long end = ((long)data[13] << 16) | ((long)data[14] << 8) | data[15];
        long l0end = ((long)data[17] << 16) | ((long)data[18] << 8) | data[19];

        return new DvdPhysicalInfo
        {
            BookType = (b4 >> 4) & 0x0F,
            PartVersion = b4 & 0x0F,
            Layers = ((b6 >> 5) & 0x03) + 1,
            DataAreaStart = start,
            DataAreaEnd = end,
            Layer0End = l0end
        };
    }

    /// <summary>READ CAPACITY(10): ultimo LBA dichiarato dal drive.</summary>
    public long ReadCapacity()
    {
        var data = new byte[8];
        var cdb = new byte[10];
        cdb[0] = 0x25;

        var res = Execute(cdb, data, data.Length, true, 15);
        if (!res.Success) return -1;

        return ReadBe32(data, 0);
    }

    /// <summary>TEST UNIT READY (0x00).</summary>
    public bool TestUnitReady()
    {
        var cdb = new byte[6];
        cdb[0] = 0x00;
        return Execute(cdb, null, 0, true, 10).Success;
    }

    /// <summary>SET CD SPEED (0xBB): rallentare aiuta molto sui dischi rovinati.</summary>
    public bool SetReadSpeed(int kilobytesPerSecond)
    {
        var cdb = new byte[12];
        cdb[0] = 0xBB;
        cdb[2] = (byte)(kilobytesPerSecond >> 8);
        cdb[3] = (byte)kilobytesPerSecond;
        cdb[4] = 0xFF;
        cdb[5] = 0xFF;
        return Execute(cdb, null, 0, true, 15).Success;
    }

    /// <summary>START/STOP UNIT (0x1B) con LoEj: espelle il disco.</summary>
    public bool Eject()
    {
        var cdb = new byte[6];
        cdb[0] = 0x1B;
        cdb[4] = 0x02; // LoEj = 1, Start = 0
        return Execute(cdb, null, 0, true, 20).Success;
    }

    public static long ReadBe32(byte[] b, int offset) =>
        ((long)b[offset] << 24) | ((long)b[offset + 1] << 16) | ((long)b[offset + 2] << 8) | b[offset + 3];

    public void Dispose()
    {
        _buffer?.Dispose();
        _handle?.Dispose();
        _handle = null;
    }
}
