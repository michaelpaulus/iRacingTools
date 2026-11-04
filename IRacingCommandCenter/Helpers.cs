using System.ComponentModel;
using System.Diagnostics;

namespace IRacingCommandCenter;

/// <summary>One helper program the command center manages (Crew Chief, the overlays, the voice agent, the coach).</summary>
public class HelperApp
{
    [Description("Shown in the window and the tray menu.")]
    public string Name { get; set; } = "";

    [Description("Program to run: an .exe, or \"dotnet\" to build and run a project folder with the arguments below.")]
    public string Path { get; set; } = "";

    [Description("Command-line arguments, e.g. \"run -c Release\" for a project run with dotnet.")]
    public string Args { get; set; } = "";

    [Description("Folder the program starts in (where it finds its settings file). Empty = the program's own folder.")]
    public string WorkingDir { get; set; } = "";

    [Description("Process name used to spot a copy that was started some other way (e.g. CrewChiefV4). Empty = only track what we started.")]
    public string ProcessName { get; set; } = "";

    [Description("Start it when iRacing starts.")]
    public bool StartWithIRacing { get; set; } = true;

    [Description("Stop it when iRacing closes.")]
    public bool StopWithIRacing { get; set; } = true;

    [Description("Run without a console window; its output goes to a log you can open from the window.")]
    public bool HideWindow { get; set; } = true;

    [Description("Its own settings file, opened by the Settings button. Relative paths are inside the working folder.")]
    public string SettingsFile { get; set; } = "";

    public HelperApp Clone() => (HelperApp)MemberwiseClone();
}

/// <summary>Starts, stops and watches the helper apps, and keeps a log of each one's output.</summary>
public class HelperManager
{
    readonly Config _config;
    readonly Dictionary<string, Process> _started = [];
    readonly object _gate = new();

    public HelperManager(Config config) => _config = config;

    public static string LogDir => System.IO.Path.Combine(Config.Dir, "logs");
    public static string LogPath(HelperApp app) => System.IO.Path.Combine(LogDir, $"{Safe(app.Name)}.log");

    static string Safe(string name) => string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public bool IsRunning(HelperApp app) => Ours(app) != null || External(app);

    /// <summary>"running", "running (started outside)", "stopped" or "exited (code)".</summary>
    public string Status(HelperApp app)
    {
        if (Ours(app) != null) return "running";
        if (External(app)) return "running (started outside)";
        lock (_gate)
            if (_started.TryGetValue(app.Name, out var p))
                try { return p.ExitCode == 0 ? "stopped" : $"exited ({p.ExitCode})"; } catch { }
        return "stopped";
    }

    Process? Ours(HelperApp app)
    {
        lock (_gate)
        {
            if (!_started.TryGetValue(app.Name, out var p)) return null;
            try { return p.HasExited ? null : p; } catch { return null; }
        }
    }

    bool External(HelperApp app) =>
        app.ProcessName.Length > 0 && Process.GetProcessesByName(app.ProcessName).Length > 0;

    public void Start(HelperApp app)
    {
        if (IsRunning(app)) return;
        try
        {
            string dir = app.WorkingDir.Length > 0 ? app.WorkingDir : System.IO.Path.GetDirectoryName(app.Path) ?? "";
            var psi = new ProcessStartInfo(app.Path, app.Args) { WorkingDirectory = dir };
            if (app.HideWindow)
            {
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }
            else psi.UseShellExecute = true;

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (app.HideWindow)
            {
                Directory.CreateDirectory(LogDir);
                var path = LogPath(app);
                TrimLog(path);
                File.AppendAllText(path, $"{Environment.NewLine}==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} started: {app.Path} {app.Args}{Environment.NewLine}");
                void Append(string? line) { if (line != null) lock (_gate) try { File.AppendAllText(path, line + Environment.NewLine); } catch { } }
                p.OutputDataReceived += (_, e) => Append(e.Data);
                p.ErrorDataReceived += (_, e) => Append(e.Data);
                p.Exited += (_, _) => Append($"==== {DateTime.Now:HH:mm:ss} exited ({SafeExitCode(p)})");
            }
            p.Start();
            if (app.HideWindow) { p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
            lock (_gate) _started[app.Name] = p;
            Log.Write($"Started {app.Name}");
        }
        catch (Exception ex)
        {
            Log.Write($"Error: couldn't start {app.Name}: {ex.Message}");
            throw new InvalidOperationException($"Couldn't start {app.Name}: {ex.Message}", ex);
        }
    }

    static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    /// <summary>Stops a copy we started (with everything it launched, e.g. dotnet run's child). Copies started outside are left alone.</summary>
    public void Stop(HelperApp app)
    {
        var p = Ours(app);
        if (p == null) return;
        try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); Log.Write($"Stopped {app.Name}"); }
        catch (Exception ex) { Log.Write($"Error: couldn't stop {app.Name}: {ex.Message}"); }
    }

    public void Restart(HelperApp app)
    {
        Stop(app);
        Start(app);
    }

    public void OnIRacingStarted()
    {
        foreach (var app in _config.Apps.Where(a => a.StartWithIRacing))
            try { Start(app); } catch { /* logged; the window shows it as stopped */ }
    }

    public void OnIRacingStopped()
    {
        foreach (var app in _config.Apps.Where(a => a.StopWithIRacing)) Stop(app);
    }

    /// <summary>On exit: the apps that close with iRacing. The rest (Crew Chief) keep running on their own.</summary>
    public void StopAll()
    {
        foreach (var app in _config.Apps.Where(a => a.StopWithIRacing)) Stop(app);
    }

    /// <summary>The app's settings file as a full path, or null when it has none.</summary>
    public static string? SettingsPath(HelperApp app)
    {
        if (app.SettingsFile.Length == 0) return null;
        if (System.IO.Path.IsPathRooted(app.SettingsFile)) return app.SettingsFile;
        string dir = app.WorkingDir.Length > 0 ? app.WorkingDir : System.IO.Path.GetDirectoryName(app.Path) ?? "";
        return System.IO.Path.Combine(dir, app.SettingsFile);
    }

    static void TrimLog(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 500_000) File.WriteAllLines(path, File.ReadAllLines(path).TakeLast(2000));
        }
        catch { }
    }
}
