using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DVDRescue.Native;

/// <summary>
/// Ripristina le unità di rete mappate dentro il processo elevato.
///
/// DVDRescue deve girare come amministratore per parlare col lettore a livello di settore, e
/// Windows tiene le connessioni di rete separate fra il token normale e quello elevato: le
/// lettere mappate dall'utente (Z: verso \\server\condivisione) semplicemente non esistono per
/// un processo elevato, quindi non compaiono nella finestra di scelta della cartella.
///
/// Qui si leggono le mappature salvate dall'utente e si rifanno nella sessione elevata, usando
/// le credenziali di Windows già memorizzate. Se una non si può ristabilire da sola, resta
/// sempre la possibilità di scrivere il percorso di rete per esteso
/// (\\server\condivisione\cartella), che funziona anche senza lettera assegnata.
/// </summary>
public static class NetworkDrives
{
    private const uint ResourceTypeDisk = 0x00000001;
    private const uint ConnectInteractive = 0x00000008;
    private const uint ConnectPrompt = 0x00000010;

    private const int NoError = 0;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorDeviceAlreadyRemembered = 1202;
    private const int ErrorSessionCredentialConflict = 1219;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public string LocalName;
        public string RemoteName;
        public string Comment;
        public string Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern int WNetAddConnection2(ref NetResource netResource, string password,
                                                 string username, uint flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection3W")]
    private static extern int WNetAddConnection3(IntPtr owner, ref NetResource netResource,
                                                 string password, string username, uint flags);

    public sealed class MappedDrive
    {
        public string Letter = "";
        public string RemotePath = "";
        public bool Restored;
        public string Problem = "";

        public override string ToString() => $"{Letter}: → {RemotePath}";
    }

    /// <summary>Mappature salvate dall'utente, lette dal suo profilo.</summary>
    public static List<MappedDrive> ReadUserMappings()
    {
        var result = new List<MappedDrive>();

        try
        {
            using var network = Registry.CurrentUser.OpenSubKey("Network");
            if (network == null) return result;

            foreach (string letter in network.GetSubKeyNames())
            {
                using var entry = network.OpenSubKey(letter);
                string remote = entry?.GetValue("RemotePath") as string;
                if (string.IsNullOrWhiteSpace(remote)) continue;

                result.Add(new MappedDrive
                {
                    Letter = letter.ToUpperInvariant(),
                    RemotePath = remote
                });
            }
        }
        catch { /* senza mappature non c'è niente da ripristinare */ }

        return result;
    }

    /// <summary>
    /// Rifà nella sessione corrente le mappature che mancano. Non chiede credenziali:
    /// usa quelle già memorizzate in Windows.
    /// </summary>
    public static List<MappedDrive> RestoreAll()
    {
        var mappings = ReadUserMappings();

        foreach (var mapping in mappings)
        {
            if (Directory.Exists(mapping.Letter + ":\\"))
            {
                mapping.Restored = true;       // già visibile, niente da fare
                continue;
            }

            var resource = new NetResource
            {
                Type = ResourceTypeDisk,
                LocalName = mapping.Letter + ":",
                RemoteName = mapping.RemotePath.TrimEnd('\\')
            };

            int code = WNetAddConnection2(ref resource, null, null, 0);

            mapping.Restored = code == NoError || code == ErrorAlreadyAssigned ||
                               code == ErrorDeviceAlreadyRemembered;

            if (!mapping.Restored) mapping.Problem = Describe(code);
        }

        return mappings;
    }

    /// <summary>
    /// Ristabilisce una connessione chiedendo le credenziali con la finestra di Windows.
    /// Serve quando la condivisione vuole un utente diverso da quello del computer.
    /// </summary>
    public static bool ConnectInteractively(IntPtr owner, string remotePath, string driveLetter = null)
    {
        var resource = new NetResource
        {
            Type = ResourceTypeDisk,
            LocalName = string.IsNullOrWhiteSpace(driveLetter) ? null : driveLetter.TrimEnd(':') + ":",
            RemoteName = remotePath.TrimEnd('\\')
        };

        int code = WNetAddConnection3(owner, ref resource, null, null, ConnectInteractive | ConnectPrompt);
        return code == NoError || code == ErrorAlreadyAssigned;
    }

    private static string Describe(int code) => code switch
    {
        5 => "accesso negato",
        53 => "percorso di rete non trovato",
        67 => "nome di rete non valido",
        86 => "password non valida",
        1219 => "credenziali in conflitto con una connessione già aperta",
        1326 => "utente o password non corretti",
        _ => $"errore {code}"
    };

    // ------------------------------------------------- impostazione di sistema

    private const string PoliciesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string LinkedConnectionsValue = "EnableLinkedConnections";

    /// <summary>
    /// Stato dell'impostazione di Windows che fa vedere le stesse unità di rete alla sessione
    /// normale e a quella amministratore. Vale per tutto il sistema, non solo per DVDRescue.
    /// </summary>
    public static bool IsLinkedConnectionsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PoliciesKey);
            return key?.GetValue(LinkedConnectionsValue) is int value && value == 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// Attiva o disattiva quell'impostazione. Richiede privilegi di amministratore (DVDRescue
    /// li ha già) e ha effetto dal riavvio successivo. Disattivandola il valore viene rimosso,
    /// così il sistema torna esattamente com'era prima.
    /// </summary>
    public static void SetLinkedConnections(bool enabled)
    {
        using var key = Registry.LocalMachine.CreateSubKey(PoliciesKey, writable: true);
        if (key == null) throw new InvalidOperationException("Chiave di registro non accessibile.");

        if (enabled) key.SetValue(LinkedConnectionsValue, 1, RegistryValueKind.DWord);
        else key.DeleteValue(LinkedConnectionsValue, throwOnMissingValue: false);
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName,
                                                System.Text.StringBuilder remoteName,
                                                ref int length);

