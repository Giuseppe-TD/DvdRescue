using System.Text;

namespace DVDRescue.Core;

/// <summary>Letture da buffer e conversioni di data usate da tutti i lettori di filesystem.</summary>
public static class ByteUtils
{
    public static ushort U16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

    public static uint U32LE(byte[] b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    public static ulong U64LE(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
        return v;
    }

    public static int I32LE(byte[] b, int o) => unchecked((int)U32LE(b, o));

    public static short I16LE(byte[] b, int o) => unchecked((short)U16LE(b, o));

    public static ushort U16BE(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

    public static uint U32BE(byte[] b, int o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    public static ulong U64BE(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        return v;
    }

    /// <summary>Interi a 24 bit big endian (usati nelle strutture DVD).</summary>
    public static uint U24BE(byte[] b, int o) => (uint)((b[o] << 16) | (b[o + 1] << 8) | b[o + 2]);

    public static string Ascii(byte[] b, int o, int len)
    {
        if (o < 0 || len <= 0 || o + len > b.Length) return "";
        return Encoding.ASCII.GetString(b, o, len).TrimEnd('\0', ' ');
    }

    public static string Latin1(byte[] b, int o, int len)
    {
        if (o < 0 || len <= 0 || o + len > b.Length) return "";
        return Encoding.Latin1.GetString(b, o, len).TrimEnd('\0', ' ');
    }

    public static string Utf16LE(byte[] b, int o, int byteLen)
    {
        if (o < 0 || byteLen <= 0 || o + byteLen > b.Length) return "";
        return Encoding.Unicode.GetString(b, o, byteLen).TrimEnd('\0');
    }

    public static string Utf16BE(byte[] b, int o, int byteLen)
    {
        if (o < 0 || byteLen <= 0 || o + byteLen > b.Length) return "";
        return Encoding.BigEndianUnicode.GetString(b, o, byteLen).TrimEnd('\0');
    }

    /// <summary>Data/ora in formato DOS (FAT).</summary>
    public static DateTime? DosDateTime(ushort date, ushort time, byte fineTenths = 0)
    {
        if (date == 0) return null;
        try
        {
            int year = 1980 + ((date >> 9) & 0x7F);
            int month = (date >> 5) & 0x0F;
            int day = date & 0x1F;
            int hour = (time >> 11) & 0x1F;
            int minute = (time >> 5) & 0x3F;
            int second = (time & 0x1F) * 2 + fineTenths / 100;

            if (month < 1 || month > 12 || day < 1 || day > 31 || hour > 23 || minute > 59 || second > 59)
                return null;

            return new DateTime(year, month, day, hour, minute, second);
        }
        catch { return null; }
    }

    /// <summary>Secondi dal 1970 (ext, HFS usa un'altra epoca).</summary>
    public static DateTime? UnixTime(long seconds)
    {
        if (seconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToLocalTime(); }
        catch { return null; }
    }

    /// <summary>Intervalli da 100 ns dal 1601 (NTFS).</summary>
    public static DateTime? NtfsTime(ulong value)
    {
        if (value == 0) return null;
        try { return DateTime.FromFileTimeUtc((long)value).ToLocalTime(); }
        catch { return null; }
    }

    /// <summary>Secondi dal 1904 (HFS/HFS+).</summary>
    public static DateTime? HfsTime(uint value)
    {
        if (value == 0) return null;
        try { return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(value).ToLocalTime(); }
        catch { return null; }
    }

    /// <summary>Timestamp ECMA-167 (UDF), 12 byte.</summary>
    public static DateTime? UdfTime(byte[] b, int o)
    {
        if (o < 0 || o + 12 > b.Length) return null;
        try
        {
            int year = I16LE(b, o + 2);
            int month = b[o + 4], day = b[o + 5], hour = b[o + 6], min = b[o + 7], sec = b[o + 8];

            if (year < 1970 || year > 2200 || month < 1 || month > 12 || day < 1 || day > 31) return null;
            if (hour > 23 || min > 59 || sec > 59) return null;

            return new DateTime(year, month, day, hour, min, sec);
        }
        catch { return null; }
    }

    /// <summary>Data ISO 9660 a 7 byte (directory record).</summary>
    public static DateTime? Iso9660Time(byte[] b, int o)
    {
        if (o + 7 > b.Length) return null;
        try
        {
            int year = 1900 + b[o];
            int month = b[o + 1], day = b[o + 2], hour = b[o + 3], min = b[o + 4], sec = b[o + 5];
            if (year < 1970 || year > 2200 || month < 1 || month > 12 || day < 1 || day > 31) return null;
            if (hour > 23 || min > 59 || sec > 59) return null;
            return new DateTime(year, month, day, hour, min, sec);
        }
        catch { return null; }
    }

    /// <summary>Data ISO 9660 a 17 byte in cifre ASCII (volume descriptor).</summary>
    public static DateTime? Iso9660LongTime(byte[] b, int o)
    {
        if (o + 16 > b.Length) return null;
        try
        {
            string s = Encoding.ASCII.GetString(b, o, 16);
            if (!int.TryParse(s.Substring(0, 4), out int year) || year < 1970) return null;
            int month = int.Parse(s.Substring(4, 2));
            int day = int.Parse(s.Substring(6, 2));
            int hour = int.Parse(s.Substring(8, 2));
            int min = int.Parse(s.Substring(10, 2));
            int sec = int.Parse(s.Substring(12, 2));
            if (month < 1 || month > 12 || day < 1 || day > 31) return null;
            return new DateTime(year, month, day, hour, min, sec);
        }
        catch { return null; }
    }

    /// <summary>Nome file ripulito dai caratteri non ammessi su Windows.</summary>
    public static string SafeFileName(string name, string fallback = "senza_nome")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c < 32 || c == '<' || c == '>' || c == ':' || c == '"' || c == '/' ||
                      c == '\\' || c == '|' || c == '?' || c == '*' ? '_' : c);

        string result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0) return fallback;
        if (result.Length > 200) result = result[..200];

        string upper = Path.GetFileNameWithoutExtension(result).ToUpperInvariant();
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                              "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                              "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (Array.IndexOf(reserved, upper) >= 0) result = "_" + result;

        return result;
    }
}
