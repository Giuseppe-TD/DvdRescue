using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DVDRescue.Native;

/// <summary>
/// P/Invoke minimi per accesso raw al lettore ottico e SCSI pass-through (MMC).
/// Il layout delle strutture assume processo a 64 bit (il csproj forza PlatformTarget x64).
/// </summary>
internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;

    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    public const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;

    /// <summary>
    /// IOCTL_STORAGE_QUERY_PROPERTY: con StorageAdapterProperty dice quanti byte al massimo
    /// l'adattatore accetta in un solo comando. È il numero che decide la velocità di tutto:
    /// una richiesta più grande viene rifiutata a prescindere da com'è messo il disco.
    /// </summary>
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    public const int StorageAdapterProperty = 1;
    public const int PropertyStandardQuery = 0;

    public const uint IOCTL_STORAGE_CHECK_VERIFY = 0x002D4800;
    public const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    public const uint IOCTL_STORAGE_LOAD_MEDIA = 0x002D480C;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;

    public const byte SCSI_IOCTL_DATA_OUT = 0;
    public const byte SCSI_IOCTL_DATA_IN = 1;
    public const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER
    {
        public SCSI_PASS_THROUGH_DIRECT Spt;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Sense;
    }
}
