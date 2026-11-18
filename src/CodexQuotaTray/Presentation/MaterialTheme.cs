using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace CodexQuotaTray.Presentation;

/// <summary>Material 3 语义配色；保留原生控件的键盘和辅助功能行为。</summary>
internal sealed record MaterialTheme(Color Surface, Color Container, Color OnSurface, Color OnSurfaceVariant,
    Color Primary, Color OnPrimary, Color Outline, Color Error)
{
    public static MaterialTheme Current()
    {
        if (SystemInformation.HighContrast)
            return new(SystemColors.Window, SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText,
                SystemColors.Highlight, SystemColors.HighlightText, SystemColors.WindowText, SystemColors.WindowText);
        var light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return light
            ? new(C("FDF8FD"), C("F2ECF4"), C("1D1B20"), C("49454F"), C("6750A4"), Color.White, C("79747E"), C("B3261E"))
            : new(C("141218"), C("211F26"), C("E6E0E9"), C("CAC4D0"), C("D0BCFF"), C("381E72"), C("938F99"), C("FFB4AB"));
    }
    private static Color C(string hex) => ColorTranslator.FromHtml("#" + hex);
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class TonalPanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color SurfaceColor { get; set; }
    public TonalPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = MaterialTheme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 16 * DeviceDpi / 96f);
        using var brush = new SolidBrush(SurfaceColor);
        e.Graphics.FillPath(brush, path);
        base.OnPaint(e);
    }
}

/// <summary>保留 Button 的角色和键盘行为，补充圆角及可见交互状态。</summary>
internal sealed class MaterialButton(MaterialTheme theme) : Button
{
    private bool hovered;
    private bool pressed;
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? theme.Surface);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(2, 2, Width - 5, Height - 5);
        using var path = MaterialTheme.Rounded(bounds, bounds.Height / 2f);
        using var brush = new SolidBrush(Enabled ? theme.Primary : theme.Container);
        e.Graphics.FillPath(brush, path);
        if (Enabled && (hovered || pressed))
        {
            using var state = new SolidBrush(Color.FromArgb(pressed ? 32 : 20, theme.OnPrimary));
            e.Graphics.FillPath(state, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? theme.OnPrimary : theme.OnSurfaceVariant,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -8, -8), theme.OnPrimary, theme.Primary);
    }
}
