using System.Runtime.InteropServices;

namespace DVDRescue.Native;

/// <summary>
/// Buffer non gestito allineato a 4096 byte: richiesto da IOCTL_SCSI_PASS_THROUGH_DIRECT,
/// che vuole un DataBuffer allineato all'alignment mask del device.
/// </summary>
internal sealed class AlignedBuffer : IDisposable
{
    private IntPtr _raw;

    public IntPtr Pointer { get; private set; }
    public int Size { get; }

    public AlignedBuffer(int size, int alignment = 4096)
    {
        Size = size;
        _raw = Marshal.AllocHGlobal(size + alignment);
        long addr = _raw.ToInt64();
        long aligned = (addr + alignment - 1) & ~((long)alignment - 1);
        Pointer = new IntPtr(aligned);
    }

    public void CopyTo(byte[] destination, int destinationOffset, int count)
    {
        Marshal.Copy(Pointer, destination, destinationOffset, count);
    }

    public byte[] ToArray(int count)
    {
        var b = new byte[count];
        Marshal.Copy(Pointer, b, 0, count);
        return b;
    }

    public void Clear(int count)
    {
        for (int i = 0; i < count; i += 8)
        {
            if (i + 8 <= count) Marshal.WriteInt64(Pointer, i, 0);
            else Marshal.WriteByte(Pointer, i, 0);
        }
    }

    public void Dispose()
    {
        if (_raw != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_raw);
            _raw = IntPtr.Zero;
            Pointer = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~AlignedBuffer() => Dispose();
}
