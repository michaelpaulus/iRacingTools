using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

/// <summary>
/// Drives iRacing's replay through the SDK broadcast message: play speed, seeking, camera switches and the camera state
/// (hiding the replay UI). Enum values match irsdk_defines.h (irsdk_BroadcastMsg, irsdk_CameraState).
/// </summary>
public static class Sim
{
    enum Broadcast
    {
        CamSwitchPos = 0, CamSwitchNum = 1, CamSetState = 2, ReplaySetPlaySpeed = 3, ReplaySetPlayPosition = 4,
        ReplaySearch = 5, ReplaySetState = 6, ReplaySearchSessionTime = 12,
    }

    [Flags]
    public enum CameraState
    {
        IsSessionScreen = 0x01, IsScenicActive = 0x02, CamToolActive = 0x04, UIHidden = 0x08,
        UseAutoShotSelection = 0x10, UseTemporaryEdits = 0x20, UseKeyAcceleration = 0x40, UseKey10xAcceleration = 0x80,
        UseMouseAimMode = 0x100,
    }

    public const string ProcessName = "iRacingSim64DX11";

    static readonly uint MsgId = Native.RegisterWindowMessage("IRSDK_BROADCASTMSG");

    static void Send(Broadcast type, int var1, int var2 = 0) =>
        Native.SendNotifyMessage((IntPtr)0xFFFF, MsgId, (IntPtr)((int)type | (var1 << 16)), (IntPtr)var2);

    // Messages with two small arguments after the first pack them into one (irsdk's MAKELONG(var2, var3)).
    static void Send(Broadcast type, int var1, int var2, int var3) => Send(type, var1, (var2 & 0xFFFF) | (var3 << 16));

    /// <summary>Replay speed: 0 = pause, 1 = normal, up to 16; slow motion plays at 1/<paramref name="speed"/>.</summary>
    public static void ReplaySpeed(int speed, bool slowMotion = false) => Send(Broadcast.ReplaySetPlaySpeed, speed, slowMotion ? 1 : 0);

    /// <summary>Jumps to a moment of a session (its SessionTime in seconds).</summary>
    public static void SeekSessionTime(int sessionNum, double sessionTime) =>
        Send(Broadcast.ReplaySearchSessionTime, sessionNum, (int)Math.Max(0, sessionTime * 1000));

    /// <summary>To the start of the replay.</summary>
    public static void SeekStart() => Send(Broadcast.ReplaySearch, 0);

    /// <summary>Points the camera at a car using a camera group from the session info.</summary>
    public static void Camera(string carNumber, int group) => Send(Broadcast.CamSwitchNum, PadCarNum(carNumber), group, 0);

    public static void SetCameraState(CameraState state) => Send(Broadcast.CamSetState, (int)state);

    /// <summary>
    /// The number CamSwitchNum takes: the plain number, with leading zeros ("007", "07") encoded the way
    /// irsdk_padCarNum does, since they are different cars.
    /// </summary>
    public static int PadCarNum(string carNumber)
    {
        var digits = new string(carNumber.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return 0;
        int num = int.Parse(digits);
        int zeros = digits.TakeWhile(c => c == '0').Count();
        if (num == 0) zeros = Math.Max(0, digits.Length - 1);
        if (zeros == 0) return num;
        int place = num > 99 ? 3 : num > 9 ? 2 : 1;
        return num + 1000 * (place + zeros);
    }

    public static Process? FindProcess() => Process.GetProcessesByName(ProcessName).FirstOrDefault();

    /// <summary>The sim window's client area in screen pixels, or null when it isn't open.</summary>
    public static Rectangle? WindowRect(out IntPtr hwnd)
    {
        hwnd = FindProcess()?.MainWindowHandle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || !Native.GetClientRect(hwnd, out var r)) return null;
        var p = new Native.POINT();
        Native.ClientToScreen(hwnd, ref p);
        return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Brings the sim to the front (capture records the screen, so nothing may cover it).</summary>
    public static void BringToFront()
    {
        WindowRect(out var hwnd);
        if (hwnd == IntPtr.Zero) return;
        Native.ShowWindow(hwnd, 9); // SW_RESTORE
        // Windows only lets the foreground app hand focus over; a tap of Alt counts as input and unlocks it.
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        Native.SetForegroundWindow(hwnd);
    }

    /// <summary>The sim has keyboard focus.</summary>
    public static bool Focused()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName.StartsWith("iRacingSim", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Brings the sim to the front and waits until it really has focus and is taking keys (it ignores them for a
    /// moment after it's activated). False if it never got focus.
    /// </summary>
    public static bool FocusForKeys(int timeoutMs = 4000)
    {
        BringToFront();
        var sw = Stopwatch.StartNew();
        while (!Focused())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(100);
            if (sw.ElapsedMilliseconds % 1000 < 100) BringToFront();
        }
        Thread.Sleep(1000);
        return Focused();
    }

    /// <summary>
    /// Presses one of the sim's control bindings, like "Shift+Ctrl+Alt+M" (modifiers held while the last key is
    /// tapped), as scan codes, which DirectX games read. Keys go to whatever has focus: false if that isn't the sim.
    /// </summary>
    public static bool PressKeys(string combo, bool scanCodes = true)
    {
        if (!Focused()) return false;

        var named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) { ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12 };
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k =>
            named.TryGetValue(k, out var vk) ? vk
            : k.Length == 1 ? (ushort)char.ToUpperInvariant(k[0]) // letters and digits: VK = ASCII
            : k.StartsWith('F') && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24 ? (ushort)(0x70 + f - 1)
            : throw new ArgumentException($"Unknown key '{k}' in \"{combo}\"")).ToList();
        Native.INPUT Key(ushort vk, bool up) => new()
        {
            type = 1, // keyboard
            ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = (up ? 0x0002u : 0) | (scanCodes ? 0x0008u : 0) }, // KEYUP, SCANCODE
        };
        // The sim reads the keyboard once a frame, so an instant tap can be missed: hold each step for a few frames.
        void Send(params Native.INPUT[] inputs) { Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()); Thread.Sleep(60); }
        foreach (var k in keys.SkipLast(1)) Send(Key(k, false));
        Send(Key(keys[^1], false));
        Thread.Sleep(60);
        Send(Key(keys[^1], true));
        foreach (var k in Enumerable.Reverse(keys).Skip(1)) Send(Key(k, true));
        return true;
    }

    /// <summary>Minimises (or restores) this app's windows, so they don't end up in the recording.</summary>
    public static void MinimizeApp(bool minimize)
    {
        var console = Native.GetConsoleWindow();
        if (console != IntPtr.Zero && Native.IsWindowVisible(console)) Native.ShowWindow(console, minimize ? 6 : 9); // SW_MINIMIZE / SW_RESTORE
        foreach (var form in System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().ToList())
            form.Invoke(() => form.WindowState = minimize ? System.Windows.Forms.FormWindowState.Minimized : System.Windows.Forms.FormWindowState.Normal);
        if (!minimize) return;
        Thread.Sleep(400); // the minimise animation
        BringToFront();
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);

        // The union's mouse member is larger than the keyboard one; the explicit size keeps INPUT the size Windows expects.
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    }
}
