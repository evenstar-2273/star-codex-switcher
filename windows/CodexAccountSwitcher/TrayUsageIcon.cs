using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CodexAccountSwitcher;

internal static class TrayUsageIcon
{
    public static Icon Create(int remainingPercent, bool lightTaskbar, Size size)
    {
        var label = Math.Clamp(remainingPercent, 0, 100).ToString();
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap)) {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var color = lightTaskbar ? Color.Black : Color.White;
            using var brush = new SolidBrush(color);
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags |= StringFormatFlags.NoWrap;
            for (var pixels = size.Height * 0.88f; pixels >= 5; pixels -= 0.5f) {
                using var font = new Font("Segoe UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = graphics.MeasureString(label, font, PointF.Empty, format);
                if (measured.Width > size.Width - 1 || measured.Height > size.Height) continue;
                graphics.DrawString(label, font, brush,
                    new RectangleF(0, 0, size.Width, size.Height), format);
                break;
            }
        }
        var handle = bitmap.GetHicon();
        try {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        } finally {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
