using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexQuotaTray.Presentation;

public static class RingRenderer
{
    public static Bitmap Draw(double? remaining, double? timeRemaining, bool stale, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        var width = size * .13f;
        var margin = size * .12f;
        var bounds = new RectangleF(margin, margin, size - 2 * margin, size - 2 * margin);

        // 保留旧版的尺寸、颜色和 26° 间隙：左额度、右时间，均从顶端向底端延伸。
        using var outline = Pen(Color.FromArgb(235, 20, 25, 34), width + size * .065f);
        using var rail = Pen(Color.FromArgb(150, 161, 178), width);
        graphics.DrawArc(outline, bounds, 257, -154);
        graphics.DrawArc(outline, bounds, 283, 154);
        graphics.DrawArc(rail, bounds, 257, -154);
        graphics.DrawArc(rail, bounds, 283, 154);
        var color = stale ? Color.FromArgb(150, 160, 178) : remaining switch
        {
            <= 10 => Color.FromArgb(239, 92, 98),
            <= 25 => Color.FromArgb(240, 177, 65),
            _ => Color.FromArgb(57, 209, 164)
        };
        DrawArc(remaining, color, 257, -1);
        DrawArc(timeRemaining, stale ? Color.FromArgb(150, 160, 178) : Color.FromArgb(89, 178, 255), 283, 1);
        return bitmap;

        void DrawArc(double? percent, Color arcColor, float start, int direction)
        {
            if (percent is not { } value || !double.IsFinite(value) || value <= 0) return;
            using var pen = Pen(arcColor, width);
            graphics.DrawArc(pen, bounds, start, (float)Math.Clamp(value, 0, 100) * 1.54f * direction);
        }
    }

    public static Icon CreateIcon(double? remaining, double? timeRemaining, bool stale)
    {
        using var bitmap = Draw(remaining, timeRemaining, stale, 32);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private static Pen Pen(Color color, float width) => new(color, width)
    { StartCap = LineCap.Round, EndCap = LineCap.Round };

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
