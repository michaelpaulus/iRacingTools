using System.Text.Json;

namespace IRacingCommandCenter;

public class Config
{
    /// <summary>The Shelly plug's address and MAC. Empty = find it on the network the first time (see ShellyClient).</summary>
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public int[] Outlets { get; set; } = [1];
    public bool Auto { get; set; } = true;
    public string[] Processes { get; set; } = ["iRacingUI", "iRacingSim64DX11"];
    /// <summary>Other sims that also turn the outlets on (AC EVO, AC Rally). Helper apps and OneDrive still follow iRacing only.</summary>
    public string[] OtherSims { get; set; } = ["AssettoCorsaEVO", "acr"];
    /// <summary>Keep these sims' windows stretched across all three screens.</summary>
    public bool SpanWindows { get; set; } = true;
    public string[] SpanProcesses { get; set; } = ["iRacingSim64DX11", "AssettoCorsaEVO", "acr"];
    public int PollSeconds { get; set; } = 5;
    public int OffDelaySeconds { get; set; } = 60;
    /// <summary>Shut OneDrive down while the sim runs and start it again when the sim closes.</summary>
    public bool PauseOneDrive { get; set; } = true;
    // Before the app list existed, Crew Chief was the only helper. Only read when Apps is empty, to build the list.
    public bool LaunchCrewChief { get; set; } = true;
    public string CrewChiefPath { get; set; } = @"C:\Program Files (x86)\Britton IT Ltd\CrewChiefV4\CrewChiefV4.exe";
    public string CrewChiefArgs { get; set; } = "-game IRACING";

    /// <summary>Helper programs started/stopped with iRacing and controlled from the window.</summary>
    public List<HelperApp> Apps { get; set; } = [];
    /// <summary>
    /// Processes that mean "the game is running" for the helper apps: the sim itself, not the launcher, so the
    /// overlays and voice agent start when you load into a session. (The power outlets follow Processes.)
    /// </summary>
    public string[] AppProcesses { get; set; } = ["iRacingSim64DX11"];

    /// <summary>Fills in the default helpers the first time: Crew Chief, and the overlays, voice agent and coach projects.</summary>
    void AddDefaultApps()
    {
        Apps.Add(new HelperApp
        {
            Name = "Crew Chief", Path = CrewChiefPath, Args = CrewChiefArgs, ProcessName = "CrewChiefV4",
            StartWithIRacing = LaunchCrewChief, StopWithIRacing = false, HideWindow = false,
        });
        var root = FindProjectsRoot();
        if (root == null) return;
        HelperApp Project(string folder, string name, string settings, bool withIRacing) => new()
        {
            Name = name, Path = "dotnet", Args = "run -c Release", WorkingDir = Path.Combine(root, folder),
            SettingsFile = settings, StartWithIRacing = withIRacing, StopWithIRacing = true, HideWindow = true,
        };
        Apps.Add(Project("IRacingOverlays", "Overlays", "overlays.json", true));
        Apps.Add(Project("IRacingVoice", "Voice agent", "voice.json", true));
        Apps.Add(Project("IRacingCoach", "Coach", "coach.json", false));
    }

    /// <summary>
    /// The folder holding the IRacingOverlays / IRacingVoice / IRacingCoach projects: above this exe, or the git checkout
    /// in Documents\source\repos\iRacingTools, or the old Documents\IRacingCoach.
    /// </summary>
    static string? FindProjectsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "IRacingOverlays"))) return dir.FullName;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new[] { Path.Combine(docs, "source", "repos", "iRacingTools"), Path.Combine(docs, "IRacingCoach") }
            .FirstOrDefault(root => Directory.Exists(Path.Combine(root, "IRacingOverlays")));
    }

    /// <summary>
    /// After the projects move (e.g. into the git checkout), point any helper whose folder is gone at the folder of the
    /// same name in the projects root. Returns true when something changed.
    /// </summary>
    bool RepointMovedApps()
    {
        var root = FindProjectsRoot();
        if (root == null) return false;
        bool changed = false;
        foreach (var app in Apps)
        {
            if (app.WorkingDir.Length == 0 || Directory.Exists(app.WorkingDir)) continue;
            var moved = Path.Combine(root, Path.GetFileName(app.WorkingDir.TrimEnd('\\', '/')));
            if (!Directory.Exists(moved)) continue;
            Log.Write($"{app.Name}: {app.WorkingDir} is gone; using {moved}");
            app.WorkingDir = moved;
            changed = true;
        }
        return changed;
    }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iRacingCommandCenter");
    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Config Load()
    {
        Config? config = null;
        try
        {
            if (File.Exists(FilePath))
                config = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Options);
        }
        catch (Exception ex)
        {
            Log.Write($"Bad config, using defaults: {ex.Message}");
        }
        config ??= new Config();
        if (config.Apps.Count == 0)
        {
            config.AddDefaultApps();
            Log.Write($"Helper apps set up: {string.Join(", ", config.Apps.Select(a => a.Name))}");
        }
        else config.RepointMovedApps();
        config.Save();
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}

public static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(Config.Dir, "CommandCenter.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                // keep the log small
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 200_000)
                    File.WriteAllLines(FilePath, File.ReadAllLines(FilePath).TakeLast(500));
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
