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
    private const string StorageError = "Windows could not access private account storage (error 4393).";
    private const string StorageLinkError = "Account storage cannot use symbolic links or junctions.";

    internal static void Run(NativeSettings native, string output)
    {
        var original = CultureInfo.CurrentUICulture;
        try {
            CheckStorageMessages();
            CheckCase("zh-CN", "system", true, "system-language-zh.png");
            CheckCase("zh-CN", "english", false, null);
            CheckCase("en-US", "simplifiedChinese", true, null);
            CheckCase("en-US", "system", false, null);
            CheckCase("zh-CN", "invalid-preference", true, null);
            CheckCase("zh-CN", "system", true, "storage-error-zh.png", StorageError);
            CheckCase("en-US", "simplifiedChinese", true, null, StorageError);
            CheckCase("zh-CN", "english", false, null, StorageError);
            CheckImportCase("zh-CN", "system", true, StorageLinkError, "import-storage-link-error-zh.png");
            CheckImportCase("en-US", "simplifiedChinese", true, StorageError, "import-storage-4393-error-zh.png");
            CheckImportCase("en-US", "simplifiedChinese", true, "The account storage directory could not be resolved (Windows error 1921).", "import-storage-target-error-zh.png");
            CheckImportCase("zh-CN", "english", false, StorageLinkError, null);
            Console.WriteLine("PASS: Windows UI language wins over inherited English host strings; explicit language choices, localized identity/storage/import errors with original codes, failed import recovery and unchanged preferences verified.");
        } finally { CultureInfo.CurrentUICulture = original; }

        void CheckCase(string culture, string language, bool chinese, string? screenshot, string error = IdentityError)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var client = new LanguageClient(language);
            client.State = client.State with { Error = error };
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
                    Require(!texts.Any(text => text is "Accounts" or "Settings" or "Add Account" or "No saved accounts" || text == error), "English host strings cannot leak into the Chinese empty-state/error screen.");
                    if (error == StorageError) {
                        Require(texts.Any(text => text.Contains("账号存储") && text.Contains("4393") && text.Contains("链接或重定向")), "The storage failure is visible in Chinese with its code and relevant troubleshooting hint.");
                        Require(!texts.Any(text => text.Contains("数据损坏") || text.Contains("账号丢失")), "A blocked storage path must not be presented as confirmed lost or corrupt account data.");
                    } else Require(texts.Any(text => text.Contains("Codex") && text.Contains("身份")), "The known identity error keeps its meaning in Chinese.");
                } else Require(texts.Contains(error), "An explicit English choice preserves the English diagnostic.");
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
                client.State = client.State with { Error = null };
                window.Navigate("accounts"); window.UpdateLayout();
                var normalTexts = Descendants<TextBlock>(window).Select(text => text.Text).ToArray();
                Require(normalTexts.Contains(chinese ? "还没有账号，可先导入或添加账号。" : "No accounts yet. Import or add an account."),
                    "A normal empty registry still shows the ordinary add/import empty state.");
                Require(!normalTexts.Any(text => text.Contains("4393") || text.Contains("账号存储") || text.Contains("private account storage"))
                    && !Descendants<Button>(window).Any(button => AutomationProperties.GetName(button) == (chinese ? "关闭错误提示" : "Dismiss error")),
                    "An empty account list without an error must not show a storage warning or stale error banner.");
            } finally { window.CloseForExit(); }
        }

        void CheckImportCase(string culture, string language, bool chinese, string error, string? screenshot)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var client = new LanguageClient(language) { ImportError = error };
            var window = new JsonImportWindow(client) { ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            try {
                window.Show(); window.UpdateLayout();
                var editor = Descendants<TextBox>(window).Single();
                const string pasted = "{\"fixture\":\"synthetic account only\"}";
                editor.Text = pasted;
                var submit = Descendants<Button>(window).Single(button => AutomationProperties.GetName(button) == "导入粘贴的 JSON");
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                var status = Descendants<TextBlock>(window).Single(text => AutomationProperties.GetName(text) == "导入状态");
                if (chinese) {
                    Require(status.Text.Contains("账号存储") && !status.Text.Contains("Account storage") && !status.Text.Contains("private account storage"),
                        "Known storage failures use Chinese inside the import dialog as well as the main error banner.");
                    Require(error != StorageError || status.Text.Contains("4393"), "Import storage error codes remain visible.");
                } else Require(status.Text == error, "The import diagnostic follows an explicit English preference.");
                Require(window.IsVisible && editor.Text == pasted && !editor.IsReadOnly && submit.IsEnabled,
                    "A failed import stays open, retains pasted credentials and restores editing and retry controls.");
                Require(Descendants<Button>(window).All(button => button.IsEnabled), "Storage failures restore file import, clear and cancel controls.");
                Require(client.ImportAttempts == 1 && client.Commands == 0 && client.State.Settings.Language == language,
                    "Error presentation neither retries import nor switches accounts or rewrites the language preference.");
                if (screenshot != null) {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, screenshot)); png.Save(file);
                }
            } finally { window.Close(); }
        }
    }

    private static void CheckStorageMessages()
    {
        Require(WindowsLocalization.Error(StorageError, "english") == StorageError, "English preference retains the original storage diagnostic.");
        Require(WindowsLocalization.Error("Windows could not access private account storage (error 5).", "simplifiedChinese")?.Contains("访问权限") == true,
            "Access-denied storage failures explain the Windows user's permissions.");
        Require(WindowsLocalization.Error("Windows could not access private account storage (error 32).", "simplifiedChinese")?.Contains("其他程序占用") == true,
            "Sharing violations explain that another process is using the account file.");
        Require(WindowsLocalization.Error("Windows could not access private account storage (error 4294967295).", "simplifiedChinese")?.Contains("4294967295") == true,
            "Unrecognized Win32 codes remain available for diagnosis without guessing a cause.");
        foreach (var message in new[] {
            "Windows could not access private account storage (error unknown).",
            "Windows could not access private account storage (error 4393). Additional diagnostic.",
            "Unmapped diagnostic: fixture-only"
        }) Require(WindowsLocalization.Error(message, "simplifiedChinese") == message, "Only the complete known storage error format is translated; other diagnostics are retained.");
        Require(WindowsLocalization.Error("Account storage cannot use symbolic links or junctions.", "simplifiedChinese")?.Contains("链接或联接") == true,
            "The managed storage link rejection follows the same Chinese presentation.");
        Require(WindowsLocalization.Error("Windows user identity is unavailable.", "simplifiedChinese")?.Contains("Windows 用户身份") == true,
            "Unavailable Windows identity is translated without inventing an account login failure.");
        Require(WindowsLocalization.Error("Cleanup path is outside account storage.", "simplifiedChinese")?.Contains("已停止清理") == true,
            "A rejected cleanup path states that cleanup was stopped.");
        foreach (var code in new[] { 2, 3, 5, 1921 }) {
            var message = $"The account storage directory could not be resolved (Windows error {code}).";
            var localized = WindowsLocalization.Error(message, "simplifiedChinese")!;
            Require(localized.Contains("实际位置") && localized.Contains(code.ToString(CultureInfo.InvariantCulture)),
                "Directory resolution failures retain their Windows code in the Chinese diagnostic.");
            Require(WindowsLocalization.Error(message, "english") == message,
                "Directory resolution failures respect an explicit English preference.");
        }
        foreach (var message in new[] {
            "The account storage directory is empty.",
            "The account storage root must be a directory.",
            "Choose a dedicated account storage folder, not a drive or Windows user folder."
        }) Require(WindowsLocalization.Error(message, "simplifiedChinese")?.Contains("账号数据") == true,
            "Invalid storage root choices display a Chinese diagnostic.");
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

    private sealed class LanguageClient(string language) : IAccountClient, IAccountImporter
    {
        public int Commands { get; private set; }
        public string? ImportError { get; init; }
        public int ImportAttempts { get; private set; }
        public AccountSnapshot State { get; set; } = new([], null, new(language), false, false, false, IdentityError, new() {
            ["accounts"] = "Accounts", ["settings"] = "Settings", ["add_account"] = "Add Account",
            ["no_accounts"] = "No saved accounts", ["active_unconfirmed"] = "Active account could not be confirmed",
            ["register_current_account"] = "Register Current Account", ["quit"] = "Quit"
        });
        public event Action? Changed { add { } remove { } }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null)
        { Commands++; return Task.CompletedTask; }
        public Task<ImportResult> ImportTextAsync(string json)
        { ImportAttempts++; return Task.FromException<ImportResult>(new IOException(ImportError ?? StorageLinkError)); }
        public Task<ImportResult> ImportFilesAsync(string[] paths) => ImportTextAsync("");
    }
}
