using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace LayoutFixer;

/// The suggestion bubble next to the text cursor. It never takes focus and clicks pass through it.
/// A frosted-glass (acrylic) card that follows the Windows light/dark theme and uses your Windows
/// accent colour for the main key. Drawn by hand so it can show "typed -> fixed", key caps and a
/// direction badge, and scales with DPI.
sealed class Popup : Form
{
    /// Colours for one theme. `Tint` is the glass tint as 0xAABBGGRR; everything else is drawn on top of the blur.
    sealed record Theme(bool IsDark, uint Tint, Color Text, Color Dim, Color KeyBack, Color KeyBorder,
        Color BadgeBack, Color BadgeText, Color EdgeLight, Color EdgeShade, Color Gloss);

    // Neutral greys only. Most colours are translucent so the blurred desktop shows through them.
    static readonly Theme Dark = new(true, 0xCC_1C_1C_1C,
        Color.FromArgb(0xFA, 0xFA, 0xFC), Color.FromArgb(0xC4, 0xC4, 0xCA),
        Color.FromArgb(40, 255, 255, 255), Color.FromArgb(90, 255, 255, 255),
        Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0xE6, 0xE6, 0xEA),
        Color.FromArgb(150, 255, 255, 255), Color.FromArgb(26, 255, 255, 255), Color.FromArgb(34, 255, 255, 255));
    static readonly Theme Light = new(false, 0xC0_F5_F5_F5,
        Color.FromArgb(0x1B, 0x1B, 0x1F), Color.FromArgb(0x55, 0x55, 0x5D),
        Color.FromArgb(215, 255, 255, 255), Color.FromArgb(50, 0, 0, 0),
        Color.FromArgb(18, 0, 0, 0), Color.FromArgb(0x3C, 0x3C, 0x44),
        Color.FromArgb(235, 255, 255, 255), Color.FromArgb(56, 0, 0, 0), Color.FromArgb(120, 255, 255, 255));

    const int MaxOriginal = 220, MaxFixed = 340;

    /// Play a pop when the popup appears (the tray menu's "Pop sound" switch).
    public bool PopSound = true;

    Theme theme = Dark;
    Color accent = Color.FromArgb(0x00, 0x78, 0xD4);
    string original = "", fixedText = "", badge = "";
    int generation;
    bool dwmRounded;
    readonly Font fixedFont = new("Segoe UI Semibold", 13f), originalFont = new("Segoe UI", 10.5f),
        hintFont = new("Segoe UI", 9f), keyFont = new("Segoe UI Semibold", 8.5f), badgeFont = new("Segoe UI Semibold", 8f);

    public Popup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        // Pure black is see-through on a glass window, so the blur shows wherever we don't draw.
        BackColor = Color.Black;
        // Create the window now (hidden): BeginInvoke only works once it exists, and the first
        // suggestion arrives from a background thread before the popup has ever been shown.
        _ = Handle;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // No activation, no taskbar button, always on top, click-through.
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008 | 0x00000020;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounds the corners itself; older Windows gets a rounded region in ApplyShape.
        int round = 2;
        dwmRounded = Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) == 0;
        // Windows 11 would add its own thin border: turn it off, we draw a glass edge ourselves.
        int none = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(Handle, 34, ref none, sizeof(int));
        // Make the whole window "glass": pure black pixels we paint then show the frosted blur instead.
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
    }

    /// Light or dark, like the Windows apps setting. LAYOUTFIXER_THEME=light|dark forces one (for testing).
    static bool SystemUsesLightTheme()
    {
        switch (Environment.GetEnvironmentVariable("LAYOUTFIXER_THEME")?.ToLowerInvariant())
        {
            case "light": return true;
            case "dark": return false;
        }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// The accent colour picked in Windows settings (falls back to Windows blue).
    static Color SystemAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
        }
        catch { }
        return Color.FromArgb(0x00, 0x78, 0xD4);
    }

    static Color ReadableOn(Color bg) => (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) > 150 ? Color.FromArgb(0x14, 0x14, 0x18) : Color.White;

    static bool IsRtl(string text) => text.Any(c => c >= 0x0600 && c <= 0x06FF);

    static StringFormat Format(string text, StringAlignment align = StringAlignment.Near)
    {
        var f = new StringFormat(StringFormat.GenericDefault)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = align,
        };
        f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        if (IsRtl(text)) f.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        return f;
    }

    float DpiScale => DeviceDpi / 96f;
    float Px(float v) => v * DpiScale;

    SizeF Measure(Graphics g, string text, Font font, float maxWidth)
    {
        using var f = Format(text);
        var size = g.MeasureString(text, font, new SizeF(maxWidth, 1000), f);
        return new SizeF(Math.Min(size.Width, maxWidth), size.Height);
    }

    sealed record Parts(Size Size, RectangleF Orig, RectangleF Arrow, RectangleF Fix, RectangleF TabKey,
        RectangleF TabLabel, RectangleF EscKey, RectangleF EscLabel, RectangleF Badge);

    /// Lays out the whole popup. Rectangles are in client pixels.
    Parts Arrange(Graphics g)
    {
        float padX = Px(16), padY = Px(12), gap = Px(8);
        var o = Measure(g, original, originalFont, Px(MaxOriginal));
        var arrow = Measure(g, "→", originalFont, Px(40));
        var fx = Measure(g, fixedText, fixedFont, Px(MaxFixed));
        bool hasOriginal = original.Length > 0;
        float row1H = Math.Max(fx.Height, hasOriginal ? o.Height : 0);
        float row1W = (hasOriginal ? o.Width + gap + arrow.Width + gap : 0) + fx.Width;

        var tab = Measure(g, "Tab", keyFont, Px(60));
        var esc = Measure(g, "Esc", keyFont, Px(60));
        var fixLabel = Measure(g, "Fix", hintFont, Px(80));
        var ignoreLabel = Measure(g, "Ignore", hintFont, Px(80));
        var bd = Measure(g, badge, badgeFont, Px(120));
        float keyPadX = Px(8), keyH = Math.Max(tab.Height, esc.Height) + Px(5), badgeW = bd.Width + Px(16);
        float row2W = tab.Width + keyPadX * 2 + Px(6) + fixLabel.Width + Px(16)
            + esc.Width + keyPadX * 2 + Px(6) + ignoreLabel.Width + Px(22) + badgeW;
        float row2H = Math.Max(keyH, bd.Height + Px(5));

        float width = Math.Max(row1W, row2W);
        float y1 = padY, y2 = padY + row1H + Px(8), x = padX;

        var origR = new RectangleF(x, y1, o.Width, row1H);
        RectangleF arrowR = default, fixR;
        if (hasOriginal)
        {
            arrowR = new RectangleF(x + o.Width + gap, y1, arrow.Width, row1H);
            fixR = new RectangleF(arrowR.Right + gap, y1, fx.Width, row1H);
        }
        else fixR = new RectangleF(x, y1, fx.Width, row1H);

        var tabKey = new RectangleF(x, y2, tab.Width + keyPadX * 2, row2H);
        var tabLabel = new RectangleF(tabKey.Right + Px(6), y2, fixLabel.Width, row2H);
        var escKey = new RectangleF(tabLabel.Right + Px(16), y2, esc.Width + keyPadX * 2, row2H);
        var escLabel = new RectangleF(escKey.Right + Px(6), y2, ignoreLabel.Width, row2H);
        var badgeR = new RectangleF(padX + width - badgeW, y2, badgeW, row2H);

        var size = new Size((int)Math.Ceiling(width + padX * 2), (int)Math.Ceiling(y2 + row2H + padY));
        return new Parts(size, origR, arrowR, fixR, tabKey, tabLabel, escKey, escLabel, badgeR);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void DrawText(Graphics g, string text, Font font, Color color, RectangleF r, StringAlignment align = StringAlignment.Near)
    {
        using var f = Format(text, align);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, r, f);
    }

    // Never fill the background: transparent pixels are what lets the frosted glass show through.
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        // Draw into an image that has an alpha channel, then copy it over as-is (alpha included).
        using var image = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(image)) Render(g);
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        e.Graphics.DrawImageUnscaled(image, 0, 0);
    }

    void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        var l = Arrange(g);

        if (original.Length > 0)
        {
            DrawText(g, original, originalFont, theme.Dim, l.Orig);
            // Strike through what is being replaced.
            using var pen = new Pen(Color.FromArgb(0xB0, theme.Dim), Math.Max(1f, DpiScale));
            float midY = l.Orig.Top + l.Orig.Height / 2;
            g.DrawLine(pen, l.Orig.Left, midY, l.Orig.Right, midY);
            DrawText(g, "→", originalFont, theme.Dim, l.Arrow, StringAlignment.Center);
        }
        DrawText(g, fixedText, fixedFont, theme.Text, l.Fix);

        // Tab is the main action, so it is the high-contrast key (light on dark, dark on light); Esc stays quiet.
        DrawKey(g, l.TabKey, "Tab", theme.Text, theme.Text, theme.IsDark ? Color.FromArgb(0x16, 0x16, 0x1A) : Color.White);
        DrawText(g, "Fix", hintFont, theme.Dim, l.TabLabel);
        DrawKey(g, l.EscKey, "Esc", theme.KeyBack, theme.KeyBorder, theme.Text);
        DrawText(g, "Ignore", hintFont, theme.Dim, l.EscLabel);

        using (var path = Rounded(l.Badge, l.Badge.Height / 2))
        using (var fill = new SolidBrush(theme.BadgeBack)) g.FillPath(fill, path);
        DrawText(g, badge, badgeFont, theme.BadgeText, l.Badge, StringAlignment.Center);

        // Glass: a soft highlight over the top half, and an edge that is bright where light hits the
        // top-left and fades to a faint shade at the bottom-right.
        var edge = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Rounded(edge, CornerRadius))
        {
            var state = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using (var gloss = new LinearGradientBrush(new RectangleF(0, 0, Width, Height * 0.65f), theme.Gloss, Color.FromArgb(0, theme.Gloss), 90f))
                g.FillRectangle(gloss, 0, 0, Width, Height * 0.65f);
            g.Restore(state);
            using var brush = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), theme.EdgeLight, theme.EdgeShade, 55f);
            using var pen = new Pen(brush, 1f);
            g.DrawPath(pen, path);
        }
    }

    /// Windows 11 rounds the window itself; Windows 10 can't round frosted glass, so it stays square like its own flyouts.
    float CornerRadius => dwmRounded ? Px(8) : 0;

    void DrawKey(Graphics g, RectangleF r, string text, Color fill, Color border, Color textColor)
    {
        using var path = Rounded(r, Px(5));
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
        DrawText(g, text, keyFont, textColor, r, StringAlignment.Center);
    }

    /// Frosted glass behind the card, tinted for the current theme.
    void ApplyShape()
    {
        Native.SetAcrylic(Handle, theme.Tint);
        int dark = theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // immersive dark mode
    }

    /// `from` and `to` are language codes ("fa" / "en") shown in the badge.
    public void ShowSuggestion(string typed, string suggestion, string from, string to)
    {
        if (!Visible)
        {
            theme = SystemUsesLightTheme() ? Light : Dark;
            accent = SystemAccent();
        }
        original = typed;
        fixedText = suggestion;
        badge = $"{from.ToUpperInvariant()} → {to.ToUpperInvariant()}";
        using (var g = CreateGraphics()) Size = Arrange(g).Size;
        ApplyShape();
        Invalidate();
        // Finding the caret can be slow in some apps, so do it off the UI thread.
        int gen = ++generation;
        Task.Run(Caret.Find).ContinueWith(t =>
        {
            if (gen != generation || IsDisposed) return;
            BeginInvoke(() => Place(t.Result));
        });
    }

    void Place(Rectangle? caret)
    {
        // Without a caret, sit just below the mouse pointer so the popup doesn't cover what you're pointing at.
        var anchor = caret ?? new Rectangle(Cursor.Position.X, Cursor.Position.Y, 0, (int)Px(18));
        var screen = Screen.FromPoint(anchor.Location).WorkingArea;
        int x = Math.Clamp(anchor.Left, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - Width - 4));
        int y = anchor.Bottom + (int)Px(6);
        if (y + Height > screen.Bottom) y = anchor.Top - Height - (int)Px(6);
        Location = new Point(x, y);
        // A pop only when the popup newly appears, not on every extra word that extends the suggestion.
        bool appearing = !Visible;
        if (appearing) Show();
        if (appearing && PopSound) Sounds.Pop();
    }

    public void HideIt()
    {
        generation++;
        if (Visible) Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fixedFont.Dispose(); originalFont.Dispose(); hintFont.Dispose(); keyFont.Dispose(); badgeFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// Where the text cursor is in the app you're typing in.
static class Caret
{
    public static Rectangle? Find()
    {
        // 1. The classic Win32 caret (Notepad, Office, most native apps).
        uint thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var info = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (Native.GetGUIThreadInfo(thread, ref info) && info.hwndCaret != IntPtr.Zero)
        {
            var p = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            if (Native.ClientToScreen(info.hwndCaret, ref p))
                return new Rectangle(p.X, p.Y, Math.Max(1, info.rcCaret.Right - info.rcCaret.Left), Math.Max(1, info.rcCaret.Bottom - info.rcCaret.Top));
        }
        // 2. UI Automation (browsers, modern apps).
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element?.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) == true)
            {
                var selection = ((TextPattern)pattern).GetSelection();
                if (selection.Length > 0)
                {
                    var range = selection[0];
                    var rects = range.GetBoundingRectangles();
                    if (rects.Length == 0)
                    {
                        var wider = range.Clone();
                        wider.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
                        rects = wider.GetBoundingRectangles();
                    }
                    if (rects.Length > 0)
                    {
                        var r = rects[^1];
                        return new Rectangle((int)r.Right, (int)r.Top, 1, Math.Max(1, (int)r.Height));
                    }
                }
            }
        }
        catch { /* app doesn't support it */ }
        // 3. Fall back to the mouse pointer.
        return null;
    }
}
