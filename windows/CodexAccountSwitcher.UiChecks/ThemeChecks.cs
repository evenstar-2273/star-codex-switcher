using System;
using System.Linq;
using System.Windows.Media;
using Microsoft.Win32;
using CodexAccountSwitcher;

internal static class ThemeChecks
{
    public static void Run(App app)
    {
        var key = @"Software\CodexAccountSwitcher.UiChecks\Theme-" + Guid.NewGuid().ToString("N");
        try {
            var settings = new NativeSettings(key);
            Require(settings.ThemePreference == "system", "Absent theme preference defaults to the system.");
            Require(settings.AccentPreference == "purple", "Absent accent preference defaults to twilight purple.");
            settings.ThemePreference = "dark";
            Require(new NativeSettings(key).ThemePreference == "dark", "Theme preference persists for the current user.");
            app.UseThemeSettings(settings); app.ApplyThemePreference(false);
            Require(app.UsesDarkPalette, "Dark selection overrides a light system theme.");
            settings.ThemePreference = "light";
            Require(!app.UsesDarkPalette, "Changing theme applies immediately.");
            app.ApplyThemePreference(true);
            Require(!app.UsesDarkPalette, "Light selection overrides a dark system theme.");
            using (var registry = Registry.CurrentUser.CreateSubKey(key)) registry.SetValue("ThemePreference", "unrecognized");
            Require(settings.ThemePreference == "system", "Invalid persisted values fall back to the system.");
            app.ApplyThemePreference(true); Require(app.UsesDarkPalette, "System mode follows a dark preference change.");
            app.ApplyThemePreference(false); Require(!app.UsesDarkPalette, "System mode follows a light preference change.");
            settings.ThemePreference = "invalid";
            using (var registry = Registry.CurrentUser.OpenSubKey(key)) Require((string?)registry?.GetValue("ThemePreference") == "system", "Invalid assignments persist a safe normalized value.");
            foreach (var dark in new[] { false, true }) {
                settings.ThemePreference = dark ? "dark" : "light";
                var semantics = new[] { "Success", "Warning", "WarningSurface", "Danger" }.ToDictionary(name => name, name => ((SolidColorBrush)app.Resources[name]).Color);
                foreach (var accent in new[] { "blue", "purple", "teal", "orange" }) {
                    settings.AccentPreference = accent;
                    Require(app.AccentPreference == accent && app.UsesDarkPalette == dark, "Accent changes apply immediately without changing light/dark mode.");
                    var reopened = new NativeSettings(key);
                    Require(reopened.AccentPreference == accent && reopened.ThemePreference == settings.ThemePreference, "Accent and brightness preferences persist independently for a new settings instance.");
                    var primary = (LinearGradientBrush)app.Resources["Accent"];
                    var selected = (LinearGradientBrush)app.Resources["Selected"];
                    Require(primary.GradientStops[0].Color != primary.GradientStops[1].Color, "Each accent has a visible two-color gradient.");
                    var onAccent = ((SolidColorBrush)app.Resources["OnAccent"]).Color;
                    var ink = ((SolidColorBrush)app.Resources["Ink"]).Color;
                    var accentInk = ((SolidColorBrush)app.Resources["AccentInk"]).Color;
                    foreach (var color in GradientSamples(primary)) Require(Contrast(onAccent, color) >= 4.5, $"{accent} button text passes 4.5:1 across the gradient.");
                    foreach (var color in GradientSamples(selected)) {
                        Require(Contrast(ink, color) >= 4.5, $"{accent} selected-card text is legible in both palettes.");
                        Require(Contrast(accentInk, color) >= 4.5, $"{accent} current-account label is legible on its selected surface.");
                    }
                    foreach (var surfaceKey in new[] { "ListSurface", "Surface", "ButtonSurface", "SectionHeader", "InputSurface", "Hover" })
                        Require(Contrast(accentInk, ((SolidColorBrush)app.Resources[surfaceKey]).Color) >= 4.5, $"{accent} accent text passes 4.5:1 on {surfaceKey}.");
                    foreach (var pair in semantics) Require(((SolidColorBrush)app.Resources[pair.Key]).Color == pair.Value, "Accent choice cannot recolor quota or warning meanings.");
                }
                foreach (var plan in new[] { "free", "plus", "pro100", "pro200", "pro500" }) {
                    var foreground = ((SolidColorBrush)app.Resources["PlanForeground_" + plan]).Color;
                    var gradient = (LinearGradientBrush)app.Resources["PlanBackground_" + plan];
                    Require(gradient.GradientStops.Count >= 3, "Plan badges use a restrained satin gradient.");
                    foreach (var stop in gradient.GradientStops)
                        Require(Contrast(foreground, stop.Color) >= 4.5, $"{plan} badge text remains readable at every gradient stop in {(dark ? "dark" : "light")} mode.");
                }
                var schedule = ((SolidColorBrush)app.Resources["Schedule"]).Color;
                var surface = ((SolidColorBrush)app.Resources["ListSurface"]).Color;
                Require(Contrast(schedule, surface) >= 4.5, "Quota recovery times remain legible on the card surface.");
            }
            using (var registry = Registry.CurrentUser.CreateSubKey(key)) registry.SetValue("AccentPreference", "not-a-color");
            Require(settings.AccentPreference == "purple", "Invalid stored accents fall back to twilight purple.");
            settings.AccentPreference = "invalid";
            using (var registry = Registry.CurrentUser.OpenSubKey(key)) Require((string?)registry?.GetValue("AccentPreference") == "purple", "Invalid accent assignments persist their normalized fallback.");
            settings.AccentPreference = "purple"; settings.ThemePreference = "system";
            app.ApplyThemePreference(false); Require(!app.UsesDarkPalette && app.AccentPreference == "purple", "System light mode retains the selected accent.");
            app.ApplyThemePreference(true); Require(app.UsesDarkPalette && app.AccentPreference == "purple", "System dark mode retains the selected accent.");
            Console.WriteLine("PASS: persisted/system themes, four persisted gradient accents, immediate application, semantic colors and sampled text contrast >= 4.5:1.");
        } finally {
            app.UseThemeSettings(new NativeSettings());
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
        }
    }
    private static System.Collections.Generic.IEnumerable<Color> GradientSamples(LinearGradientBrush brush)
    {
        var a = brush.GradientStops[0].Color; var b = brush.GradientStops[1].Color;
        for (var i = 0; i <= 100; i++) {
            var t = i / 100.0;
            yield return Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
        }
    }
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color) {
            static double Channel(byte component) { var value = component / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
