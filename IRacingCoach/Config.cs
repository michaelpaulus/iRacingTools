using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class PushToTalkConfig
{
    /// <summary>"None", "Keyboard" or "Joystick". Run "IRacingCoach bind" to set it.</summary>
    public string Type { get; set; } = "None";
    /// <summary>Windows virtual-key code when Type is Keyboard.</summary>
    public int Key { get; set; }
    /// <summary>Joystick manufacturer/product IDs, so the device is found again if its slot changes.</summary>
    public int JoystickMid { get; set; }
    public int JoystickPid { get; set; }
    public string JoystickName { get; set; } = "";
    /// <summary>Zero-based button number (Windows exposes the first 32 buttons).</summary>
    public int Button { get; set; }
}

public sealed class CoachConfig
{
    public string ReferenceCsv { get; set; } = "";
    public string ReferenceName { get; set; } = "the reference";
    /// <summary>iRacing internal track name the reference lap belongs to; coaching is off on other tracks.</summary>
    public string ReferenceTrack { get; set; } = "nurburgring combinedshortb";
    public double TrackLengthKm { get; set; } = 24.1544;
    public int DistanceBins { get; set; } = 2415;

    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low / medium / high. Low keeps radio answers quick.</summary>
    public string Effort { get; set; } = "low";

    public string Voice { get; set; } = "Microsoft Zira Desktop";
    public int VoiceRate { get; set; } = 1;
    public PushToTalkConfig PushToTalk { get; set; } = new();

    /// <summary>Only call out a corner when at least this much time was lost to the reference.</summary>
    public double MinSecondsLostToCall { get; set; } = 0.3;
    public double MinSecondsBetweenCallouts { get; set; } = 6;
    /// <summary>Drop corner callouts that waited longer than this (stale by the next corner).</summary>
    public double MaxCalloutAgeSeconds { get; set; } = 5;
    public bool Praise { get; set; } = true;
    /// <summary>Speak the per-corner callouts. When false they are only printed in the console.</summary>
    public bool SpokenCallouts { get; set; } = false;

    /// <summary>Always-on-top window with the live gap, corner bar and track map (works with iRacing windowed/borderless).</summary>
    public bool ShowOverlay { get; set; } = true;
    /// <summary>Screen position; int.MinValue = top-left of the primary screen. Saved when you drag the overlay.</summary>
    public int OverlayX { get; set; } = int.MinValue;
    public int OverlayY { get; set; } = int.MinValue;
    public int OverlayWidth { get; set; } = 300;
    public int OverlayHeight { get; set; } = 380;

    /// <summary>Brake/throttle comparison window. Drag to move, drag an edge to resize.</summary>
    public bool ShowPedals { get; set; } = true;
    public int PedalX { get; set; } = int.MinValue;
    public int PedalY { get; set; } = int.MinValue;
    public int PedalWidth { get; set; } = 380;
    public int PedalHeight { get; set; } = 150;
    public double OverlayOpacity { get; set; } = 0.85;
    /// <summary>Virtual-key code that hides/shows the overlays. 145 = Scroll Lock.</summary>
    public int OverlayToggleKey { get; set; } = 145;

    /// <summary>Thin "relative" window: the reference car ahead/behind you by track distance.</summary>
    public bool ShowStrip { get; set; } = true;
    public int StripX { get; set; } = int.MinValue;
    public int StripY { get; set; } = int.MinValue;
    public int StripWidth { get; set; } = 110;
    public int StripHeight { get; set; } = 380;

    /// <summary>Box over the game roughly where the reference car would be through your windscreen.</summary>
    public bool ShowWindscreenMarker { get; set; } = true;
    /// <summary>Monitor to draw it on (index into Windows' screen list); -1 = primary (your centre screen).</summary>
    public int MarkerScreen { get; set; } = -1;
    /// <summary>Horizontal field of view of that screen in degrees; 0 = work it out from iRacing's monitor setup.</summary>
    public double MarkerFovDeg { get; set; } = 0;
    /// <summary>Shift the marker up (negative) or down (positive) if it sits above/below the road.</summary>
    public int MarkerHorizonOffsetPx { get; set; } = 0;
    public double MarkerEyeHeightM { get; set; } = 1.0;
    public double MarkerMaxDistanceM { get; set; } = 250;
    public double MarkerOpacity { get; set; } = 0.6;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string PathFor(string baseDir) => Path.Combine(baseDir, "coach.json");

    public static CoachConfig Load(string baseDir)
    {
        var path = PathFor(baseDir);
        if (!File.Exists(path))
        {
            var fresh = new CoachConfig();
            fresh.Save(baseDir);
            return fresh;
        }
        return JsonSerializer.Deserialize<CoachConfig>(File.ReadAllText(path), Json) ?? new CoachConfig();
    }

    public void Save(string baseDir) => File.WriteAllText(PathFor(baseDir), JsonSerializer.Serialize(this, Json));
}
