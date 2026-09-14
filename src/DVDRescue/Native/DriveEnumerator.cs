namespace DVDRescue.Native;

public sealed class OpticalDriveEntry
{
    public string Letter = "";
    public string Description = "";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Description) ? $"{Letter}:" : $"{Letter}:  —  {Description}";
}

public static class DriveEnumerator
{
    /// <summary>
    /// Elenca le unità ottiche. Usa DriveInfo (non richiede WMI) e prova ad arricchire
    /// la descrizione interrogando direttamente il drive con INQUIRY.
    /// </summary>
    public static List<OpticalDriveEntry> List()
    {
        var result = new List<OpticalDriveEntry>();

        foreach (var di in DriveInfo.GetDrives())
        {
            if (di.DriveType != DriveType.CDRom) continue;

            string letter = di.Name.TrimEnd('\\', ':', '/');
            var entry = new OpticalDriveEntry { Letter = letter };

            try
            {
                entry.Description = Inquiry(letter);
            }
            catch
            {
                entry.Description = "";
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>INQUIRY (0x12): vendor + product del lettore.</summary>
    private static string Inquiry(string letter)
    {
        try
        {
            using var drive = OpticalDrive.Open(letter);
            var data = new byte[96];
            var cdb = new byte[6];
            cdb[0] = 0x12;
            cdb[4] = (byte)data.Length;

            var res = drive.Execute(cdb, data, data.Length, true, 10);
            if (!res.Success) return "";

            string vendor = System.Text.Encoding.ASCII.GetString(data, 8, 8).Trim();
            string product = System.Text.Encoding.ASCII.GetString(data, 16, 16).Trim();
            return $"{vendor} {product}".Trim();
        }
        catch
        {
            return "";
        }
    }
}