    /// <summary>
    /// Percorso di rete di una lettera, se ce l'ha. Guarda prima la connessione attiva e poi
    /// le mappature salvate nel profilo: la seconda risponde anche quando la lettera non esiste
    /// in questa sessione, che è esattamente il caso di un programma avviato come amministratore.
    /// </summary>
    public static string GetRemotePath(string driveLetter)
    {
        string letter = driveLetter.TrimEnd('\\', '/').TrimEnd(':');
        if (letter.Length != 1) return null;

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            int length = buffer.Capacity;
            if (WNetGetConnection(letter + ":", buffer, ref length) == NoError)
            {
                string remote = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(remote)) return remote;
            }
        }
        catch { /* si prova con il profilo */ }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Network\" + letter);
            return key?.GetValue("RemotePath") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Trasforma Y:\cartella in \\server\condivisione\cartella quando Y: è una lettera di rete.
    /// Il percorso per esteso funziona anche da processo elevato, dove la lettera non esiste.
    /// Restituisce null se non c'è niente da tradurre.
    /// </summary>
    public static string ResolveToUnc(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\")) return null;
        if (path.Length < 2 || path[1] != ':') return null;

        string remote = GetRemotePath(path[..1]);
        if (string.IsNullOrWhiteSpace(remote)) return null;

        string rest = path.Length > 2 ? path[2..].TrimStart('\\', '/') : "";
        return string.IsNullOrEmpty(rest) ? remote : remote.TrimEnd('\\') + "\\" + rest;
    }

    /// <summary>Radice \\server\condivisione di un percorso di rete per esteso.</summary>
    public static string GetShareRoot(string uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath) || !uncPath.StartsWith(@"\\")) return null;

        var parts = uncPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\")) return true;

        // una lettera mappata ma non visibile in questa sessione resta un percorso di rete:
        // chiederlo a DriveInfo darebbe "non esiste", che è la risposta sbagliata alla domanda
        if (!string.IsNullOrEmpty(ResolveToUnc(path))) return true;

        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.Length < 2) return false;

            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network;
        }
        catch { return false; }
    }
}
