namespace MediaWidget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\MediaWidget.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
            return;

        // A taskbar widget should never pop up a crash dialog: log unexpected errors and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // The taskbar owns our window, so if Explorer restarts Windows may destroy it along with the old taskbar.
        // Start a fresh widget in that case; only stop when the user actually quits.
        while (true)
        {
            using var widget = new WidgetForm();
            Application.Run(widget);
            if (widget.QuitRequested)
                break;
            Thread.Sleep(2000);
        }
    }

    /// <summary>Appends to %LOCALAPPDATA%\MediaWidget\errors.log (reset once it passes 256 KB).</summary>
    private static void LogError(Exception? error)
    {
        if (error is null)
            return;
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaWidget");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
