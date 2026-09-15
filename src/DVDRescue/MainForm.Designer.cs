namespace DVDRescue;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private GroupBox grpSource;
    private Label lblDrive;
    private ComboBox cmbDrives;
    private Button btnRefreshDrives;
    private Button btnOpenImage;
    private Button btnRead;
    private Label lblSpeed;
    private ComboBox cmbSpeed;
    private CheckBox chkDeepScan;
    private CheckBox chkThorough;
    private CheckBox chkSaveImage;
    private Label lblWorkFolder;
    private TextBox txtWorkFolder;
    private Button btnBrowseWork;

    private GroupBox grpTitles;
    private ListView lstTitles;
    private ColumnHeader colNum;
    private ColumnHeader colDuration;
    private ColumnHeader colSize;
    private ColumnHeader colDate;
    private ColumnHeader colInfo;
    private ColumnHeader colOrigin;
    private Button btnSelectAll;
    private Button btnSelectNone;
    private Label lblDiscInfo;

    private GroupBox grpOutput;
    private Label lblOutFolder;
    private TextBox txtOutFolder;
    private Button btnBrowseOut;
    private Button btnNetwork;
    private Label lblSplit;
    private ComboBox cmbSplit;
    private Label lblPrefix;
    private TextBox txtPrefix;
    private CheckBox chkH264;
    private CheckBox chkRemux;
    private CheckBox chkKeepRaw;
    private CheckBox chkDeinterlace;
    private Label lblQuality;
    private NumericUpDown numCrf;
    private Label lblPreset;
    private ComboBox cmbPreset;
    private Button btnExtract;
    private Button btnCancel;

    private ProgressBar progressBar;
    private Label lblStatus;
    private TextBox txtLog;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        grpSource = new GroupBox();
        lblDrive = new Label();
        cmbDrives = new ComboBox();
        btnRefreshDrives = new Button();
        btnOpenImage = new Button();
        btnRead = new Button();
        lblSpeed = new Label();
        cmbSpeed = new ComboBox();
        chkDeepScan = new CheckBox();
        chkThorough = new CheckBox();
        chkSaveImage = new CheckBox();
        lblWorkFolder = new Label();
        txtWorkFolder = new TextBox();
        btnBrowseWork = new Button();

        grpTitles = new GroupBox();
        lstTitles = new ListView();
        colNum = new ColumnHeader();
        colDuration = new ColumnHeader();
        colSize = new ColumnHeader();
        colDate = new ColumnHeader();
        colInfo = new ColumnHeader();
        colOrigin = new ColumnHeader();
        btnSelectAll = new Button();
        btnSelectNone = new Button();
        lblDiscInfo = new Label();

        grpOutput = new GroupBox();
        lblOutFolder = new Label();
        txtOutFolder = new TextBox();
        btnBrowseOut = new Button();
        btnNetwork = new Button();
        lblSplit = new Label();
        cmbSplit = new ComboBox();
        lblPrefix = new Label();
        txtPrefix = new TextBox();
        chkH264 = new CheckBox();
        chkRemux = new CheckBox();
        chkKeepRaw = new CheckBox();
        chkDeinterlace = new CheckBox();
        lblQuality = new Label();
        numCrf = new NumericUpDown();
        lblPreset = new Label();
        cmbPreset = new ComboBox();
        btnExtract = new Button();
        btnCancel = new Button();

        progressBar = new ProgressBar();
        lblStatus = new Label();
        txtLog = new TextBox();

        ((System.ComponentModel.ISupportInitialize)numCrf).BeginInit();
        grpSource.SuspendLayout();
        grpTitles.SuspendLayout();
        grpOutput.SuspendLayout();
        SuspendLayout();

        // ---------------------------------------------------------- sorgente
        grpSource.Text = "1. Disco";
        grpSource.Location = new Point(12, 12);
        grpSource.Size = new Size(960, 120);
        grpSource.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        lblDrive.Text = "Lettore:";
        lblDrive.Location = new Point(14, 30);
        lblDrive.Size = new Size(60, 20);
        lblDrive.TextAlign = ContentAlignment.MiddleLeft;

        cmbDrives.Location = new Point(78, 27);
        cmbDrives.Size = new Size(430, 23);
        cmbDrives.DropDownStyle = ComboBoxStyle.DropDownList;

        btnRefreshDrives.Text = "Aggiorna";
        btnRefreshDrives.Location = new Point(516, 26);
        btnRefreshDrives.Size = new Size(90, 26);
        btnRefreshDrives.Click += BtnRefreshDrives_Click;

        btnOpenImage.Text = "Apri immagine...";
        btnOpenImage.Location = new Point(612, 26);
        btnOpenImage.Size = new Size(130, 26);
        btnOpenImage.Click += BtnOpenImage_Click;

        btnRead.Text = "Leggi disco";
        btnRead.Location = new Point(790, 24);
        btnRead.Size = new Size(155, 58);
        btnRead.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
        btnRead.Click += BtnRead_Click;

        lblSpeed.Text = "Velocità lettura:";
        lblSpeed.Location = new Point(14, 60);
        lblSpeed.Size = new Size(100, 20);
        lblSpeed.TextAlign = ContentAlignment.MiddleLeft;

        cmbSpeed.Location = new Point(118, 57);
        cmbSpeed.Size = new Size(90, 23);
        cmbSpeed.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSpeed.Items.AddRange(new object[] { "massima", "8x", "4x", "2x" });
        cmbSpeed.SelectedIndex = 0;

        chkDeepScan.Text = "Ignora le strutture e scandisci i settori";
        chkDeepScan.Location = new Point(218, 58);
        chkDeepScan.Size = new Size(280, 22);

        chkThorough.Text = "Parti subito col recupero insistente (di solito non serve: ci arriva da solo)";
        chkThorough.Location = new Point(504, 58);
        chkThorough.Size = new Size(340, 22);

        chkSaveImage.Text = "Salva anche una copia integrale del disco (.bin)";
        chkSaveImage.Location = new Point(14, 84);
        chkSaveImage.Size = new Size(320, 22);

        lblWorkFolder.Text = "in";
        lblWorkFolder.Location = new Point(338, 86);
        lblWorkFolder.Size = new Size(20, 20);
        lblWorkFolder.TextAlign = ContentAlignment.MiddleLeft;

        txtWorkFolder.Location = new Point(360, 83);
        txtWorkFolder.Size = new Size(338, 23);
        txtWorkFolder.Enabled = false;

        btnBrowseWork.Text = "...";
        btnBrowseWork.Location = new Point(704, 82);
        btnBrowseWork.Size = new Size(38, 25);
        btnBrowseWork.Enabled = false;
        btnBrowseWork.Click += BtnBrowseWork_Click;

        chkSaveImage.CheckedChanged += (s, e) =>
        {
            txtWorkFolder.Enabled = chkSaveImage.Checked;
            btnBrowseWork.Enabled = chkSaveImage.Checked;
        };

        grpSource.Controls.AddRange(new Control[]
        {
            lblDrive, cmbDrives, btnRefreshDrives, btnOpenImage, btnRead,
            lblSpeed, cmbSpeed, chkDeepScan, chkThorough, chkSaveImage,
            lblWorkFolder, txtWorkFolder, btnBrowseWork
        });

        // ------------------------------------------------------------ titoli
        grpTitles.Text = "2. Video trovati";
        grpTitles.Location = new Point(12, 140);
        grpTitles.Size = new Size(960, 214);
        grpTitles.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        lstTitles.Location = new Point(14, 24);
        lstTitles.Size = new Size(824, 152);
        lstTitles.View = View.Details;
        lstTitles.CheckBoxes = true;
        lstTitles.FullRowSelect = true;
        lstTitles.GridLines = true;
        lstTitles.HideSelection = false;
        lstTitles.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        colNum.Text = "#"; colNum.Width = 36;
        colDuration.Text = "Durata"; colDuration.Width = 80;
        colSize.Text = "Dimensione"; colSize.Width = 90;
        colDate.Text = "Registrato il"; colDate.Width = 130;
        colInfo.Text = "Formato"; colInfo.Width = 180;
        colOrigin.Text = "Origine"; colOrigin.Width = 280;
        lstTitles.Columns.AddRange(new[] { colNum, colDuration, colSize, colDate, colInfo, colOrigin });

        btnSelectAll.Text = "Seleziona tutti";
        btnSelectAll.Location = new Point(848, 24);
        btnSelectAll.Size = new Size(100, 27);
        btnSelectAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnSelectAll.Click += (s, e) => SetAllChecked(true);

        btnSelectNone.Text = "Deseleziona";
        btnSelectNone.Location = new Point(848, 57);
        btnSelectNone.Size = new Size(100, 27);
        btnSelectNone.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnSelectNone.Click += (s, e) => SetAllChecked(false);

        lblDiscInfo.Location = new Point(14, 182);
        lblDiscInfo.Size = new Size(930, 22);
        lblDiscInfo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        lblDiscInfo.ForeColor = SystemColors.GrayText;

        grpTitles.Controls.AddRange(new Control[] { lstTitles, btnSelectAll, btnSelectNone, lblDiscInfo });

        // ------------------------------------------------------------ output
        grpOutput.Text = "3. Come salvare";
        grpOutput.Location = new Point(12, 362);
        grpOutput.Size = new Size(960, 168);
        grpOutput.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        lblOutFolder.Text = "Cartella:";
        lblOutFolder.Location = new Point(14, 28);
        lblOutFolder.Size = new Size(60, 20);
        lblOutFolder.TextAlign = ContentAlignment.MiddleLeft;

        txtOutFolder.Location = new Point(78, 25);
        txtOutFolder.Size = new Size(566, 23);

        btnBrowseOut.Text = "...";
        btnBrowseOut.Location = new Point(650, 24);
        btnBrowseOut.Size = new Size(38, 25);
        btnBrowseOut.Click += BtnBrowseOut_Click;

        btnNetwork.Text = "Rete ▾";
        btnNetwork.Location = new Point(694, 24);
        btnNetwork.Size = new Size(64, 25);
        btnNetwork.Click += BtnNetwork_Click;

        lblSplit.Text = "Divisione:";
        lblSplit.Location = new Point(14, 58);
        lblSplit.Size = new Size(60, 20);
        lblSplit.TextAlign = ContentAlignment.MiddleLeft;

        cmbSplit.Location = new Point(78, 55);
        cmbSplit.Size = new Size(330, 23);
        cmbSplit.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSplit.Items.AddRange(new object[]
        {
            "Tutto in un unico file",
            "Un file per registrazione",
            "Un file per capitolo"
        });
        cmbSplit.SelectedIndex = 0;
        cmbSplit.SelectedIndexChanged += CmbSplit_SelectedIndexChanged;

        lblPrefix.Text = "Nome file:";
        lblPrefix.Location = new Point(424, 58);
        lblPrefix.Size = new Size(70, 20);
        lblPrefix.TextAlign = ContentAlignment.MiddleLeft;

        txtPrefix.Location = new Point(496, 55);
        txtPrefix.Size = new Size(202, 23);
        txtPrefix.Text = "video";

        chkH264.Text = "MP4 H.264 + AAC (compatibile ovunque)";
        chkH264.Location = new Point(78, 86);
        chkH264.Size = new Size(290, 22);
        chkH264.Checked = true;

        chkRemux.Text = "MP4 senza ricodifica (veloce, qualità originale)";
        chkRemux.Location = new Point(374, 86);
        chkRemux.Size = new Size(324, 22);

        chkKeepRaw.Text = "Tieni anche il flusso grezzo";
        chkKeepRaw.Location = new Point(78, 110);
        chkKeepRaw.Size = new Size(290, 22);

        chkDeinterlace.Text = "Deinterlaccia (consigliato per le videocamere)";
        chkDeinterlace.Location = new Point(374, 110);
        chkDeinterlace.Size = new Size(324, 22);
        chkDeinterlace.Checked = true;

        lblQuality.Text = "Qualità (CRF):";
        lblQuality.Location = new Point(78, 138);
        lblQuality.Size = new Size(90, 20);
        lblQuality.TextAlign = ContentAlignment.MiddleLeft;

        numCrf.Location = new Point(172, 135);
        numCrf.Size = new Size(55, 23);
        numCrf.Minimum = 14;
        numCrf.Maximum = 30;
        numCrf.Value = 20;

        lblPreset.Text = "Velocità:";
        lblPreset.Location = new Point(244, 138);
        lblPreset.Size = new Size(60, 20);
        lblPreset.TextAlign = ContentAlignment.MiddleLeft;

        cmbPreset.Location = new Point(306, 135);
        cmbPreset.Size = new Size(110, 23);
        cmbPreset.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPreset.Items.AddRange(new object[] { "ultrafast", "veryfast", "faster", "fast", "medium", "slow", "slower" });
        cmbPreset.SelectedIndex = 4;

        btnExtract.Text = "Estrai e converti";
        btnExtract.Location = new Point(790, 24);
        btnExtract.Size = new Size(155, 58);
        btnExtract.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
        btnExtract.Enabled = false;
        btnExtract.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnExtract.Click += BtnExtract_Click;

        btnCancel.Text = "Annulla";
        btnCancel.Location = new Point(790, 88);
        btnCancel.Size = new Size(155, 30);
        btnCancel.Enabled = false;
        btnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnCancel.Click += BtnCancel_Click;

        grpOutput.Controls.AddRange(new Control[]
        {
            lblOutFolder, txtOutFolder, btnBrowseOut, btnNetwork, lblSplit, cmbSplit, lblPrefix, txtPrefix,
            chkH264, chkRemux, chkKeepRaw, chkDeinterlace,
            lblQuality, numCrf, lblPreset, cmbPreset, btnExtract, btnCancel
        });

        // ------------------------------------------------------- stato e log
        progressBar.Location = new Point(12, 540);
        progressBar.Size = new Size(960, 20);
        progressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        lblStatus.Location = new Point(12, 564);
        lblStatus.Size = new Size(960, 20);
        lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        lblStatus.Text = "Pronto.";

        txtLog.Location = new Point(12, 588);
        txtLog.Size = new Size(960, 150);
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Color.FromArgb(24, 24, 24);
        txtLog.ForeColor = Color.Gainsboro;
        txtLog.Font = new Font("Consolas", 8.75F);
        txtLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        // -------------------------------------------------------------- form
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(984, 750);
        MinimumSize = new Size(1000, 700);
        Controls.AddRange(new Control[] { grpSource, grpTitles, grpOutput, progressBar, lblStatus, txtLog });
        Text = "DVDRescue — recupero video da DVD, miniDVD e Blu-ray";
        StartPosition = FormStartPosition.CenterScreen;
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;

        ((System.ComponentModel.ISupportInitialize)numCrf).EndInit();
        grpSource.ResumeLayout(false);
        grpSource.PerformLayout();
        grpTitles.ResumeLayout(false);
        grpOutput.ResumeLayout(false);
        grpOutput.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }
}
