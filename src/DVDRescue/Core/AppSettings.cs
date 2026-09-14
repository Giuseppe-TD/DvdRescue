using System.Text.Json;
using System.Text.Json.Serialization;

namespace DVDRescue.Core;

/// <summary>
/// Impostazioni dell'utente, conservate fra un avvio e l'altro.
///
/// Stanno in un file JSON dentro %APPDATA%\DVDRescue: niente registro, niente installazione,
/// e per portarsele su un altro computer basta copiare il file. Se il file manca o è rovinato
/// si riparte dai valori predefiniti senza dire niente: è una comodità, non un pezzo critico.
/// </summary>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = "";
    public string WorkFolder { get; set; } = "";
    public string FileNamePrefix { get; set; } = "video";

    /// <summary>0 = tutto in un file, 1 = per registrazione, 2 = per capitolo.</summary>
    public int SplitMode { get; set; }

    public bool MakeH264 { get; set; } = true;
    public bool MakeRemux { get; set; }
    public bool KeepRaw { get; set; }
    public bool Deinterlace { get; set; } = true;

    public int Crf { get; set; } = 20;
    public string Preset { get; set; } = "medium";

    /// <summary>Indice nella tendina: 0 = massima, 1 = 8x, 2 = 4x, 3 = 2x.</summary>
    public int ReadSpeed { get; set; }

    public bool SaveDiscImage { get; set; }
    public bool DeepScan { get; set; }
    public bool ThoroughRecovery { get; set; }

    /// <summary>Ultima unità usata, per ritrovarla selezionata al prossimo avvio.</summary>
    public string LastDrive { get; set; } = "";

    [JsonIgnore]
    public static string FilePath
    {
        get
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DVDRescue");
            return Path.Combine(folder, "impostazioni.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* se non si riesce a salvare, pazienza: si riparte dai valori predefiniti */ }
    }
}
