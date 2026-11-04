namespace IRacingCommandCenter;

static class Program
{
    // IRacingCommandCenter.exe          start (or bring up) the window
    // IRacingCommandCenter.exe --tray   start minimized to the tray (used at Windows startup)
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\iRacingCommandCenter", out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\iRacingCommandCenter.Show");
        if (!first)
        {
            // already running: ask that instance to show its window
            showEvent.Set();
            return;
        }

        // log anything unexpected instead of dying silently
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"Unhandled error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Crash: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"Background error: {e.Exception}"); e.SetObserved(); };

        Startup.MigrateFromHubPower();
        ApplicationConfiguration.Initialize();
        bool showWindow = !args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayApp(showEvent, showWindow));
    }
}
