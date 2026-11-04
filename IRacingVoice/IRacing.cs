using System.Runtime.InteropServices;

/// <summary>
/// Sends commands to iRacing through the SDK's broadcast message (pit service, chat, force feedback) and reads the
/// current pit-service selections. Enum values match irsdk_defines.h (BroadcastMsg / PitCommandMode / ChatCommandMode).
/// </summary>
public static class IRacing
{
    enum Broadcast
    {
        CamSwitchNum = 1, ReplaySetPlaySpeed = 3, ReplaySetPlayPosition = 4, ReplaySearch = 5,
        ChatCommand = 8, PitCommand = 9, TelemCommand = 10, FfbCommand = 11, VideoCapture = 13,
    }
    public enum Pit { Clear = 0, Windshield = 1, Fuel = 2, LF = 3, RF = 4, LR = 5, RR = 6, ClearTires = 7, FastRepair = 8, ClearWindshield = 9, ClearFastRepair = 10, ClearFuel = 11, Compound = 12 }
    enum Chat { Macro = 0, BeginChat = 1, Reply = 2, Cancel = 3 }
    public enum Video { Screenshot = 0, Start = 1, Stop = 2 }
    enum ReplayPos { Begin = 0, Current = 1, End = 2 }
    enum ReplaySearchMode { ToStart = 0, ToEnd = 1 }

    // PitSvFlags bits.
    const int LfTire = 0x01, RfTire = 0x02, LrTire = 0x04, RrTire = 0x08, FuelFill = 0x10, Tearoff = 0x20, FastRepairFlag = 0x40;

    static readonly uint MsgId = Native.RegisterWindowMessage("IRSDK_BROADCASTMSG");

    static void Send(Broadcast type, int var1, int var2 = 0) =>
        Native.SendNotifyMessage((IntPtr)0xFFFF, MsgId, (IntPtr)((int)type | (var1 << 16)), (IntPtr)var2);

    // Messages with two small arguments after the first pack them into one (irsdk's MAKELONG(var2, var3)).
    static void Send(Broadcast type, int var1, int var2, int var3) => Send(type, var1, (var2 & 0xFFFF) | (var3 << 16));

    public static void PitCommand(Pit command, int value = 0) => Send(Broadcast.PitCommand, (int)command, value);

    /// <summary>iRacing's telemetry recording (.ibt in Documents\iRacing\telemetry), like Alt+L.</summary>
    public static void TelemetryLogging(bool on) => Send(Broadcast.TelemCommand, on ? 1 : 0);

    /// <summary>iRacing's own screenshot / video capture.</summary>
    public static void VideoCapture(Video what) => Send(Broadcast.VideoCapture, (int)what);

    /// <summary>Plays the last <paramref name="seconds"/> again at normal speed (the replay buffer is 60 frames a second).</summary>
    public static void InstantReplay(double seconds)
    {
        Send(Broadcast.ReplaySetPlayPosition, (int)ReplayPos.End, -(int)Math.Round(seconds * 60));
        Send(Broadcast.ReplaySetPlaySpeed, 1, 0);
    }

    /// <summary>Back to the live view: to the end of the replay, at normal speed.</summary>
    public static void BackToLive()
    {
        Send(Broadcast.ReplaySearch, (int)ReplaySearchMode.ToEnd);
        Send(Broadcast.ReplaySetPlaySpeed, 1, 0);
    }

    /// <summary>Replay speed: 0 = pause, 1 = normal; slow motion plays at 1/<paramref name="speed"/>.</summary>
    public static void ReplaySpeed(int speed, bool slowMotion = false) => Send(Broadcast.ReplaySetPlaySpeed, speed, slowMotion ? 1 : 0);

    /// <summary>Points the camera at a car (by its number) using a camera group from the session info.</summary>
    public static void Camera(int carNumber, int group) => Send(Broadcast.CamSwitchNum, carNumber, group, 0);

    /// <summary>Wheel force in newton-metres (iRacing's "max force" setting).</summary>
    public static void SetMaxForce(double newtonMetres) => Send(Broadcast.FfbCommand, 0, (int)(newtonMetres * 65536));

    /// <summary>
    /// Opens iRacing's chat box, types the text and sends it. Only when the sim is the focused window: the keystrokes
    /// go to whatever has focus, so otherwise they'd land in another app (and press Enter there).
    /// </summary>
    public static void SendChat(string text)
    {
        if (!SimFocused()) throw new InvalidOperationException("iRacing isn't the focused window");
        Send(Broadcast.ChatCommand, (int)Chat.BeginChat);
        Thread.Sleep(150);
        Keyboard.Type(text);
        Thread.Sleep(50);
        Keyboard.Press("Enter");
    }

    public const double LitresPerGallon = 3.785;

    /// <summary>iRacing's own units setting (DisplayUnits: 0 = imperial, 1 = metric); metric when not connected.</summary>
    public static bool Metric(ITelemetrySource t) => ForceMetric ?? (!t.IsConnected || !t.Has("DisplayUnits") || t.Get("DisplayUnits") > 0);

    /// <summary>Overrides iRacing's setting (test mode: --gallons / --litres).</summary>
    public static bool? ForceMetric { get; set; }

