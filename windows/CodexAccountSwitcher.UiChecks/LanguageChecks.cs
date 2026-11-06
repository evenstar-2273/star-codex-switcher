using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;

internal static class LanguageChecks
{
    private const string IdentityError = "Codex did not return an account identity.";

    internal static void Run(NativeSettings native, string output)
    {
        var original = CultureInfo.CurrentUICulture;
        try {
            CheckCase("zh-CN", "system", true, "system-language-zh.png");
            CheckCase("zh-CN", "english", false, null);
            CheckCase("en-US", "simplifiedChinese", true, null);
            CheckCase("en-US", "system", false, null);
            CheckCase("zh-CN", "invalid-preference", true, null);
            Console.WriteLine("PASS: Windows UI language wins over inherited English host strings; explicit language choices, localized identity errors and unchanged preferences verified.");
        } finally { CultureInfo.CurrentUICulture = original; }

        void CheckCase(string culture, string language, bool chinese, string? screenshot)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var client = new LanguageClient(language);
            var window = new MainWindow(client, native) {
                Width = 1000, Height = 700, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
            };
            try {
                window.Show(); window.UpdateLayout();
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                var texts = Descendants<TextBlock>(window).Select(item => item.Text).ToArray();
                var buttons = Descendants<Button>(window).Select(AutomationProperties.GetName).ToArray();
                Require(texts.Contains(chinese ? "账号" : "Accounts"), "The account heading follows the Windows language preference.");
                Require(buttons.Contains(chinese ? "设置" : "Settings"), "The settings action follows the Windows language preference.");
                Require(buttons.Contains(chinese ? "添加账号" : "Add account"), "The add-account action uses native presentation copy.");
                if (chinese) {
                    Require(!texts.Any(text => text is "Accounts" or "Settings" or "Add Account" or "No saved accounts" or IdentityError), "English host strings cannot leak into the Chinese empty-state/error screen.");
                    Require(texts.Any(text => text.Contains("Codex") && text.Contains("身份")), "The known identity error keeps its meaning in Chinese.");
                } else Require(texts.Contains(IdentityError), "An explicit English choice preserves the English diagnostic.");
                Require(client.State.Settings.Language == language && client.Commands == 0, "Presentation must not rewrite the user's language setting or operate on accounts.");
                if (screenshot != null) {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, screenshot)); png.Save(file);
                }
                client.State = client.State with { Error = "Unmapped diagnostic: fixture-only" };
                window.Navigate("accounts");
                window.UpdateLayout();
                Require(Descendants<TextBlock>(window).Any(text => text.Text == "Unmapped diagnostic: fixture-only"), "Unrecognized diagnostics remain available instead of being hidden by a generic translation.");
            } finally { window.CloseForExit(); }
        }
    }

    private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class LanguageClient(string language) : IAccountClient
    {
        public int Commands { get; private set; }
        public AccountSnapshot State { get; set; } = new([], null, new(language), false, false, false, IdentityError, new() {
            ["accounts"] = "Accounts", ["settings"] = "Settings", ["add_account"] = "Add Account",
            ["no_accounts"] = "No saved accounts", ["active_unconfirmed"] = "Active account could not be confirmed",
            ["register_current_account"] = "Register Current Account", ["quit"] = "Quit"
        });
        public event Action? Changed { add { } remove { } }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null)
        { Commands++; return Task.CompletedTask; }
    }
}
