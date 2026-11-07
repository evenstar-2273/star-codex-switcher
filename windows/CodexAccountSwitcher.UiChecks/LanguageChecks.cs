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
    private const string PermissionError = "The operation could not be completed. You don’t have permission.";
    private const string CancelledCleanup = "Sign-in was cancelled, but the incomplete account profile could not be removed: ";
    private const string SavedCleanup = "The account was saved, but the incomplete account profile could not be removed: ";
    private const string LegacyCancellation = "The operation could not be completed. (Swift.CancellationError error 1.)";
    private const string LegacyCleanup = " Removing the incomplete profile also failed: ";

    internal static void Run(NativeSettings native, string output)
    {
        var original = CultureInfo.CurrentUICulture;
        try {
            CheckStorageMessages();
            CheckCleanupMessages();
            CheckUpdateErrors();
            CheckUpdateHelperLanguage();
            CheckCase("zh-CN", "system", true, "system-language-zh.png");
            CheckCase("zh-CN", "english", false, null);
            CheckCase("en-US", "simplifiedChinese", true, null);
            CheckCase("en-US", "system", false, null);
            CheckCase("zh-CN", "invalid-preference", true, null);
            CheckCase("zh-CN", "system", true, "storage-error-zh.png", StorageError);
            CheckCase("en-US", "simplifiedChinese", true, null, StorageError);
            CheckCase("zh-CN", "english", false, null, StorageError);
            CheckCase("zh-CN", "system", true, "cancel-cleanup-permission-zh.png", LegacyCancellation + LegacyCleanup + PermissionError, "已取消添加账号");
            CheckCase("zh-CN", "system", true, "saved-cleanup-permission-zh.png", SavedCleanup + PermissionError, "账号已保存");
            CheckCase("zh-CN", "system", true, null, "This account is already saved. Select the existing account instead." + LegacyCleanup + PermissionError, "该账号已保存");
            CheckImportCase("zh-CN", "system", true, StorageLinkError, "import-storage-link-error-zh.png");
            CheckImportCase("en-US", "simplifiedChinese", true, StorageError, "import-storage-4393-error-zh.png");
            CheckImportCase("en-US", "simplifiedChinese", true, "The account storage directory could not be resolved (Windows error 1921).", "import-storage-target-error-zh.png");
            CheckImportCase("zh-CN", "english", false, StorageLinkError, null);
            Console.WriteLine("PASS: Windows UI language, account/storage/cancellation/cleanup messages, update-helper language handoff, known bilingual failures, private technical details, failed import recovery and unchanged preferences verified.");
        } finally { CultureInfo.CurrentUICulture = original; }

        void CheckCase(string culture, string language, bool chinese, string? screenshot, string error = IdentityError, string? expectedFragment = null)
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
                    if (expectedFragment != null) {
                        Require(texts.Any(text => text.Contains(expectedFragment) && text.Contains("文件是否被占用及目录权限")), "Cleanup failure keeps its original operation outcome and a Chinese file-access explanation.");
                        Require(!texts.Any(text => text.Contains("The operation") || text.Contains("Swift.CancellationError") || text.Contains("Removing the incomplete")), "Cancellation implementation strings never appear in the primary Chinese banner.");
                    } else if (error == StorageError) {
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
                Require(Descendants<TextBlock>(window).Any(text => text.Text == (chinese ? "操作未完成，请重试。可查看技术详情以进一步排查。" : "Unmapped diagnostic: fixture-only")
                    && text.ToolTip?.ToString()?.Contains("Unmapped diagnostic: fixture-only") == true), "Unknown errors have a localized primary explanation and retain technical details separately.");
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
        }) Require(WindowsLocalization.Error(message, "simplifiedChinese")?.Contains("操作未完成") == true
            && WindowsLocalization.TechnicalDetails(message, "simplifiedChinese")?.Contains(message) == true,
            "Unrecognized storage formats are not guessed; the primary explanation is Chinese and full diagnostics remain separate.");
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

    private static void CheckCleanupMessages()
    {
        foreach (var permission in new[] { PermissionError, "The operation could not be completed. You don't have permission." }) {
            foreach (var wrapper in new[] { CancelledCleanup, SavedCleanup, LegacyCancellation + LegacyCleanup }) {
                var text = WindowsLocalization.Error(wrapper + permission, "simplifiedChinese")!;
                Require(text.Contains("临时账号目录") && text.Contains("文件是否被占用及目录权限"), "Both Cocoa apostrophe forms explain cleanup access failures without assuming permission is the only cause.");
                Require(!text.Contains("The operation") && !text.Contains("CancellationError"), "Known cleanup diagnostics do not leak English implementation text into primary copy.");
            }
        }
        var saved = WindowsLocalization.Error(SavedCleanup + PermissionError, "simplifiedChinese")!;
        Require(saved.StartsWith("账号已保存", StringComparison.Ordinal) && !saved.Contains("登录失败"), "A saved account plus cleanup failure must not be described as a failed sign-in.");
        var duplicate = WindowsLocalization.Error("This account is already saved. Select the existing account instead." + LegacyCleanup + PermissionError, "simplifiedChinese")!;
        Require(duplicate.Contains("该账号已保存") && duplicate.Contains("文件是否被占用及目录权限") && !duplicate.Contains("已取消"), "Legacy duplicate errors retain both independent causes rather than being mistaken for cancellation.");
        foreach (var code in new[] { 5, 32, 33 }) {
            var text = WindowsLocalization.Error(CancelledCleanup + $"Windows could not access private account storage (error {code}).", "simplifiedChinese")!;
            Require(text.Contains(code.ToString(CultureInfo.InvariantCulture)) && text.Contains(code == 5 ? "访问权限" : "其他程序占用"), "Cleanup failures preserve Windows permission or sharing codes and their distinct reasons.");
        }
        Require(WindowsLocalization.Error(LegacyCancellation, "simplifiedChinese") == "已取消添加账号。", "Only the exact legacy Swift cancellation signature is recognized as cancellation.");
        foreach (var unknown in new[] { "Cannot cancel the network operation: fixture failure", "CancellationError: permission denied by fixture", "Permission service rejected a request" }) {
            var text = WindowsLocalization.Error(unknown, "simplifiedChinese")!;
            Require(text.Contains("操作未完成") && !text.Contains("已取消") && !text.Contains("登录凭据已失效"), "Arbitrary cancel or permission words are neither suppressed nor misclassified as authentication failure.");
            Require(WindowsLocalization.TechnicalDetails(unknown, "simplifiedChinese")!.Contains(unknown), "Unknown diagnostics remain available in a separate technical detail.");
            Require(WindowsLocalization.Error(unknown, "english") == unknown, "English preference retains an unfamiliar diagnostic.");
        }
        var unknownCleanup = CancelledCleanup + "Fixture cleanup error 999: no known cause";
        Require(WindowsLocalization.Error(unknownCleanup, "simplifiedChinese")!.Contains("未能清理")
            && !WindowsLocalization.Error(unknownCleanup, "simplifiedChinese")!.Contains("Fixture"), "An unknown cleanup error remains a visible Chinese failure without guessing its cause.");
        Require(WindowsLocalization.TechnicalDetails(unknownCleanup, "simplifiedChinese")!.Contains("999"), "Unknown cleanup codes remain available for diagnosis.");
        foreach (var known in new[] {
            "The Codex account process did not stop in time. Its files were left unchanged.",
            "Account core is unavailable. Restart the app.",
            "The runtime archive has no Swift host.",
            "The shared Swift runtime is missing. Build with scripts/package-windows.ps1.",
            "The refresh interval was not confirmed by the account service."
        }) Require(WindowsLocalization.Error(known, "simplifiedChinese") != known && !WindowsLocalization.Error(known, "simplifiedChinese")!.Contains("操作未完成，请重试。可查看"), "Known process, runtime and settings errors have specific Chinese explanations.");
        var mixed = "账号的登录凭据已失效（401）。请重新授权，或导入有效的完整账号凭据。 / Login rejected; reauthorize this account.\n已恢复原登录并重新打开 Codex。 / Previous login restored.";
        var localized = WindowsLocalization.Error(mixed, "simplifiedChinese")!;
        Require(localized.Contains("401") && localized.Contains("已恢复原登录") && !localized.Contains("Login") && !localized.Contains("Previous"), "Known bilingual account and recovery messages display only their selected language.");
        Require(WindowsLocalization.Error("JSON 中没有账号。 / No accounts found.", "english") == "No accounts found.", "Internal bilingual errors also select English correctly.");
        const string secrets = "Fixture diagnostic: access_token=access-value refresh_token=refresh-value id_token=id-value password=secret-password client_secret=secret-client Authorization: Bearer secret-bearer https://user:secret-url@example.test/callback?code=secret-code&state=secret-state eyJabc123456.eyJdef123456.signature123456 rt-secret-refresh sk-secret-api";
        var details = WindowsLocalization.TechnicalDetails(secrets, "simplifiedChinese")!;
        foreach (var secret in new[] { "access-value", "refresh-value", "id-value", "secret-password", "secret-client", "secret-bearer", "secret-url", "secret-code", "secret-state", "eyJabc123456", "rt-secret-refresh", "sk-secret-api" })
            Require(!details.Contains(secret), "Technical details redact credential fields, bearer tokens, URL credentials, OAuth query secrets and standalone token formats.");
        Require(details.Contains("Fixture diagnostic") && details.Contains("[redacted]"), "Redaction preserves useful non-secret diagnostic context.");
    }

    private static void CheckUpdateErrors()
    {
        const string failure = "Fixture update validation failed: access_token=fixture-secret";
        var native = new NativeSettings(@"Software\StarCodexSwitcher.UiChecks\LanguageUpdate-" + Guid.NewGuid().ToString("N"),
            _ => Task.FromException<UpdateRelease>(new FormatException(failure)));
        native.CheckUpdatesAsync().GetAwaiter().GetResult();
        Require(native.UpdateDiagnostic?.Contains("Fixture update validation failed") == true && !native.UpdateDiagnostic.Contains("fixture-secret"),
            "Update diagnostics retain their cause without storing the synthetic credential.");
        var window = new MainWindow(new LanguageClient("simplifiedChinese"), native) {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        var release = new UpdateRelease(new Version(9, 0, 0), new Uri("https://example.test/fixture.exe"), new string('0', 64), "合成更新说明");
        var dialog = new UpdateAvailableWindow(native, new UpdatePromptRequest(release, true), true, () => false) {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        try {
            window.Show(); window.Navigate("settings"); window.UpdateLayout();
            dialog.Show(); dialog.UpdateLayout();
            foreach (var surface in new Window[] { window, dialog }) {
                var text = Descendants<TextBlock>(surface).ToArray();
                Require(text.Any(block => block.Text.Contains("操作未完成")) && !text.Any(block => block.Text.Contains("Fixture update")),
                    "Settings and the update dialog use Chinese primary text for unfamiliar update failures.");
                Require(text.Any(block => block.ToolTip?.ToString()?.Contains("Fixture update validation failed") == true)
                    && !text.Any(block => block.ToolTip?.ToString()?.Contains("fixture-secret") == true),
                    "Both update surfaces retain only sanitized technical details.");
            }
        } finally { dialog.CloseForShutdown(); window.CloseForExit(); }
    }

    private static void CheckUpdateHelperLanguage()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        var variable = UpdateInstaller.HelperLanguageEnvironmentVariable;
        var originalEnvironment = Environment.GetEnvironmentVariable(variable);
        const string unknownFailure = "Fixture helper update failed before reading its job file.";
        var jobPath = Path.Combine(Path.GetTempPath(), "switcher-language-fixture", "job.json");
        try {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var client = new LanguageClient("simplifiedChinese");
            var start = UpdateInstaller.CreateHelperStartInfo(jobPath, client.State.Settings.Language);
            Require(start.ArgumentList.SequenceEqual(new[] { "--apply-update", jobPath }) && !start.UseShellExecute && start.CreateNoWindow,
                "Passing the chosen language preserves the hidden update-helper command.");
            Require(start.Environment[variable] == "simplifiedChinese", "The updater receives the selected Chinese preference even on English Windows.");
            Environment.SetEnvironmentVariable(variable, start.Environment[variable]);
            var message = WindowsLocalization.Error(unknownFailure, UpdateInstaller.ReadHelperLanguage());
            Require(message.Contains("操作未完成") && !message.Contains("Fixture"), "Helper errors before job parsing have a Chinese primary message with a Chinese preference.");
            Require(WindowsLocalization.Error(PermissionError, UpdateInstaller.ReadHelperLanguage()).Contains("文件是否被占用及目录权限"),
                "Helper file-access failures retain the Chinese corrective explanation.");
            var details = WindowsLocalization.TechnicalDetails("access_token=fixture-secret " + unknownFailure, UpdateInstaller.ReadHelperLanguage());
            Require(details!.Contains("技术详情") && details.Contains(unknownFailure) && !details.Contains("fixture-secret"),
                "Helper diagnostics retain the original cause separately and hide credentials.");

            foreach (var preference in new[] { "english", "system" }) {
                var english = UpdateInstaller.CreateHelperStartInfo(jobPath, preference);
                Environment.SetEnvironmentVariable(variable, english.Environment[variable]);
                Require(WindowsLocalization.Error(unknownFailure, UpdateInstaller.ReadHelperLanguage()) == unknownFailure,
                    "Explicit English and follow-system preferences still use English on English Windows.");
            }
            foreach (var invalid in new string?[] { null, "invalid-language" }) {
                Environment.SetEnvironmentVariable(variable, invalid);
                Require(UpdateInstaller.ReadHelperLanguage() == "simplifiedChinese", "Missing or invalid helper language safely defaults to Chinese.");
            }
        } finally {
            Environment.SetEnvironmentVariable(variable, originalEnvironment);
            CultureInfo.CurrentUICulture = originalCulture;
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
