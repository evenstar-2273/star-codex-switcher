using System;
using System.Linq;
using System.Windows.Media;
using CodexAccountSwitcher;

internal static class PlanVisualChecks
{
    public static void Run()
    {
        foreach (var value in new[] { "prolite", "pro-lite", "PRO_100", "pro 5x", "codex-pro-5x", "chatgptprolite" })
            Require(AccountVisuals.PlanBadge(value).Label == "PRO 100", "Recognized lite aliases must use the 100 badge.");
        foreach (var value in new[] { "pro", "PRO200", "pro-20x", "codex-pro-20x", "chatgptpro" })
            Require(AccountVisuals.PlanBadge(value).Label == "PRO 200", "The official pro plan must use the 200 badge.");
        foreach (var value in new[] { "promax", "pro-max", "PRO 500", "chatgptpromax" })
            Require(AccountVisuals.PlanBadge(value).Label == "PRO 500", "Official max metadata must not inherit old filename-based 200 assumptions.");
        foreach (var value in new[] { null, "", "future-plan", "pro500@example.test", "my-plus-account", "notfree" })
            Require(AccountVisuals.PlanBadge(value).Label == "未知", "Unknown data must not look like FREE or a guessed paid tier.");
        Require(AccountVisuals.PlanBadge(null, chinese: false).Label == "Unknown", "Unknown label supports English.");
        foreach (var dark in new[] { false, true }) {
            var badges = new[] { "free", "plus", "prolite", "pro", "promax" }.Select(plan => AccountVisuals.PlanBadge(plan, dark)).ToArray();
            Require(badges.Select(b => b.Background).Distinct().Count() == 5, "Every requested tier needs a distinct badge color.");
            foreach (var badge in badges) {
                Require(Contrast(badge.Foreground, badge.Background) >= 4.5, $"{badge.Label} text must be readable on its {(dark ? "dark" : "light")} badge.");
                _ = ColorConverter.ConvertFromString(badge.Border);
            }
        }
        Console.WriteLine("PASS: five distinct plan badge palettes, aliases, unknown handling and text contrast.");
    }
    private static double Contrast(string foreground, string background)
    {
        double Luminance(string text) {
            var color = (Color)ColorConverter.ConvertFromString(text);
            double Channel(byte component) { var value = component / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var a = Luminance(foreground); var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
