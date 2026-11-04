using System.Runtime.InteropServices;

/// <summary>Reads a keyboard key or a wheel/joystick button, even while iRacing has focus.</summary>
public sealed class PushToTalk
{
    readonly PushToTalkConfig _cfg;
    int _joyId = -1;
    DateTime _nextJoySearch = DateTime.MinValue;

    public PushToTalk(PushToTalkConfig cfg) => _cfg = cfg;

    public static bool IsKeyDown(int vk) => vk > 0 && (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public bool IsConfigured =>_cfg.Type is "Keyboard" or "Joystick";

    public string Describe() => _cfg.Type switch
    {
        "Keyboard" => $"keyboard key {(ConsoleKey)_cfg.Key}",
        "Joystick" => $"button {_cfg.Button + 1} on {_cfg.JoystickName}",
        _ => "not set (run: IRacingCoach bind)",
    };

    public bool IsHeld()
    {
        switch (_cfg.Type)
        {
            case "Keyboard":
                return (Native.GetAsyncKeyState(_cfg.Key) & 0x8000) != 0;
            case "Joystick":
                if (_joyId < 0 || !Native.TryReadButtons(_joyId, out uint buttons))
                {
                    // Device may have changed slot (e.g. after unplugging); look it up by its IDs again.
                    if (DateTime.UtcNow < _nextJoySearch) return false;
                    _nextJoySearch = DateTime.UtcNow.AddSeconds(2);
                    _joyId = Native.FindJoystick(_cfg.JoystickMid, _cfg.JoystickPid);
                    return false;
                }
                return (buttons & (1u << _cfg.Button)) != 0;
            default:
                return false;
        }
    }

    /// <summary>Waits for the next key or button press and stores it in the config.</summary>
    public static bool Bind(PushToTalkConfig cfg, TimeSpan timeout)
    {
        var baseline = new uint[16];
        for (int j = 0; j < 16; j++) Native.TryReadButtons(j, out baseline[j]);
        var keysDown = new bool[256];
        for (int k = 8; k < 256; k++) keysDown[k] = (Native.GetAsyncKeyState(k) & 0x8000) != 0;

        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            for (int j = 0; j < 16; j++)
            {
                if (!Native.TryReadButtons(j, out uint now)) continue;
                uint pressed = now & ~baseline[j];
                if (pressed != 0)
                {
                    var caps = Native.Caps(j);
                    cfg.Type = "Joystick";
                    cfg.Button = System.Numerics.BitOperations.TrailingZeroCount(pressed);
                    cfg.JoystickMid = caps.wMid;
                    cfg.JoystickPid = caps.wPid;
                    cfg.JoystickName = string.IsNullOrWhiteSpace(caps.szPname) ? $"joystick {j}" : caps.szPname;
                    return true;
                }
                baseline[j] = now;
            }
            for (int k = 8; k < 256; k++)
            {
                if (k is 0x10 or 0x11 or 0x12) continue; // generic Shift/Ctrl/Alt: the L/R variants are reported too
                bool down = (Native.GetAsyncKeyState(k) & 0x8000) != 0;
                if (down && !keysDown[k])
                {
                    cfg.Type = "Keyboard";
                    cfg.Key = k;
                    return true;
                }
                keysDown[k] = down;
            }
            Thread.Sleep(15);
        }
        return false;
    }

    static class Native
    {
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        struct JOYINFOEX
        {
            public int dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
            public uint dwButtons;
            public int dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct JOYCAPS
        {
            public ushort wMid, wPid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax,
                wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
        }

        [DllImport("winmm.dll")]
        static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int joyGetDevCapsW(IntPtr uJoyID, ref JOYCAPS pjc, int cbjc);

        const int JOY_RETURNBUTTONS = 0x80;

        public static bool TryReadButtons(int id, out uint buttons)
        {
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
            bool ok = joyGetPosEx(id, ref info) == 0;
            buttons = ok ? info.dwButtons : 0;
            return ok;
        }

        public static JOYCAPS Caps(int id)
        {
            var caps = new JOYCAPS();
            joyGetDevCapsW(id, ref caps, Marshal.SizeOf<JOYCAPS>());
            return caps;
        }

        public static int FindJoystick(int mid, int pid)
        {
            for (int j = 0; j < 16; j++)
            {
                if (!TryReadButtons(j, out _)) continue;
                var c = Caps(j);
                if (c.wMid == mid && c.wPid == pid) return j;
            }
            return -1;
        }
    }
}
