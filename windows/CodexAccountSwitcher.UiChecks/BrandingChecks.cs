using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using CodexAccountSwitcher;

internal static class BrandingChecks
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private const string SelectedPngSha256 = "D27D1CF2CEA1BD51E5C891C7D18532DE3BE68EA3C5A6C5E697D8875D8C171230";

    public static void Run()
    {
        var artwork = Resource("logo.png");
        Require(Convert.ToHexString(SHA256.HashData(artwork)) == SelectedPngSha256,
            "The packaged PNG must be the selected Star Codex Switcher S-arrow/star artwork.");
        var application = Resource("app.ico");
        foreach (var filename in new[] { "app.ico", "tray-light.ico", "tray-dark.ico" }) {
            var bytes = Resource(filename);
            Require(DirectorySizes(bytes).OrderBy(size => size).SequenceEqual(Sizes), "Windows icon frames must include every supported DPI size.");
            var decoded = new IconBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Require(decoded.Frames.Select(frame => frame.PixelWidth).OrderBy(size => size).SequenceEqual(Sizes),
                "WPF must decode every size, including the PNG-compressed 256px shell frame.");
            // Both taskbar themes deliberately use the same approved full-color logo.
            Require(bytes.SequenceEqual(application), "Application and tray surfaces use the same selected artwork.");
            // GDI's Icon selector may cap PNG-backed shell frames; tray sizes are BMP-backed.
            foreach (var size in Sizes.Where(size => size <= 128)) {
                using var stream = new MemoryStream(bytes);
                using var icon = new System.Drawing.Icon(stream, size, size);
                using var bitmap = icon.ToBitmap();
                Require(bitmap.Width == size && bitmap.Height == size, $"Windows must select the requested icon resolution: {filename}, requested {size}, got {bitmap.Width}x{bitmap.Height}.");
                Require(bitmap.GetPixel(0, 0).A == 0 && bitmap.GetPixel(size - 1, 0).A == 0 &&
                    bitmap.GetPixel(0, size - 1).A == 0 && bitmap.GetPixel(size - 1, size - 1).A == 0,
                    "The icon corners must remain genuinely transparent, without an external black or white square.");
                var visible = 0; var blue = 0; var white = 0; var top = size; var bottom = -1;
                for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A < 128) continue;
                    visible++; top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    if (pixel.B > pixel.R + 25 && pixel.B > pixel.G + 15) blue++;
                    if (pixel.R > 180 && pixel.G > 180 && pixel.B > 180) white++;
                }
                Require(visible >= size * size / 5 && visible < size * size * 9 / 10,
                    "The mark needs sufficient visible area while retaining transparent padding.");
                Require(bottom - top + 1 >= size * 0.6, "Small icon frames must contain a legible-sized mark.");
                Require(blue > size * size / 20 && white > size * size / 30,
                    "The selected navy tile and bright S arrows must remain present after downscaling.");
            }
        }
        Console.WriteLine("PASS: approved branding source, 9 DPI frames, transparent corners and visible full-color app/tray marks.");
    }
    private static byte[] Resource(string name)
    {
        var uri = new Uri("pack://application:,,,/Codex-Account-Switcher-windows-x64;component/" + name);
        using var stream = Application.GetResourceStream(uri).Stream;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    private static IEnumerable<int> DirectorySizes(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        Require(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1, "Icon resource must have a Windows ICO header.");
        var count = reader.ReadUInt16(); var sizes = new List<int>();
        for (var i = 0; i < count; i++) {
            var width = reader.ReadByte(); var height = reader.ReadByte();
            Require(width == height, "Icon frames must be square.");
            sizes.Add(width == 0 ? 256 : width); reader.ReadBytes(14);
        }
        return sizes;
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
