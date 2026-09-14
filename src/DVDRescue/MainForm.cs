using System.Collections.Concurrent;
using System.Reflection;
using DVDRescue.Disc;
using DVDRescue.Media;
using DVDRescue.Model;
using DVDRescue.Native;

namespace DVDRescue;

public partial class MainForm : Form
{
    private CancellationTokenSource _cts;
    private ISectorSource _source;
    private OpticalDrive _drive;
    private DiscAnalysis _analysis;
    private string _imagePath;
    private string _ffmpegPath;
    private bool _busy;

    private readonly ConcurrentQueue<string> _logQueue = new();
    private volatile ProgressReport _lastProgress;
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
        catch { /* icona non critica */ }
    }

    // --------------------------------------------------------------- ciclo UI

    private void MainForm_Load(object sender, EventArgs e)
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        txtWorkFolder.Text = Path.Combine(docs, "DVDRescue", "lavoro");
        txtOutFolder.Text = Path.Combine(docs, "DVDRescue");

        RefreshDrives();

        _ffmpegPath = FfmpegLocator.Find();
        Log(_ffmpegPath != null
            ? $"ffmpeg trovato: {_ffmpegPath}"
            : "ffmpeg non trovato: verrà scaricato automaticamente alla prima conversione.");

        Log("Inserisci il disco e premi \"Leggi disco\". Per i dischi rovinati conviene lasciare attiva la copia su immagine.");
    }

    private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            var r = MessageBox.Show("Un'operazione è in corso. Vuoi interromperla e uscire?",
                "DVDRescue", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            _cts?.Cancel();
        }

        _uiTimer.Stop();
        _source?.Dispose();
        _drive?.Dispose();
    }

    private void UiTimer_Tick(object sender, EventArgs e)
    {
        if (!_logQueue.IsEmpty)
        {
            var sb = new System.Text.StringBuilder();
            while (_logQueue.TryDequeue(out string line))
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");

            txtLog.AppendText(sb.ToString());
        }

        var p = _lastProgress;
        if (p != null)
        {
            _lastProgress = null;
            int value = (int)Math.Max(0, Math.Min(100, p.Percent));
            if (progressBar.Value != value) progressBar.Value = value;

            string speed = p.SpeedMbPerSec > 0.1 ? $"  —  {p.SpeedMbPerSec:F1} MB/s" : "";
            lblStatus.Text = $"{p.Stage}: {p.Detail}  ({value}%){speed}";
        }
    }

    private void Log(string message) => _logQueue.Enqueue(message);

    private IProgress<ProgressReport> CreateProgress() =>
        new Progress<ProgressReport>(p => _lastProgress = p);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnRead.Enabled = !busy;
        btnOpenImage.Enabled = !busy;
        btnRefreshDrives.Enabled = !busy;
        cmbDrives.Enabled = !busy;
        btnExtract.Enabled = !busy && lstTitles.Items.Count > 0;
        btnCancel.Enabled = busy;
        grpOutput.Enabled = true;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in lstTitles.Items) item.Checked = value;
    }

    // ------------------------------------------------------------- sorgente

    private void RefreshDrives()
    {
        cmbDrives.Items.Clear();
        try
        {
            var drives = DriveEnumerator.List();
            foreach (var d in drives) cmbDrives.Items.Add(d);
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
        using var dlg = new FolderBrowserDialog { Description = "Cartella di lavoro per l'immagine del disco" };
        if (Directory.Exists(txtWorkFolder.Text)) dlg.SelectedPath = txtWorkFolder.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) txtWorkFolder.Text = dlg.SelectedPath;
    }

    private void BtnBrowseOut_Click(object sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Cartella di destinazione dei video" };
        if (Directory.Exists(txtOutFolder.Text)) dlg.SelectedPath = txtOutFolder.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) txtOutFolder.Text = dlg.SelectedPath;
    }

    private async void BtnOpenImage_Click(object sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Apri un'immagine grezza del disco",
            Filter = "Immagini disco (*.bin;*.iso;*.img;*.raw)|*.bin;*.iso;*.img;*.raw|Tutti i file (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await RunAnalysisAsync(dlg.FileName);
    }

    private async void BtnRead_Click(object sender, EventArgs e) => await RunAnalysisAsync(null);

    private void BtnCancel_Click(object sender, EventArgs e)
    {
        _cts?.Cancel();
        Log("Interruzione richiesta...");
    }

    // -------------------------------------------------------------- analisi

    private async Task RunAnalysisAsync(string imageFile)
    {
        if (_busy) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = CreateProgress();

        lstTitles.Items.Clear();
        _source?.Dispose(); _source = null;
        _drive?.Dispose(); _drive = null;
        _analysis = null;
        _imagePath = null;

        SetBusy(true);

        try
        {
            if (imageFile != null)
            {
                Log($"Apro l'immagine {imageFile}");
                _source = new ImageSectorSource(imageFile);
                _imagePath = imageFile;
                _analysis = new DiscAnalysis
                {
                    SourceName = Path.GetFileName(imageFile),
                    LastWrittenLba = _source.TotalSectors - 1,
                    ScannedSectors = _source.TotalSectors
                };
            }
            else
            {
                if (cmbDrives.SelectedItem is not OpticalDriveEntry entry)
                {
                    MessageBox.Show("Seleziona un lettore.", "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Log($"Apro l'unità {entry.Letter}: ({entry.Description})");

                int speedKb = cmbSpeed.SelectedIndex switch
                {
                    1 => 11080,   // 8x
                    2 => 5540,    // 4x
                    3 => 2770,    // 2x
                    _ => 0        // massima: non tocca il lettore
                };

                var probe = await Task.Run(() => DiscEngine.ProbeDrive(entry.Letter, Log, ct, speedKb), ct);
                _drive = probe.Drive;
                _source = probe.Source;
                _analysis = probe.Analysis;

                if (chkMakeImage.Checked)
                {
                    string workFolder = txtWorkFolder.Text.Trim();
                    Directory.CreateDirectory(workFolder);

                    long needed = (_analysis.LastWrittenLba + 1) * 2048L;
                    try
                    {
                        var di = new DriveInfo(Path.GetPathRoot(workFolder)!);
                        if (di.AvailableFreeSpace < needed + (100L << 20))
                        {
                            var r = MessageBox.Show(
                                $"Servono circa {needed / 1048576.0:F0} MB ma su {di.Name} ce ne sono {di.AvailableFreeSpace / 1048576.0:F0}.\r\n\r\n" +
                                "Vuoi continuare comunque? (In alternativa annulla, scegli un'altra cartella o togli la spunta alla copia su immagine.)",
                                "Spazio insufficiente", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                            if (r != DialogResult.Yes) return;
                        }
                    }
                    catch { /* controllo spazio non determinante */ }

                    _imagePath = Path.Combine(workFolder, $"disco_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
                    Log($"Copio il disco in {_imagePath}");

                    await DiscEngine.CreateImageAsync(_source, _imagePath, 0, _analysis.LastWrittenLba,
                                                      progress, Log, ct);

                    _source.Dispose();
                    _drive.Dispose();
                    _drive = null;

                    _source = new ImageSectorSource(_imagePath);
                    Log("Il disco può essere rimosso: da qui in avanti si lavora sull'immagine.");
                }
            }

            var analysis = _analysis;
            var source = _source;

            _analysis = await Task.Run(() => DiscEngine.ScanContent(source, analysis, progress, Log, ct), ct);
            PopulateTitles(_analysis);
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
                ? $"{lstTitles.Items.Count} titoli pronti per l'estrazione."
                : "Pronto.";
        }
    }

    private void PopulateTitles(DiscAnalysis analysis)
    {
        lstTitles.Items.Clear();
        if (analysis?.Titles == null) return;

        foreach (var t in analysis.Titles)
        {
            var item = new ListViewItem(t.Index.ToString()) { Checked = true, Tag = t };
            item.SubItems.Add(t.DurationText);
            item.SubItems.Add(t.SizeText);
            item.SubItems.Add(t.StartLba.ToString());
            item.SubItems.Add(t.Recorded?.ToString("dd/MM/yyyy HH:mm") ?? "—");
            item.SubItems.Add(string.IsNullOrEmpty(t.SourceFile) ? "scansione diretta" : t.SourceFile);
            lstTitles.Items.Add(item);
        }

        lblSourceInfo.Text = analysis.FilesystemInfo;

        foreach (var note in analysis.Notes) Log("Nota: " + note);

        if (analysis.Titles.Count == 0)
        {
            Log("Nessun video individuato. Suggerimenti: prova un altro lettore (i DVD-RAM/8 cm non sono letti da tutti),");
            Log("pulisci il disco, oppure riapri l'immagine già creata e riprova con una soglia diversa.");
        }

        btnExtract.Enabled = lstTitles.Items.Count > 0 && !_busy;
    }

    // ------------------------------------------------------------ estrazione

    private async void BtnExtract_Click(object sender, EventArgs e)
    {
        if (_busy || _source == null || _analysis == null) return;

        var selected = lstTitles.Items.Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => (VideoTitle)i.Tag)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show("Seleziona almeno un titolo.", "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = new ExtractOptions
        {
            OutputFolder = txtOutFolder.Text.Trim(),
            MakeH264 = chkH264.Checked,
            MakeRemux = chkRemux.Checked,
            KeepMpg = chkKeepMpg.Checked,
            Crf = (int)numCrf.Value,
            Preset = cmbPreset.SelectedItem?.ToString() ?? "medium",
            Deinterlace = chkDeinterlace.Checked,
            FileNamePrefix = string.IsNullOrWhiteSpace(txtPrefix.Text) ? "titolo" : txtPrefix.Text.Trim()
        };

        if (!options.MakeH264 && !options.MakeRemux && !options.KeepMpg)
        {
            MessageBox.Show("Scegli almeno un formato di uscita.", "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = CreateProgress();

        SetBusy(true);

        try
        {
            Directory.CreateDirectory(options.OutputFolder);

            if ((options.MakeH264 || options.MakeRemux) && _ffmpegPath == null)
            {
                _ffmpegPath = FfmpegLocator.Find();

                if (_ffmpegPath == null)
                {
                    var r = MessageBox.Show(
                        "ffmpeg non è presente. Lo scarico adesso (circa 40 MB dalle build ufficiali gyan.dev)?",
                        "ffmpeg mancante", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                    if (r == DialogResult.Yes)
                    {
                        _ffmpegPath = await FfmpegLocator.DownloadAsync(new Progress<string>(Log), ct);
                    }
                    else
                    {
                        Log("Conversione disattivata: verranno salvati solo i file .mpg grezzi.");
                        options.MakeH264 = false;
                        options.MakeRemux = false;
                        options.KeepMpg = true;
                    }
                }
            }

            var runner = _ffmpegPath != null ? new FfmpegRunner(_ffmpegPath) : null;
            string mpgFolder = options.KeepMpg
                ? options.OutputFolder
                : Path.Combine(Path.GetTempPath(), "DVDRescue_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(mpgFolder);

            int done = 0;
            foreach (var title in selected)
            {
                ct.ThrowIfCancellationRequested();
                done++;

                string baseName = DiscEngine.BuildBaseName(title, options);
                string mpgPath = Path.Combine(mpgFolder, baseName + ".mpg");

                Log($"— Titolo {title.Index} ({done} di {selected.Count}): {title.DurationText}, {title.SizeText}");

                var source = _source;
                long bytes = await Task.Run(() =>
                    DiscEngine.WriteTitleStreamAsync(source, title, mpgPath, progress, ct), ct);

                Log($"  stream estratto: {bytes / 1048576.0:F0} MB → {Path.GetFileName(mpgPath)}");

                if (runner != null && options.MakeH264)
                {
                    string outPath = Path.Combine(options.OutputFolder, baseName + ".mp4");
                    Log($"  converto in H.264: {Path.GetFileName(outPath)}");
                    int code = await runner.RunAsync(
                        FfmpegRunner.BuildH264Args(mpgPath, outPath, options),
                        title.DurationSeconds, $"Conversione {done}/{selected.Count}", progress, Log, ct);
                    Log(code == 0 ? "  fatto." : $"  ffmpeg ha restituito il codice {code}.");
                }

                if (runner != null && options.MakeRemux)
                {
                    string outPath = Path.Combine(options.OutputFolder, baseName + "_originale.mp4");
                    Log($"  remux senza ricodifica: {Path.GetFileName(outPath)}");
                    int code = await runner.RunAsync(
                        FfmpegRunner.BuildRemuxArgs(mpgPath, outPath),
                        title.DurationSeconds, $"Remux {done}/{selected.Count}", progress, Log, ct);
                    Log(code == 0 ? "  fatto." : $"  ffmpeg ha restituito il codice {code}.");
                }

                if (!options.KeepMpg)
                {
                    try { File.Delete(mpgPath); } catch { }
                }
            }

            if (!options.KeepMpg)
            {
                try { Directory.Delete(mpgFolder, true); } catch { }
            }

            Log($"Completato: {selected.Count} titoli elaborati in {options.OutputFolder}");

            if (MessageBox.Show("Estrazione completata. Vuoi aprire la cartella di destinazione?",
                    "DVDRescue", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
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
