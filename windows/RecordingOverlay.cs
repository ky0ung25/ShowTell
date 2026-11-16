using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace ShowTell;

/// <summary>
/// Small "● 0:12" pill at the top of the recorded screen. Click-through and hidden from screen capture.
/// This is a WinForms form on purpose: Windows refuses WDA_EXCLUDEFROMCAPTURE on WPF's per-pixel transparent
/// (UpdateLayeredWindow) windows, and WPF strips WS_EX_LAYERED from normal ones, which breaks click-through.
/// A WinForms form with alpha-only layering supports both.
/// </summary>
public sealed class RecordingOverlay : WinForms.Form
{
    readonly Rectangle _screenPx;
    readonly Func<TimeSpan> _elapsed;
    readonly string _hint;
    readonly WinForms.Timer _timer;
    readonly DateTime _shownAt = DateTime.UtcNow;
    Font? _timeFont, _hintFont;
    float _scale = 1;
    string _timeText = "0:00";

    static readonly Color PillColor = Color.FromArgb(0x18, 0x18, 0x1B);
    static readonly Color DotColor = Color.FromArgb(0xE5, 0x48, 0x4D);

    public RecordingOverlay(Rectangle screenPx, Func<TimeSpan> elapsed, string hotkeyHint)
    {
        _screenPx = screenPx;
        _elapsed = elapsed;
        _hint = hotkeyHint;
        FormBorderStyle = WinForms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = WinForms.FormStartPosition.Manual;
        AutoScaleMode = WinForms.AutoScaleMode.None;
        BackColor = PillColor;
        Opacity = 0.93;
        DoubleBuffered = true;
        // Start on the recorded monitor so DeviceDpi is that monitor's DPI.
        Bounds = new Rectangle(screenPx.Left + screenPx.Width / 2 - 100, screenPx.Top + 12, 200, 34);

        _timer = new WinForms.Timer { Interval = 80 };
        _timer.Tick += (_, _) =>
        {
            _timeText = Transcriber.Format(_elapsed());
            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override WinForms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }
    const int WS_EX_TOPMOST = 0x8;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE)) // keep the pill out of the frames
            Log.Write($"Overlay: SetWindowDisplayAffinity failed ({Marshal.GetLastWin32Error()}); the pill may appear in frames");
        LayoutPill();
    }

    void LayoutPill()
    {
        _scale = DeviceDpi / 96f;
        _timeFont?.Dispose(); _hintFont?.Dispose();
        _timeFont = new Font("Segoe UI Semibold", 13f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _hintFont = new Font("Segoe UI", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);

        var timeW = WinForms.TextRenderer.MeasureText("00:00", _timeFont).Width;
        var hintW = WinForms.TextRenderer.MeasureText(_hint, _hintFont).Width;
        int w = (int)(14 * _scale + 10 * _scale + 8 * _scale) + timeW + (int)(6 * _scale) + hintW + (int)(14 * _scale);
        int h = (int)Math.Round(32 * _scale);
        Bounds = new Rectangle(_screenPx.Left + (_screenPx.Width - w) / 2, _screenPx.Top + (int)(12 * _scale), w, h);

        using var path = RoundRect(new Rectangle(0, 0, w, h), h / 2);
        Region = new Region(path);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void OnPaint(WinForms.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        if (_timeFont == null || _hintFont == null) return;

        // Pulsing red dot.
        double t = (DateTime.UtcNow - _shownAt).TotalMilliseconds / 700.0;
        double pulse = 0.25 + 0.75 * (0.5 + 0.5 * Math.Cos(Math.PI * t));
        float d = 10 * _scale, x = 14 * _scale, cy = Height / 2f;
        using (var dot = new SolidBrush(Blend(DotColor, PillColor, pulse)))
            g.FillEllipse(dot, x, cy - d / 2, d, d);
        x += d + 8 * _scale;

        var flags = WinForms.TextFormatFlags.VerticalCenter | WinForms.TextFormatFlags.NoPadding | WinForms.TextFormatFlags.SingleLine;
        var timeSize = WinForms.TextRenderer.MeasureText(g, _timeText, _timeFont, Size.Empty, flags);
        WinForms.TextRenderer.DrawText(g, _timeText, _timeFont, new Rectangle((int)x, 0, timeSize.Width + 2, Height), Color.White, flags);
        x += timeSize.Width + 10 * _scale;
        WinForms.TextRenderer.DrawText(g, _hint, _hintFont, new Rectangle((int)x, 0, Width - (int)x, Height), Color.FromArgb(0xB4, 0xB4, 0xB8), flags);
    }

    static Color Blend(Color a, Color b, double amountA) => Color.FromArgb(
        (int)(a.R * amountA + b.R * (1 - amountA)),
        (int)(a.G * amountA + b.G * (1 - amountA)),
        (int)(a.B * amountA + b.B * (1 - amountA)));

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int dd = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, dd, dd, 180, 90);
        p.AddArc(r.Right - dd, r.Top, dd, dd, 270, 90);
        p.AddArc(r.Right - dd, r.Bottom - dd, dd, dd, 0, 90);
        p.AddArc(r.Left, r.Bottom - dd, dd, dd, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _timeFont?.Dispose();
            _hintFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
