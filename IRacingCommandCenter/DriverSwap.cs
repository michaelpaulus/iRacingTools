using System.Diagnostics;

namespace IRacingCommandCenter;

/// <summary>
/// Switches the iRacing app between drivers on one rig (a team race driver swap at a pit stop).
///
/// The iRacing app keeps who is signed in inside its profile folder (%APPDATA%\iracing-electron). Each driver gets
/// their own copy of that folder: the active one stays where the app looks for it, the others wait in
/// %APPDATA%\iracing-electron.drivers\&lt;name&gt;. A swap closes the app, renames the folders and starts the app
/// again, already signed in as the other driver. No passwords are stored or typed by this tool.
///
/// Each profile folder carries a small marker file with its driver's name, so the folders themselves say whose they are.
/// </summary>
public class DriverSwap
{
    const string Marker = "commandcenter-driver.txt";
    const string UiProcess = "iRacingUI";
    static readonly string[] SimProcesses = ["iRacingSim64DX11"];

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string ActiveDir => Path.Combine(AppData, "iracing-electron");
    public static string StoreDir => Path.Combine(AppData, "iracing-electron.drivers");
    static string DriverDir(string name) => Path.Combine(StoreDir, name);

    string _uiExe = @"C:\Program Files (x86)\iRacing\ui\iRacingUI.exe";

    /// <summary>The driver signed in to the iRacing app now, or null when the active profile hasn't been named yet.</summary>
    public string? Current => ReadMarker(ActiveDir);

    /// <summary>Every saved driver, the current one included, in name order.</summary>
    public List<string> Drivers
    {
        get
        {
            var names = new List<string>();
            if (Current is { } c) names.Add(c);
            if (Directory.Exists(StoreDir))
                names.AddRange(Directory.GetDirectories(StoreDir).Select(d => ReadMarker(d) ?? Path.GetFileName(d)));
            return names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static bool SimRunning => SimProcesses.Any(p => Process.GetProcessesByName(p).Length > 0);

    static string? ReadMarker(string dir)
    {
        try
        {
            var path = Path.Combine(dir, Marker);
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } n ? n : null : null;
        }
        catch { return null; }
    }

    static void WriteMarker(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Marker), name);
    }

    public static string? CheckName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Enter a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.')) return "Use letters, numbers and spaces only.";
        return null;
    }

    /// <summary>Names the driver who is signed in to the iRacing app now. Nothing is closed or moved.</summary>
    public void NameCurrent(string name)
    {
        if (!Directory.Exists(ActiveDir)) throw new InvalidOperationException("The iRacing app hasn't been run on this PC yet.");
        if (Drivers.Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase) && !d.Equals(Current, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"There's already a driver called {name}.");
        WriteMarker(ActiveDir, name);
        Log.Write($"Driver swap: the iRacing app's current sign-in is {name}");
    }

    /// <summary>
    /// Adds a driver: puts the current driver's profile aside and opens the iRacing app on a new, empty profile, where
    /// the new driver signs in once. The app's window and theme settings are carried over.
    /// </summary>
    public async Task AddAsync(string name, IProgress<string> progress)
    {
        var current = Current ?? throw new InvalidOperationException("Name the driver who is signed in now first.");
        if (Drivers.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"There's already a driver called {name}.");
        CheckSim();
        await CloseUiAsync(progress);
        var uiConfig = Path.Combine(ActiveDir, "config.json");
        var keep = File.Exists(uiConfig) ? File.ReadAllText(uiConfig) : null;
        await MoveAsync(ActiveDir, DriverDir(current));
        WriteMarker(ActiveDir, name);
        if (keep != null) File.WriteAllText(Path.Combine(ActiveDir, "config.json"), keep);
        Log.Write($"Driver swap: added {name} ({current} put aside); waiting for {name} to sign in");
        StartUi(progress);
    }

    /// <summary>
    /// The swap itself: closes the iRacing app, puts the current driver's profile aside, brings <paramref name="name"/>'s
    /// back and starts the app again, signed in as them. The sim has to be closed first.
    /// </summary>
    public async Task SwapToAsync(string name, IProgress<string> progress)
    {
        var current = Current ?? throw new InvalidOperationException("Name the driver who is signed in now first.");
        if (current.Equals(name, StringComparison.OrdinalIgnoreCase)) { StartUi(progress); return; }
        var saved = Directory.Exists(StoreDir) ? Directory.GetDirectories(StoreDir) : [];
        var from = saved.FirstOrDefault(d => (ReadMarker(d) ?? Path.GetFileName(d)).Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No saved sign-in for {name}.");
        CheckSim();
        var sw = Stopwatch.StartNew();
        await CloseUiAsync(progress);
        progress.Report($"Switching to {name}...");
        await MoveAsync(ActiveDir, DriverDir(current));
        try { await MoveAsync(from, ActiveDir); }
        catch
        {
            // never leave the app without a profile: put the previous driver back
            await MoveAsync(DriverDir(current), ActiveDir);
            throw;
        }
        Log.Write($"Driver swap: {current} -> {name} ({sw.Elapsed.TotalSeconds:0.0}s)");
        StartUi(progress);
    }

    static void CheckSim()
    {
        if (SimRunning) throw new InvalidOperationException("The sim is still running. Leave the car (ESC in your pit stall), close the sim, then swap.");
    }

    /// <summary>Closes the iRacing app: asks its window to close, then ends it if it's still there after a few seconds.</summary>
    async Task CloseUiAsync(IProgress<string> progress)
    {
        var procs = Process.GetProcessesByName(UiProcess);
        if (procs.Length == 0) return;
        progress.Report("Closing the iRacing app...");
        foreach (var p in procs)
        {
            try { if (p.MainModule?.FileName is { } f) _uiExe = f; } catch { }
            try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
        }
        for (int i = 0; i < 30 && Process.GetProcessesByName(UiProcess).Length > 0; i++) await Task.Delay(200);
        foreach (var p in Process.GetProcessesByName(UiProcess))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        for (int i = 0; i < 50 && Process.GetProcessesByName(UiProcess).Length > 0; i++) await Task.Delay(100);
        if (Process.GetProcessesByName(UiProcess).Length > 0) throw new InvalidOperationException("The iRacing app didn't close.");
    }

    /// <summary>Renames a folder, retrying briefly while the app's last files are let go.</summary>
    static async Task MoveAsync(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(to)) throw new InvalidOperationException($"{to} already exists; not overwriting it.");
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) when (attempt < 25) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) when (attempt < 25) { await Task.Delay(200); }
        }
    }

    void StartUi(IProgress<string> progress)
    {
        if (Process.GetProcessesByName(UiProcess).Length > 0) return;
        progress.Report("Starting the iRacing app...");
        Process.Start(new ProcessStartInfo(_uiExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_uiExe)! });
    }
}
