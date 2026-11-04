using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IRacingCommandCenter;

/// <summary>
/// Keeps each sim's window stretched across all the monitors. AC EVO and AC Rally open (or fall back after a video
/// settings change) on one screen; a borderless window is put back over the whole span, a bordered one (iRacing in
/// windowed mode) over the combined work area so its title bar stays visible.
/// </summary>
public static class TripleSpan
{
    const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    public static void Apply(IEnumerable<string> processNames)
    {
        var screens = Screen.AllScreens;
        if (screens.Length < 2) return;
        var span = screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
        var work = screens.Select(s => s.WorkingArea).Aggregate(Rectangle.Union);

        foreach (var name in processNames)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    try { Stretch(p, span, work); }
                    catch (Exception ex) { Log.Write($"Couldn't stretch {name}: {ex.Message}"); }
                }
    }

    static void Stretch(Process p, Rectangle span, Rectangle work)
    {
        var h = p.MainWindowHandle;
        if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h) || !GetWindowRect(h, out var win)) return;

        // The visible frame; Windows 11 adds invisible resize borders around bordered windows.
        var frame = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out var f, Marshal.SizeOf<RECT>()) == 0 ? f : win;
        int width = frame.Right - frame.Left, height = frame.Bottom - frame.Top;
        // Leave splash screens and launchers alone.
        if (width < 1200 || height < 700) return;
        bool bordered = (GetWindowLong(h, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        // A borderless window must cover the span exactly; a bordered one that nearly spans is a user tweak and sticks.
        if (bordered ? width >= span.Width * 0.95
                     : frame.Left == span.Left && frame.Top == span.Top && width == span.Width && height == span.Height) return;

        var target = bordered ? work : span;
        int left = target.Left - (frame.Left - win.Left), top = target.Top - (frame.Top - win.Top);
        int right = target.Right + (win.Right - frame.Right), bottom = target.Bottom + (win.Bottom - frame.Bottom);
        SetWindowPos(h, IntPtr.Zero, left, top, right - left, bottom - top, SWP_NOZORDER | SWP_NOACTIVATE);
        Log.Write($"Stretched {p.ProcessName} from {width}x{height} across all screens ({target.Width}x{target.Height})");
    }
}
