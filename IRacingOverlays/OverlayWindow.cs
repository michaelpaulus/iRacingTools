using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// Borderless, always-on-top window that never takes focus from iRacing (same approach as IRacingCoach's overlays).
/// Unlocked: drag the middle to move it, drag the left or right edge to resize it. Locked: clicks pass through to iRacing.
/// The height follows <see cref="DesiredHeight"/> so the window grows and shrinks with the number of rows.
/// </summary>
public sealed class OverlayWindow : Form
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    readonly Action<Rectangle> _changed;
    bool _locked;

    public Action<PaintEventArgs>? Painter;
    public Func<int, int>? DesiredHeight; // width -> height
    /// <summary>Called on the UI thread every timer tick, before repainting (e.g. to move between saved positions).</summary>
    public Action? Tick;

    public OverlayWindow(string title, Rectangle bounds, double opacity, bool locked, Action<Rectangle> changed, int minWidth)
    {
        _locked = locked;
        _changed = changed;
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Opacity = Math.Clamp(opacity, 0.2, 1.0);
        MinimumSize = new Size(minWidth, 40);
        Bounds = bounds;
        ResizeEnd += (_, _) => _changed(Bounds); // fires after a move or a resize

        _timer.Tick += (_, _) =>
        {
            Tick?.Invoke();
            if (DesiredHeight != null)
            {
                int h = Math.Max(MinimumSize.Height, DesiredHeight(ClientSize.Width));
                if (h != Height) Height = h;
            }
            Invalidate();
        };
        _timer.Start();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Locked
    {
        get => _locked;
        set
        {
            if (value == _locked) return;
            _locked = value;
            RecreateHandle(); // WS_EX_TRANSPARENT is only read when the window is created
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || _locked) return;

        long lp = m.LParam.ToInt64();
        var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
        const int grip = 8;
        int hit = p.X < grip ? 10 : p.X >= ClientSize.Width - grip ? 11 : 2; // left edge, right edge, caption (move)
        m.Result = (IntPtr)hit;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000,
                WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_locked) cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT; // mouse clicks pass through to iRacing
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) => Painter?.Invoke(e);
}
