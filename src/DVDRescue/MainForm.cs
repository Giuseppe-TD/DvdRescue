using System.Collections.Concurrent;
using System.Reflection;
using DVDRescue.Core;
using DVDRescue.Images;
using DVDRescue.Media;
using DVDRescue.Native;
using DVDRescue.Recovery;

namespace DVDRescue;

public partial class MainForm : Form
{
    private CancellationTokenSource _cts;
    private IBlockSource _source;
    private OpticalDrive _drive;
    private DiscImage _image;
    private RecoveryResult _result;
    private string _ffmpegPath;
    private bool _busy;
    private AppSettings _settings = new();

    private readonly ConcurrentQueue<string> _logQueue = new();
    private volatile ProgressReport _lastProgress;
    private DateTime _busySince;
    private string _phase = "";
    private int _percent;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 200 };

    public MainForm()
    {
        InitializeComponent();
        TryLoadIcon();
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    private void TryLoadIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DVDRescue.app.ico");
            if (stream != null) Icon = new Icon(stream);
        }
        catch { /* l'icona non è critica */ }
    }

    // --------------------------------------------------------------- ciclo UI

    private void MainForm_Load(object sender, EventArgs e)
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _settings = AppSettings.Load();

        txtOutFolder.Text = string.IsNullOrWhiteSpace(_settings.OutputFolder)
            ? Path.Combine(videos, "DVDRescue")
            : _settings.OutputFolder;

        txtWorkFolder.Text = string.IsNullOrWhiteSpace(_settings.WorkFolder)
            ? Path.Combine(videos, "DVDRescue", "immagini")
            : _settings.WorkFolder;

        txtPrefix.Text = _settings.FileNamePrefix;
        cmbSplit.SelectedIndex = Math.Clamp(_settings.SplitMode, 0, cmbSplit.Items.Count - 1);
        chkH264.Checked = _settings.MakeH264;
        chkRemux.Checked = _settings.MakeRemux;
        chkKeepRaw.Checked = _settings.KeepRaw;
        chkDeinterlace.Checked = _settings.Deinterlace;
        numCrf.Value = Math.Clamp(_settings.Crf, (int)numCrf.Minimum, (int)numCrf.Maximum);
        cmbSpeed.SelectedIndex = Math.Clamp(_settings.ReadSpeed, 0, cmbSpeed.Items.Count - 1);
        chkSaveImage.Checked = _settings.SaveDiscImage;
        chkDeepScan.Checked = _settings.DeepScan;
        chkThorough.Checked = _settings.ThoroughRecovery;

        int preset = cmbPreset.Items.IndexOf(_settings.Preset ?? "");
        cmbPreset.SelectedIndex = preset >= 0 ? preset : cmbPreset.Items.IndexOf("medium");

        RefreshDrives();

        if (!string.IsNullOrWhiteSpace(_settings.LastDrive))
        {
            for (int i = 0; i < cmbDrives.Items.Count; i++)
                if (cmbDrives.Items[i] is OpticalDriveEntry entry &&
                    entry.Letter.Equals(_settings.LastDrive, StringComparison.OrdinalIgnoreCase))
                {
                    cmbDrives.SelectedIndex = i;
                    break;
                }
        }

        // Versione in chiaro: serve a capire al volo quale build si sta usando.
        // Si preferisce quella "informativa", che sulle build automatiche porta anche il numero
        // di esecuzione (1.2.0-build.42) e distingue due compilazioni della stessa versione.
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version) || version.Contains('+'))
            version = assembly.GetName().Version?.ToString(3) ?? "?";
        string built = "";
        try
        {
            var stamp = File.GetLastWriteTime(Environment.ProcessPath ?? "");
            if (stamp.Year > 2000) built = $", build del {stamp:dd/MM/yyyy HH:mm}";
        }
        catch { }

        Text = $"DVDRescue {version} — recupero video da DVD, miniDVD e Blu-ray";
        Log($"DVDRescue {version}{built}");

        _ffmpegPath = FfmpegLocator.Find();
        Log(_ffmpegPath != null
            ? $"ffmpeg: {_ffmpegPath}"
            : "ffmpeg non trovato: verrà scaricato alla prima conversione.");

        Log("Inserisci il disco e premi \"Leggi disco\".");

        RestoreNetworkDrivesAsync();
    }

    /// <summary>
    /// Windows tiene separate le connessioni di rete fra sessione normale e sessione
    /// amministratore: le lettere mappate dall'utente non esistono per un processo elevato come
    /// questo, e quindi non compaiono nella finestra di scelta della cartella. Qui si rifanno.
    /// </summary>
    private async void RestoreNetworkDrivesAsync()
    {
        try
        {
            var mappings = await Task.Run(NetworkDrives.RestoreAll);

            if (NetworkDrives.IsLinkedConnectionsEnabled())
                Log("Windows è impostato per mostrare le unità di rete anche ai programmi amministratore.");

            if (mappings.Count == 0) return;

            var restored = mappings.Where(m => m.Restored).ToList();
            var failed = mappings.Where(m => !m.Restored).ToList();

            if (restored.Count > 0)
                Log($"Unità di rete disponibili: {string.Join(", ", restored.Select(m => m.ToString()))}");

            foreach (var mapping in failed)
                Log($"Unità {mapping.Letter}: non ripristinata ({mapping.Problem}). " +
                    "Usa il pulsante \"Rete...\" oppure scrivi il percorso per esteso.");
        }
        catch (Exception ex)
        {
            Log($"Ripristino delle unità di rete non riuscito: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepara la cartella di destinazione e restituisce il percorso da usare davvero.
    ///
    /// Una lettera di rete come Y: non esiste per un processo amministratore, ma il percorso
    /// per esteso a cui punta funziona: quindi si traduce e si prosegue con quello. Se la
    /// condivisione non risponde nemmeno così, si chiedono le credenziali a Windows.
    /// </summary>
    private string PrepareFolder(string folder)
    {
        folder = (folder ?? "").Trim();
        if (folder.Length == 0) throw new IOException("Nessuna cartella di destinazione indicata.");

        if (TryCreate(folder)) return folder;

        // la lettera non è raggiungibile: si prova col percorso di rete per esteso
        string unc = NetworkDrives.ResolveToUnc(folder);

        if (!string.IsNullOrEmpty(unc))
        {
            Log($"{folder} non è raggiungibile da amministratore: uso {unc}");
            if (TryCreate(unc)) return unc;
        }

        string target = unc ?? folder;

        if (NetworkDrives.IsNetworkPath(target))
        {
            string share = NetworkDrives.GetShareRoot(target) ?? target;
            Log($"Chiedo le credenziali per {share}.");

            if (NetworkDrives.ConnectInteractively(Handle, share) && TryCreate(target))
                return target;
        }

        throw new IOException(
            $"Impossibile usare la cartella {folder}." +
            (string.IsNullOrEmpty(unc) ? "" : $"\r\nProvato anche con {unc}.") +
            "\r\n\r\nScrivi il percorso di rete per esteso (\\\\server\\condivisione\\cartella) " +
            "oppure scegli una cartella locale.");
    }

    private static bool TryCreate(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch { return false; }
    }

    private void BtnNetwork_Click(object sender, EventArgs e)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Connetti a una cartella di rete...", null, (s, args) => ConnectToShare());

        bool linked = NetworkDrives.IsLinkedConnectionsEnabled();
        var toggle = new ToolStripMenuItem(
            linked
                ? "Le unità di rete sono sempre visibili — disattiva"
                : "Rendi le unità di rete sempre visibili (impostazione di Windows)",
            null, (s, args) => ToggleLinkedConnections(!linked))
        { Checked = linked };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggle);
        menu.Items.Add("Riprova a ripristinare le unità mappate", null, (s, args) => RestoreNetworkDrivesAsync());

        menu.Show(btnNetwork, new Point(0, btnNetwork.Height));
    }

    private void ConnectToShare()
    {
        string path = txtOutFolder.Text.Trim();

        if (!path.StartsWith(@"\\"))
        {
            MessageBox.Show(
                "Scrivi prima il percorso della cartella di rete per esteso, " +
                "per esempio \\\\server\\condivisione\\video, poi riapri questo menu " +
                "per inserire le credenziali.",
                "Cartella di rete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // risale alla radice \\server\condivisione: è quella che vuole l'autenticazione
        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            MessageBox.Show("Il percorso deve essere nella forma \\\\server\\condivisione\\...",
                "Cartella di rete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string share = $@"\\{parts[0]}\{parts[1]}";

        if (NetworkDrives.ConnectInteractively(Handle, share))
        {
            Log($"Connessione a {share} stabilita.");
            MessageBox.Show($"Connesso a {share}.", "DVDRescue",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            Log($"Connessione a {share} non riuscita.");
        }
    }

    /// <summary>
    /// Attiva o disattiva EnableLinkedConnections. È una modifica al sistema, quindi si fa solo
    /// con una conferma esplicita e si può sempre tornare indietro dallo stesso menu.
    /// </summary>
    private void ToggleLinkedConnections(bool enable)
    {
        string question = enable
            ? "Windows terrà collegate le unità di rete fra sessione normale e sessione " +
              "amministratore: le lettere mappate (Z:, Y:...) diventeranno visibili ai programmi " +
              "avviati come amministratore.\r\n\r\n" +
              "È un'impostazione di Windows, non di DVDRescue: vale per tutti i programmi e per " +
              "tutti gli utenti di questo computer, e ha effetto dal prossimo riavvio.\r\n\r\n" +
              "Si può annullare in qualunque momento da questo stesso menu.\r\n\r\n" +
              "Procedo?"
            : "Rimuovo l'impostazione e Windows torna a tenere separate le unità di rete delle " +
              "due sessioni, com'era prima.\r\n\r\nHa effetto dal prossimo riavvio. Procedo?";

        var answer = MessageBox.Show(question, "Impostazione di Windows",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes) return;

        try
        {
            NetworkDrives.SetLinkedConnections(enable);

            Log(enable
                ? "Impostazione attivata: dopo il riavvio le unità di rete saranno visibili anche da amministratore."
                : "Impostazione rimossa: dopo il riavvio le unità di rete torneranno separate.");

            MessageBox.Show(
                (enable ? "Impostazione attivata." : "Impostazione rimossa.") +
                "\r\n\r\nRiavvia il computer perché abbia effetto. Nel frattempo DVDRescue " +
                "continua a ripristinare le unità per conto suo all'avvio.",
                "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"Impossibile modificare l'impostazione: {ex.Message}");
            MessageBox.Show(
                "Non è stato possibile modificare l'impostazione:\r\n\r\n" + ex.Message +
                "\r\n\r\nServono i privilegi di amministratore.",
                "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            var answer = MessageBox.Show("Un'operazione è in corso. Interrompere e uscire?",
                "DVDRescue", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { e.Cancel = true; return; }
            _cts?.Cancel();
        }

        SaveSettings();
        _uiTimer.Stop();
        ReleaseSource();
    }

    /// <summary>Conserva le scelte dell'utente: al prossimo avvio si riparte da dov'era.</summary>
    private void SaveSettings()
    {
        try
        {
            _settings.OutputFolder = txtOutFolder.Text.Trim();
            _settings.WorkFolder = txtWorkFolder.Text.Trim();
            _settings.FileNamePrefix = txtPrefix.Text.Trim();
            _settings.SplitMode = cmbSplit.SelectedIndex;
            _settings.MakeH264 = chkH264.Checked;
            _settings.MakeRemux = chkRemux.Checked;
            _settings.KeepRaw = chkKeepRaw.Checked;
            _settings.Deinterlace = chkDeinterlace.Checked;
            _settings.Crf = (int)numCrf.Value;
            _settings.Preset = cmbPreset.SelectedItem?.ToString() ?? "medium";
            _settings.ReadSpeed = cmbSpeed.SelectedIndex;
            _settings.SaveDiscImage = chkSaveImage.Checked;
            _settings.DeepScan = chkDeepScan.Checked;
            _settings.ThoroughRecovery = chkThorough.Checked;

            if (cmbDrives.SelectedItem is OpticalDriveEntry entry) _settings.LastDrive = entry.Letter;

            _settings.Save();
        }
        catch { /* non deve mai impedire la chiusura */ }
    }

    private void ReleaseSource()
    {
        _source?.Dispose(); _source = null;
        _image?.Dispose(); _image = null;
        _drive?.Dispose(); _drive = null;
    }

    private void UiTimer_Tick(object sender, EventArgs e)
    {
        if (!_logQueue.IsEmpty)
        {
            var sb = new System.Text.StringBuilder();
            while (_logQueue.TryDequeue(out string line))
            {
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
                _phase = line;
            }
            txtLog.AppendText(sb.ToString());
        }

        var progress = _lastProgress;
        if (progress != null)
        {
            _lastProgress = null;
            _percent = (int)Math.Max(0, Math.Min(100, progress.Percent));
            _phase = $"{progress.Stage}: {progress.Detail}";
        }

        if (!_busy) return;

        // Segno di vita.
        //
        // Cercare dove comincia il video su un disco messo male vuol dire sondare settori che il
        // lettore ci mette secondi a rifiutare: per minuti interi non c'è niente da riportare, e
        // senza questo l'ultima scritta rimasta sullo schermo era "Pronto." — quella della lettura
        // precedente. Il programma stava lavorando e sembrava piantato.
        if (progressBar.Value != _percent) progressBar.Value = _percent;

        var elapsed = DateTime.Now - _busySince;
        string spinner = "|/-\\"[(int)(elapsed.TotalMilliseconds / 250) % 4].ToString();
        string detail = string.IsNullOrWhiteSpace(_phase) ? "lettura in corso" : Shorten(_phase);

        lblStatus.Text = $"{spinner}  {detail}  —  {elapsed:mm\\:ss}" +
                         (_percent > 0 ? $"  ({_percent}%)" : "");
    }

    private static string Shorten(string text) =>
        text.Length <= 90 ? text : text.Substring(0, 87) + "...";

    private void Log(string message) => _logQueue.Enqueue(message);

    private IProgress<ProgressReport> CreateProgress() =>
        new Progress<ProgressReport>(p => _lastProgress = p);

    private IProgress<string> CreateTextProgress() =>
        new Progress<string>(s => _lastProgress = new ProgressReport { Stage = "Analisi", Detail = s });

    private void SetBusy(bool busy)
    {
        _busy = busy;

        if (busy)
        {
            _busySince = DateTime.Now;
            _phase = "";
            _percent = 0;
        }

        btnRead.Enabled = !busy;
        btnOpenImage.Enabled = !busy;
        btnRefreshDrives.Enabled = !busy;
        cmbDrives.Enabled = !busy;
        btnExtract.Enabled = !busy && lstTitles.Items.Count > 0;
        btnCancel.Enabled = busy;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in lstTitles.Items) item.Checked = value;
    }

    // -------------------------------------------------------------- sorgente

    private void RefreshDrives()
    {
        cmbDrives.Items.Clear();
        try
        {
            foreach (var drive in DriveEnumerator.List()) cmbDrives.Items.Add(drive);
            if (cmbDrives.Items.Count > 0) cmbDrives.SelectedIndex = 0;
            else Log("Nessun lettore ottico rilevato.");
        }
        catch (Exception ex)
        {
            Log($"Errore nell'elenco dei lettori: {ex.Message}");
        }
    }

    private void BtnRefreshDrives_Click(object sender, EventArgs e) => RefreshDrives();

    private void BtnBrowseWork_Click(object sender, EventArgs e)
    {
        string chosen = PickFolder("Dove salvare la copia del disco", txtWorkFolder.Text);
        if (chosen != null) txtWorkFolder.Text = chosen;
    }

    private void BtnBrowseOut_Click(object sender, EventArgs e)
    {
        string chosen = PickFolder("Dove salvare i video", txtOutFolder.Text);
        if (chosen != null) txtOutFolder.Text = chosen;
    }

    /// <summary>
    /// Finestra di scelta della cartella. Nella casella "Cartella:" in basso si può incollare
    /// un percorso di rete per esteso (\\server\condivisione\...) anche quando la lettera
    /// mappata non compare nell'albero.
    /// </summary>
    private string PickFolder(string description, string current)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description + " — puoi anche incollare un percorso di rete",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            AutoUpgradeEnabled = true
        };

        current = (current ?? "").Trim();

        if (Directory.Exists(current)) dialog.SelectedPath = current;
        else
        {
            try
            {
                string parent = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) dialog.SelectedPath = parent;
            }
            catch { /* percorso non valido: si apre dove capita */ }
        }

        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private async void BtnOpenImage_Click(object sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Apri un'immagine di disco",
            Filter = "Immagini disco (*.iso;*.bin;*.img;*.nrg;*.mds;*.ccd;*.cdi;*.daa;*.cue;*.raw)" +
                     "|*.iso;*.bin;*.img;*.nrg;*.mds;*.ccd;*.cdi;*.daa;*.cue;*.raw|Tutti i file (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunAnalysisAsync(dialog.FileName);
    }

    private async void BtnRead_Click(object sender, EventArgs e) => await RunAnalysisAsync(null);

    private void BtnCancel_Click(object sender, EventArgs e)
    {
        _cts?.Cancel();
        Log("Interruzione richiesta...");
    }

    private void CmbSplit_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_result != null) ShowTitles();
    }

    // --------------------------------------------------------------- analisi

    private async Task RunAnalysisAsync(string imageFile)
    {
        if (_busy) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        lstTitles.Items.Clear();
        lblDiscInfo.Text = "";
        _result = null;
        ReleaseSource();

        SetBusy(true);

        try
        {
            Func<long> verifyLimit = null;
            List<SectorRange> writtenRanges = null;
            string mediaText = "", statusText = "";
            var watch = System.Diagnostics.Stopwatch.StartNew();

            if (imageFile != null)
            {
                Log($"Apro {Path.GetFileName(imageFile)}");

                _image = await Task.Run(() => DiscImage.Open(imageFile), ct);
                Log($"Formato riconosciuto: {_image.FormatName}");
                foreach (var note in _image.Notes) Log("  " + note);

                _source = _image.GetDataSource();
                mediaText = _image.FormatName;
            }
            else
            {
                if (cmbDrives.SelectedItem is not OpticalDriveEntry entry)
                {
                    MessageBox.Show("Seleziona un lettore.", "DVDRescue",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Log($"Apro l'unità {entry.Letter}: ({entry.Description})");

                int speed = cmbSpeed.SelectedIndex switch
                {
                    1 => 11080,   // 8x
                    2 => 5540,    // 4x
                    3 => 2770,    // 2x
                    _ => 0
                };

                var opened = await Task.Run(
                    () => DriveAccess.Open(entry.Letter, speed, chkThorough.Checked, Log, ct), ct);
                _drive = opened.Drive;
                _source = opened.Source;
                mediaText = opened.MediaText;
                statusText = opened.DiscStatusText;
                writtenRanges = opened.Written;
                foreach (var note in opened.Notes) Log("Nota: " + note);

                // La ricerca del limite dell'area scritta viene fatta solo se serve la scansione,
                // e una volta sola: sui dischi messi male costa una quindicina di sondaggi, e
                // rifarla a ogni tentativo vorrebbe dire pagarla tre volte per lo stesso numero.
                long cachedLimit = long.MinValue;

                verifyLimit = () =>
                {
                    if (cachedLimit == long.MinValue)
                    {
                        cachedLimit = DriveAccess.VerifyWrittenLimit(opened.Source, opened.EstimatedLastSector,
                                                                     opened.Written, Log, ct);

                        if (cachedLimit > 0)
                        {
                            opened.Source.SetTotalBlocks(cachedLimit + 1);

                            // Adesso il limite è noto. Continuare a indovinarlo dai settori
                            // illeggibili non serve più e fa danno: se in mezzo al disco c'è
                            // una zona non scritta, la ricerca si fermerebbe lì invece di
                            // arrivare al video che sta dopo.
                            opened.Source.ConsecutiveFailuresLimit = long.MaxValue;
                        }
                    }

                    opened.Source.ResetEndOfData();
                    return cachedLimit;
                };

                if (chkSaveImage.Checked)
                {
                    long limit = verifyLimit();
                    await SaveDiscImageAsync(opened.Source, limit > 0 ? limit : opened.EstimatedLastSector,
                                             opened.Written, ct);
                }
            }

            var source = _source;
            bool deep = chkDeepScan.Checked;
            bool thorough = chkThorough.Checked;
            bool preciseSplit = cmbSplit.SelectedIndex != 0;   // serve solo se i file vanno separati
            var textProgress = CreateTextProgress();

            var optical = _source as OpticalBlockSource;
            var written = writtenRanges;

            async Task<RecoveryResult> AttemptAsync(ReadEffort effort, bool deepScan, bool quickScan)
            {
                if (optical != null)
                {
                    optical.Effort = effort;
                    optical.ResetEndOfData();
                }

                return await Task.Run(() =>
                {
                    var r = RecoveryEngine.Analyze(source, deepScan, verifyLimit, textProgress, Log, ct,
                                                   preciseSplit, quickScan, written);
                    r.MediaText = mediaText;
                    r.DiscStatusText = statusText;
                    return r;
                }, ct);
            }

            var baseEffort = thorough ? ReadEffort.Thorough : ReadEffort.Fast;
            var workingEffort = baseEffort;

            var result = await AttemptAsync(baseEffort, deep, quickScan: true);

            // Se il primo giro non trova niente non si rimanda la palla all'utente: si riprova
            // da soli. Ma per gradi, perché saltare subito al metodo insistente su un disco
            // grande vuol dire mezz'ora di attesa. Il gradino intermedio legge tutto di seguito
            // senza saltare le zone vuote e usa il comando alternativo sui settori che non
            // rispondono: costa poco e basta quasi sempre.
            if (result.Titles.Count == 0 && optical != null && !thorough)
            {
                Log("Il metodo veloce non ha trovato video: riprovo con una lettura più attenta.");
                var second = await AttemptAsync(ReadEffort.Balanced, deepScan: true, quickScan: false);

                if (second.Titles.Count > 0)
                {
                    Log($"La lettura più attenta ha trovato {second.Titles.Count} video.");
                    result = second;
                    workingEffort = ReadEffort.Balanced;
                }
            }

            // Ultimo gradino: qui si insiste sul serio, tre tentativi per settore. È il più lento
            // e su un disco grande può durare parecchio, quindi lo si dice chiaramente — il
            // pulsante Interrompi resta attivo.
            if (result.Titles.Count == 0 && !(deep && thorough))
            {
                Log(optical != null
                    ? "Ancora niente: provo il recupero insistente. È il metodo più lento, si può fermare con Interrompi."
                    : "Riprovo ignorando le strutture e scandendo tutti i settori.");

                var third = await AttemptAsync(ReadEffort.Thorough, deepScan: true, quickScan: false);

                if (third.Titles.Count > 0)
                {
                    Log($"Il recupero insistente ha trovato {third.Titles.Count} video: " +
                        "su questo disco servono le letture ostinate.");
                    result = third;
                    workingEffort = ReadEffort.Thorough;
                }
                else
                {
                    Log("Niente da fare: nessun video leggibile su questo disco.");
                }
            }

            // L'estrazione deve leggere il disco con lo stesso impegno che ha permesso di
            // trovarlo, altrimenti i settori recuperati a fatica tornerebbero vuoti nel file.
            if (optical != null) optical.Effort = workingEffort;

            _result = result;
            foreach (var note in result.Notes) Log("· " + note);

            Log($"Tempo totale dell'analisi: {watch.Elapsed.TotalSeconds:F1} s.");
            ShowTitles();
        }
        catch (OperationCanceledException)
        {
            Log("Operazione annullata.");
        }
        catch (Exception ex)
        {
            Log($"ERRORE: {ex.Message}");
            MessageBox.Show(ex.Message, "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            _lastProgress = null;
            progressBar.Value = 0;
            lblStatus.Text = lstTitles.Items.Count > 0
                ? $"{lstTitles.Items.Count} video pronti."
                : "Pronto.";
        }
    }

    private async Task SaveDiscImageAsync(OpticalBlockSource source, long lastSector,
                                          List<SectorRange> writtenRanges, CancellationToken ct)
    {
        string folder = PrepareFolder(txtWorkFolder.Text);
        if (folder != txtWorkFolder.Text.Trim()) txtWorkFolder.Text = folder;

        string path = Path.Combine(folder, $"disco_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
        Log($"Copio il disco in {path}");

        var progress = CreateProgress();
        long total = lastSector + 1;

        await Task.Run(async () =>
        {
            var buffer = new byte[256 * 2048];
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Le zone mai scritte non si leggono: si scrivono come zeri e si tira dritto.
            // Sono decine di megabyte su cui il lettore impiegherebbe minuti per dire "niente".
            bool Scritto(long sector) =>
                writtenRanges == null || writtenRanges.Count == 0 ||
                writtenRanges.Any(r => sector + 255 >= r.First && sector <= r.Last);

            for (long sector = 0; sector < total; sector += 256)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(256, total - sector);

                if (Scritto(sector)) source.ReadBlocks(sector, count, buffer, 0);
                else Array.Clear(buffer, 0, count * 2048);
                await output.WriteAsync(buffer.AsMemory(0, count * 2048), ct);

                if (watch.ElapsedMilliseconds > 400)
                {
                    watch.Restart();
                    double mb = (sector + count) * 2048.0 / 1048576.0;
                    progress.Report(new ProgressReport
                    {
                        Stage = "Copia del disco",
                        Detail = $"{mb:F0} MB di {total * 2048.0 / 1048576.0:F0} MB",
                        Percent = (sector + count) * 100.0 / total
                    });
                }
            }
        }, ct);

        Log($"Copia completata ({source.BadBlockCount} settori illeggibili).");
    }

    private void ShowTitles()
    {
        lstTitles.Items.Clear();
        if (_result == null) return;

        var mode = (SplitMode)Math.Max(0, cmbSplit.SelectedIndex);
        var titles = RecoveryEngine.ApplySplit(_result, mode);

        foreach (var title in titles)
        {
            var item = new ListViewItem(title.Index.ToString()) { Checked = true, Tag = title };
            item.SubItems.Add(title.DurationText);
            item.SubItems.Add(title.SizeText);
            item.SubItems.Add(title.Recorded?.ToString("dd/MM/yyyy HH:mm") ?? "—");
            item.SubItems.Add(title.VideoInfo);
            item.SubItems.Add(title.Origin);
            lstTitles.Items.Add(item);
        }

        var info = new List<string>();
        if (!string.IsNullOrWhiteSpace(_result.ProfileText)) info.Add(_result.ProfileText);
        if (!string.IsNullOrWhiteSpace(_result.FilesystemInfo)) info.Add(_result.FilesystemInfo);
        if (!string.IsNullOrWhiteSpace(_result.VolumeLabel)) info.Add($"volume \"{_result.VolumeLabel}\"");
        if (!string.IsNullOrWhiteSpace(_result.DiscStatusText)) info.Add($"disco {_result.DiscStatusText}");
        lblDiscInfo.Text = string.Join("  ·  ", info);

        if (mode == SplitMode.PerChapter && _result.ChapterTitles.Count == 0)
            Log("Questo disco non dichiara capitoli: resto sulla divisione per registrazione.");

        if (mode != SplitMode.SingleFile && _result.QuickScan)
            Log("Questa analisi ha preso l'area scritta tutta insieme: per separare le " +
                "registrazioni premi di nuovo \"Leggi disco\" con questa divisione già scelta.");

        if (titles.Count == 0)
        {
            Log("Nessun video individuato. Prova un altro lettore, oppure spunta la scansione approfondita.");
        }

        btnExtract.Enabled = lstTitles.Items.Count > 0 && !_busy;
    }

    // ------------------------------------------------------------ estrazione

    private async void BtnExtract_Click(object sender, EventArgs e)
    {
        if (_busy || _source == null || _result == null) return;

        var selected = lstTitles.Items.Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => (RecoveryTitle)i.Tag)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("Seleziona almeno un video.", "DVDRescue",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = new ExtractOptions
        {
            OutputFolder = txtOutFolder.Text.Trim(),
            Split = (SplitMode)Math.Max(0, cmbSplit.SelectedIndex),
            MakeH264 = chkH264.Checked,
            MakeRemux = chkRemux.Checked,
            KeepRaw = chkKeepRaw.Checked,
            Crf = (int)numCrf.Value,
            Preset = cmbPreset.SelectedItem?.ToString() ?? "medium",
            Deinterlace = chkDeinterlace.Checked,
            FileNamePrefix = txtPrefix.Text
        };

        if (!options.MakeH264 && !options.MakeRemux && !options.KeepRaw)
        {
            MessageBox.Show("Scegli almeno un formato di uscita.", "DVDRescue",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = CreateProgress();

        SetBusy(true);

        try
        {
            // il percorso davvero utilizzabile può essere diverso da quello scritto
            // (una lettera di rete diventa il percorso per esteso)
            options.OutputFolder = PrepareFolder(options.OutputFolder);
            if (options.OutputFolder != txtOutFolder.Text.Trim())
                txtOutFolder.Text = options.OutputFolder;

            if ((options.MakeH264 || options.MakeRemux) && _ffmpegPath == null)
            {
                _ffmpegPath = FfmpegLocator.Find();

                if (_ffmpegPath == null)
                {
                    var answer = MessageBox.Show(
                        "ffmpeg non è presente. Lo scarico adesso (circa 40 MB)?",
                        "ffmpeg mancante", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                    if (answer == DialogResult.Yes)
                        _ffmpegPath = await FfmpegLocator.DownloadAsync(new Progress<string>(Log), ct);
                    else
                    {
                        Log("Conversione disattivata: salvo solo i flussi grezzi.");
                        options.MakeH264 = false;
                        options.MakeRemux = false;
                        options.KeepRaw = true;
                    }
                }
            }

            var runner = _ffmpegPath != null ? new FfmpegRunner(_ffmpegPath) : null;
            var source = _source;
            int done = 0;

            foreach (var title in selected)
            {
                ct.ThrowIfCancellationRequested();
                done++;

                Log($"— {title.Name} ({done} di {selected.Count}): {title.DurationText}, {title.SizeText}");

                // Ogni video riparte col bilancio pieno: il tetto serve a non spendere ore su
                // un disco morto, non a penalizzare il secondo file perché il primo era messo male.
                if (source is OpticalBlockSource opticalSource)
                {
                    opticalSource.ResetRetryBudget();
                    opticalSource.ResetEndOfData();
                }

                await TitleExtractor.ExtractAsync(source, title, options, runner, progress, Log, ct);
            }

            Log($"Completato: {selected.Count} file in {options.OutputFolder}");
            SaveSettings();

            if (MessageBox.Show("Fatto. Apro la cartella?", "DVDRescue",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = options.OutputFolder,
                    UseShellExecute = true
                });
            }
        }
        catch (OperationCanceledException)
        {
            Log("Estrazione annullata.");
        }
        catch (Exception ex)
        {
            Log($"ERRORE: {ex.Message}");
            MessageBox.Show(ex.Message, "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            _lastProgress = null;
            progressBar.Value = 0;
            lblStatus.Text = "Pronto.";
        }
    }
}
