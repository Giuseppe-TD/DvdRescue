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
        catch { /* l'icona non è critica */ }
    }

    // --------------------------------------------------------------- ciclo UI

    private void MainForm_Load(object sender, EventArgs e)
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        txtOutFolder.Text = Path.Combine(videos, "DVDRescue");
        txtWorkFolder.Text = Path.Combine(videos, "DVDRescue", "immagini");

        RefreshDrives();

        _ffmpegPath = FfmpegLocator.Find();
        Log(_ffmpegPath != null
            ? $"ffmpeg: {_ffmpegPath}"
            : "ffmpeg non trovato: verrà scaricato alla prima conversione.");

        Log("Inserisci il disco e premi \"Leggi disco\".");
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

        _uiTimer.Stop();
        ReleaseSource();
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
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            txtLog.AppendText(sb.ToString());
        }

        var progress = _lastProgress;
        if (progress != null)
        {
            _lastProgress = null;
            int value = (int)Math.Max(0, Math.Min(100, progress.Percent));
            if (progressBar.Value != value) progressBar.Value = value;
            lblStatus.Text = $"{progress.Stage}: {progress.Detail}  ({value}%)";
        }
    }

    private void Log(string message) => _logQueue.Enqueue(message);

    private IProgress<ProgressReport> CreateProgress() =>
        new Progress<ProgressReport>(p => _lastProgress = p);

    private IProgress<string> CreateTextProgress() =>
        new Progress<string>(s => _lastProgress = new ProgressReport { Stage = "Analisi", Detail = s });

    private void SetBusy(bool busy)
    {
        _busy = busy;
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
        using var dialog = new FolderBrowserDialog { Description = "Dove salvare la copia del disco" };
        if (Directory.Exists(txtWorkFolder.Text)) dialog.SelectedPath = txtWorkFolder.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) txtWorkFolder.Text = dialog.SelectedPath;
    }

    private void BtnBrowseOut_Click(object sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Dove salvare i video" };
        if (Directory.Exists(txtOutFolder.Text)) dialog.SelectedPath = txtOutFolder.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) txtOutFolder.Text = dialog.SelectedPath;
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
            long lastWritten = -1;
            string mediaText = "", statusText = "";

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

                var opened = await Task.Run(() => DriveAccess.Open(entry.Letter, speed, Log, ct), ct);
                _drive = opened.Drive;
                _source = opened.Source;
                lastWritten = opened.LastWrittenSector;
                mediaText = opened.MediaText;
                statusText = opened.DiscStatusText;
                foreach (var note in opened.Notes) Log("Nota: " + note);

                if (chkSaveImage.Checked)
                    await SaveDiscImageAsync(opened, ct);
            }

            var source = _source;
            bool deep = chkDeepScan.Checked;
            var textProgress = CreateTextProgress();

            var result = await Task.Run(() =>
            {
                var r = RecoveryEngine.Analyze(source, deep, textProgress, Log, ct);
                r.LastWrittenSector = lastWritten;
                r.MediaText = mediaText;
                r.DiscStatusText = statusText;
                return r;
            }, ct);

            _result = result;
            foreach (var note in result.Notes) Log("· " + note);

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

    private async Task SaveDiscImageAsync(DriveAccess.DriveOpenResult opened, CancellationToken ct)
    {
        string folder = txtWorkFolder.Text.Trim();
        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, $"disco_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
        Log($"Copio il disco in {path}");

        var progress = CreateProgress();
        var source = opened.Source;
        long total = opened.LastWrittenSector + 1;

        await Task.Run(async () =>
        {
            var buffer = new byte[256 * 2048];
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            for (long sector = 0; sector < total; sector += 256)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(256, total - sector);
                source.ReadBlocks(sector, count, buffer, 0);
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

        Log($"Copia completata ({opened.Source.BadBlockCount} settori illeggibili).");
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
            Directory.CreateDirectory(options.OutputFolder);

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

                await TitleExtractor.ExtractAsync(source, title, options, runner, progress, Log, ct);
            }

            Log($"Completato: {selected.Count} file in {options.OutputFolder}");

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
