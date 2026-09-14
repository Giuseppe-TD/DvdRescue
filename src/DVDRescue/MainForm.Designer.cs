namespace DVDRescue;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private GroupBox grpSource;
    private Label lblDrive;
    private ComboBox cmbDrives;
    private Button btnRefreshDrives;
    private Button btnOpenImage;
    private CheckBox chkMakeImage;
    private Label lblWorkFolder;
    private TextBox txtWorkFolder;
    private Button btnBrowseWork;
    private Button btnRead;
    private Label lblSourceInfo;
    private Label lblSpeed;
    private ComboBox cmbSpeed;

    private GroupBox grpTitles;
    private ListView lstTitles;
    private ColumnHeader colNum;
    private ColumnHeader colDuration;
    private ColumnHeader colSize;
    private ColumnHeader colStart;
    private ColumnHeader colDate;
    private ColumnHeader colSource;
    private Button btnSelectAll;
    private Button btnSelectNone;

    private GroupBox grpOutput;
    private Label lblOutFolder;
    private TextBox txtOutFolder;
    private Button btnBrowseOut;
    private CheckBox chkH264;
    private CheckBox chkRemux;
    private CheckBox chkKeepMpg;
    private CheckBox chkDeinterlace;
    private Label lblQuality;
    private NumericUpDown numCrf;
    private Label lblPreset;
    private ComboBox cmbPreset;
    private Label lblPrefix;
    private TextBox txtPrefix;
    private Button btnExtract;

    private ProgressBar progressBar;
    private Label lblStatus;
    private Button btnCancel;
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
        chkMakeImage = new CheckBox();
        lblWorkFolder = new Label();
        txtWorkFolder = new TextBox();
        btnBrowseWork = new Button();
        btnRead = new Button();
        lblSourceInfo = new Label();
        lblSpeed = new Label();
        cmbSpeed = new ComboBox();

        grpTitles = new GroupBox();
        lstTitles = new ListView();
        colNum = new ColumnHeader();
        colDuration = new ColumnHeader();
        colSize = new ColumnHeader();
        colStart = new ColumnHeader();
        colDate = new ColumnHeader();
        colSource = new ColumnHeader();
        btnSelectAll = new Button();
        btnSelectNone = new Button();

        grpOutput = new GroupBox();
        lblOutFolder = new Label();
        txtOutFolder = new TextBox();
        btnBrowseOut = new Button();
        chkH264 = new CheckBox();
        chkRemux = new CheckBox();
        chkKeepMpg = new CheckBox();
        chkDeinterlace = new CheckBox();
        lblQuality = new Label();
        numCrf = new NumericUpDown();
        lblPreset = new Label();
        cmbPreset = new ComboBox();
        lblPrefix = new Label();
        txtPrefix = new TextBox();
        btnExtract = new Button();

        progressBar = new ProgressBar();
        lblStatus = new Label();
        btnCancel = new Button();
        txtLog = new TextBox();

        ((System.ComponentModel.ISupportInitialize)numCrf).BeginInit();
        grpSource.SuspendLayout();
        grpTitles.SuspendLayout();
        grpOutput.SuspendLayout();
        SuspendLayout();

        // ---------------------------------------------------------- sorgente
        grpSource.Text = "1. Sorgente";
        grpSource.Location = new Point(12, 12);
        grpSource.Size = new Size(960, 118);
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

        chkMakeImage.Text = "Copia prima il disco su un file immagine (consigliato: legge una volta sola)";
        chkMakeImage.Location = new Point(78, 58);
        chkMakeImage.Size = new Size(520, 22);
        chkMakeImage.Checked = true;

        lblSpeed.Text = "Velocità lettura:";
        lblSpeed.Location = new Point(600, 58);
        lblSpeed.Size = new Size(100, 20);
        lblSpeed.TextAlign = ContentAlignment.MiddleRight;

        cmbSpeed.Location = new Point(704, 55);
        cmbSpeed.Size = new Size(80, 23);
        cmbSpeed.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbSpeed.Items.AddRange(new object[] { "massima", "8x", "4x", "2x" });
        cmbSpeed.SelectedIndex = 0;

        lblWorkFolder.Text = "Lavoro:";
        lblWorkFolder.Location = new Point(14, 88);
        lblWorkFolder.Size = new Size(60, 20);
        lblWorkFolder.TextAlign = ContentAlignment.MiddleLeft;

        txtWorkFolder.Location = new Point(78, 85);
        txtWorkFolder.Size = new Size(620, 23);

        btnBrowseWork.Text = "...";
        btnBrowseWork.Location = new Point(704, 84);
        btnBrowseWork.Size = new Size(38, 25);
        btnBrowseWork.Click += BtnBrowseWork_Click;

        lblSourceInfo.Location = new Point(750, 86);
        lblSourceInfo.Size = new Size(200, 20);
        lblSourceInfo.ForeColor = SystemColors.GrayText;

        grpSource.Controls.AddRange(new Control[]
        {
            lblDrive, cmbDrives, btnRefreshDrives, btnOpenImage, btnRead,
            chkMakeImage, lblWorkFolder, txtWorkFolder, btnBrowseWork, lblSourceInfo,
            lblSpeed, cmbSpeed
        });

        // ------------------------------------------------------------ titoli
        grpTitles.Text = "2. Video trovati";
        grpTitles.Location = new Point(12, 138);
        grpTitles.Size = new Size(960, 220);
        grpTitles.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        lstTitles.Location = new Point(14, 24);
        lstTitles.Size = new Size(824, 186);
        lstTitles.View = View.Details;
        lstTitles.CheckBoxes = true;
        lstTitles.FullRowSelect = true;
        lstTitles.GridLines = true;
        lstTitles.HideSelection = false;
        lstTitles.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        colNum.Text = "#"; colNum.Width = 40;
        colDuration.Text = "Durata"; colDuration.Width = 90;
        colSize.Text = "Dimensione"; colSize.Width = 100;
        colStart.Text = "Settore iniziale"; colStart.Width = 110;
        colDate.Text = "Data registrazione"; colDate.Width = 160;
        colSource.Text = "Origine"; colSource.Width = 200;
        lstTitles.Columns.AddRange(new[] { colNum, colDuration, colSize, colStart, colDate, colSource });

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

        grpTitles.Controls.AddRange(new Control[] { lstTitles, btnSelectAll, btnSelectNone });

        // ------------------------------------------------------------ output
        grpOutput.Text = "3. Conversione";
        grpOutput.Location = new Point(12, 366);
        grpOutput.Size = new Size(960, 140);
        grpOutput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        lblOutFolder.Text = "Cartella:";
        lblOutFolder.Location = new Point(14, 28);
        lblOutFolder.Size = new Size(60, 20);
        lblOutFolder.TextAlign = ContentAlignment.MiddleLeft;

        txtOutFolder.Location = new Point(78, 25);
        txtOutFolder.Size = new Size(620, 23);

        btnBrowseOut.Text = "...";
        btnBrowseOut.Location = new Point(704, 24);
        btnBrowseOut.Size = new Size(38, 25);
        btnBrowseOut.Click += BtnBrowseOut_Click;

        chkH264.Text = "MP4 H.264 + AAC (compatibile ovunque)";
        chkH264.Location = new Point(78, 56);
        chkH264.Size = new Size(290, 22);
        chkH264.Checked = true;

        chkRemux.Text = "MP4 senza ricodifica (veloce, qualità originale)";
        chkRemux.Location = new Point(374, 56);
        chkRemux.Size = new Size(320, 22);

        chkKeepMpg.Text = "Tieni anche il .mpg grezzo";
        chkKeepMpg.Location = new Point(78, 82);
        chkKeepMpg.Size = new Size(290, 22);

        chkDeinterlace.Text = "Deinterlaccia (consigliato per le videocamere)";
        chkDeinterlace.Location = new Point(374, 82);
        chkDeinterlace.Size = new Size(320, 22);
        chkDeinterlace.Checked = true;

        lblQuality.Text = "Qualità (CRF):";
        lblQuality.Location = new Point(78, 110);
        lblQuality.Size = new Size(90, 20);
        lblQuality.TextAlign = ContentAlignment.MiddleLeft;

        numCrf.Location = new Point(172, 107);
        numCrf.Size = new Size(55, 23);
        numCrf.Minimum = 14;
        numCrf.Maximum = 30;
        numCrf.Value = 20;

        lblPreset.Text = "Velocità:";
        lblPreset.Location = new Point(244, 110);
        lblPreset.Size = new Size(60, 20);
        lblPreset.TextAlign = ContentAlignment.MiddleLeft;

        cmbPreset.Location = new Point(306, 107);
        cmbPreset.Size = new Size(110, 23);
        cmbPreset.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbPreset.Items.AddRange(new object[] { "ultrafast", "veryfast", "faster", "fast", "medium", "slow", "slower" });
        cmbPreset.SelectedIndex = 4;

        lblPrefix.Text = "Nome file:";
        lblPrefix.Location = new Point(434, 110);
        lblPrefix.Size = new Size(70, 20);
        lblPrefix.TextAlign = ContentAlignment.MiddleLeft;

        txtPrefix.Location = new Point(506, 107);
        txtPrefix.Size = new Size(192, 23);
        txtPrefix.Text = "titolo";

        btnExtract.Text = "Estrai e converti";
        btnExtract.Location = new Point(790, 24);
        btnExtract.Size = new Size(155, 58);
        btnExtract.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
        btnExtract.Enabled = false;
        btnExtract.Click += BtnExtract_Click;

        btnCancel.Text = "Annulla";
        btnCancel.Location = new Point(790, 88);
        btnCancel.Size = new Size(155, 30);
        btnCancel.Enabled = false;
        btnCancel.Click += BtnCancel_Click;

        grpOutput.Controls.AddRange(new Control[]
        {
            lblOutFolder, txtOutFolder, btnBrowseOut, chkH264, chkRemux, chkKeepMpg, chkDeinterlace,
            lblQuality, numCrf, lblPreset, cmbPreset, lblPrefix, txtPrefix, btnExtract, btnCancel
        });

        // ------------------------------------------------------ stato + log
        progressBar.Location = new Point(12, 516);
        progressBar.Size = new Size(960, 20);
        progressBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        lblStatus.Location = new Point(12, 540);
        lblStatus.Size = new Size(960, 20);
        lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lblStatus.Text = "Pronto.";

        txtLog.Location = new Point(12, 564);
        txtLog.Size = new Size(960, 170);
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Vertical;
        txtLog.BackColor = Color.FromArgb(24, 24, 24);
        txtLog.ForeColor = Color.Gainsboro;
        txtLog.Font = new Font("Consolas", 8.75F);
        txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        // -------------------------------------------------------------- form
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(984, 746);
        MinimumSize = new Size(1000, 640);
        Controls.AddRange(new Control[] { grpSource, grpTitles, grpOutput, progressBar, lblStatus, txtLog });
        Text = "DVDRescue — recupero video da DVD non finalizzati";
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