    /// <summary>A fuel amount (litres, as the SDK reports it) in the driver's units, for speaking: "5.3 gallons" / "20 litres".</summary>
    public static string Fuel(double litres, bool metric)
    {
        double v = metric ? litres : litres / LitresPerGallon;
        string n = v < 10 ? v.ToString("0.#") : v.ToString("0");
        return metric ? $"{n} {(n == "1" ? "litre" : "litres")}" : $"{n} {(n == "1" ? "gallon" : "gallons")}";
    }

    /// <summary>iRacing's auto fuel setting (false when this iRacing version doesn't report it).</summary>
    public static bool AutoFuel(LiveTelemetry t) => t.IsConnected && t.Has("dpFuelAutoFillEnabled") && t.Get("dpFuelAutoFillEnabled") > 0;

    /// <summary>Whether any tyre change is selected for the next stop (for "toggle tyres").</summary>
    public static bool TyresSelected(LiveTelemetry t) =>
        t.IsConnected && t.Has("PitSvFlags") && ((int)t.Get("PitSvFlags") & (LfTire | RfTire | LrTire | RrTire)) != 0;

    /// <summary>The next pit stop's settings as one spoken sentence: "Next stop: no tyres, 5 gallons, tear-off."</summary>
    public static string PitSummary(LiveTelemetry t)
    {
        if (!t.IsConnected || !t.Has("PitSvFlags")) return "I can't see the pit settings right now.";
        int flags = (int)t.Get("PitSvFlags");
        int tyres = (flags & (LfTire | RfTire | LrTire | RrTire));
        string tyreText = tyres switch
        {
            0 => "no tyres",
            LfTire | RfTire | LrTire | RrTire => "four tyres",
            LfTire | LrTire => "lefts",
            RfTire | RrTire => "rights",
            LfTire | RfTire => "fronts",
            LrTire | RrTire => "rears",
            _ => "some tyres",
        };
        var parts = new List<string> { tyreText };
        parts.Add((flags & FuelFill) != 0 ? Fuel(t.Get("PitSvFuel"), Metric(t)) : "no fuel");
        if (AutoFuel(t)) parts.Add("auto fuel on");
        if ((flags & Tearoff) != 0) parts.Add("tear-off");
        if ((flags & FastRepairFlag) != 0) parts.Add("fast repair");
        return "Next stop: " + string.Join(", ", parts) + ".";
    }

    /// <summary>Current pit-service selections and fuel, in plain words, for the agent's context.</summary>
    public static string PitState(LiveTelemetry t)
    {
        if (!t.IsConnected || !t.Has("PitSvFlags")) return "Not connected to iRacing (not in a car).";
        bool metric = Metric(t);
        int flags = (int)t.Get("PitSvFlags");
        var tires = new List<string>();
        if ((flags & LfTire) != 0) tires.Add("LF");
        if ((flags & RfTire) != 0) tires.Add("RF");
        if ((flags & LrTire) != 0) tires.Add("LR");
        if ((flags & RrTire) != 0) tires.Add("RR");
        string fuel = (flags & FuelFill) != 0 ? $"adding {Fuel(t.Get("PitSvFuel"), metric)}" : "no fuel";
        return $"Pit service now: tyres {(tires.Count == 0 ? "none" : string.Join(" ", tires))}; {fuel}; " +
               $"auto fuel {(AutoFuel(t) ? "on" : "off")}; tear-off {((flags & Tearoff) != 0 ? "on" : "off")}; fast repair {((flags & FastRepairFlag) != 0 ? "on" : "off")}. " +
               $"Fuel in the car: {Fuel(t.Get("FuelLevel"), metric)}. " +
               $"The driver uses {(metric ? "litres" : "gallons")}: a fuel number without a unit means {(metric ? "litres" : "gallons")}.";
    }

    /// <summary>The iRacing sim (not the launcher, not another app) has keyboard focus.</summary>
    public static bool SimFocused()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.StartsWith("iRacingSim", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}

/// <summary>Synthesised key presses (scan codes, which DirectX games read) and typed text.</summary>
public static class Keyboard
{
    static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["enter"] = 0x0D, ["return"] = 0x0D,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20, ["tab"] = 0x09, ["scrolllock"] = 0x91, ["pause"] = 0x13,
    };

    /// <summary>Presses a combination like "Ctrl+Shift+Alt+D" (modifiers held while the last key is tapped).</summary>
    public static void Press(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Vk).ToList();
        var inputs = new List<INPUT>();
        foreach (var k in keys) inputs.Add(Key(k, up: false));
        for (int i = keys.Count - 1; i >= 0; i--) inputs.Add(Key(keys[i], up: true));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            inputs.Add(Unicode(c, up: false));
            inputs.Add(Unicode(c, up: true));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    static ushort Vk(string name)
    {
        if (Named.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1) return (ushort)char.ToUpperInvariant(name[0]); // letters and digits: VK = ASCII
        if (name.StartsWith('F') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24) return (ushort)(0x70 + f - 1);
        throw new ArgumentException($"Unknown key '{name}'");
    }

    static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = (up ? 0x0002u : 0) | 0x0008u } },
    };

    static INPUT Unicode(char c, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004u | (up ? 0x0002u : 0) } },
    };

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);
}
