using System.Text;

namespace DVDRescue;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
    }

    private static void ShowCrash(Exception ex)
    {
        if (ex == null) return;
        MessageBox.Show(
            $"Errore non gestito:\r\n\r\n{ex.Message}\r\n\r\n{ex.StackTrace}",
            "DVDRescue", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
