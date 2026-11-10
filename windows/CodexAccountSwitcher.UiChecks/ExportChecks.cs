using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;

internal static class ExportChecks
{
    private static int assertions;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
    public static void Run(App app, string output, NativeSettings restore)
    {
        Directory.CreateDirectory(output);
        var key = @"Software\StarCodexSwitcher.UiChecks\Export-" + Guid.NewGuid().ToString("N");
        var native = new NativeSettings(key);
        var priorContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var client = new Client();
        var main = new MainWindow(client, native) { Width = 1200, Height = 760 };
        try {
            main.Show(); Pump();
            foreach (var width in new[] { 1200, 920, 780, 540 }) {
                main.Width = width; main.Navigate("accounts"); Pump();
                var export = All<Button>(main).First(b => Name(b) == "导出此账号");
                var group = (StackPanel)export.Parent;
                var buttons = group.Children.OfType<Button>().ToArray();
                Check(buttons[^1].ToolTip as string == "删除此账号" && buttons[^2] == export, "Export stays immediately left of delete.");
                var actions = (Panel)group.Parent;
                var bounds = All<Button>(actions).Select(b => b.TransformToAncestor(actions).TransformBounds(new Rect(b.RenderSize))).ToArray();
                Check(bounds.All(b => b.Left >= -.5 && b.Right <= actions.ActualWidth + .5 && b.Bottom <= actions.ActualHeight + .5), "All action buttons must remain inside the row bounds.");
                for (int i = 0; i < bounds.Length; i++) for (int j = i + 1; j < bounds.Length; j++) Check(!bounds[i].IntersectsWith(bounds[j]), "Buttons must not overlap after shrinking or wrapping.");
                Capture(main, Path.Combine(output, $"actions-{width}.png"));
            }
            foreach (var dark in new[] { false, true }) {
                app.ApplyPalette(dark);
                string? copied = null;
                var target = Path.Combine(output, "fixture-export.json");
                var dialog = new JsonExportWindow(client, client.State.Accounts[0], text => copied = text, _ => target) { Owner = main };
                try {
                    dialog.Show(); Pump();
                    var selector = All<ComboBox>(dialog).Single(); var editor = All<TextBox>(dialog).Single();
                    Check(selector.SelectedIndex == 0 && client.Format == AccountExportFormat.Star, "Portable format is selected by default.");
                    for (int i = 0; i < 4; i++) {
                        selector.SelectedIndex = i; Pump();
                        Check(!editor.Text.Contains("fixture-refresh"), "Preview must not reveal refresh credentials.");
                        Click(dialog, "复制 JSON");
                        Check(copied == client.Last && copied!.Contains("fixture-refresh"), "Copy uses the complete selected format, not the masked preview.");
                        Click(dialog, "下载 JSON");
                        var deadline = DateTime.UtcNow.AddSeconds(5);
                        while (!All<Button>(dialog).Single(b => Name(b) == "下载 JSON").IsEnabled && DateTime.UtcNow < deadline) Pump();
                        Check(File.Exists(target) && File.ReadAllText(target) == copied, "Downloaded JSON matches copied JSON.");
                        Check(!File.ReadAllBytes(target).Take(3).SequenceEqual(new byte[] {239, 187, 191}), "JSON download uses UTF-8 without a BOM.");
                    }
                    selector.SelectedIndex = 0; dialog.Width = 520; Pump();
                    Capture(dialog, Path.Combine(output, dark ? "export-dark.png" : "export-light.png"));
                    All<CheckBox>(dialog).Single().IsChecked = true;
                    Check(editor.Text.Contains("fixture-refresh"), "Explicit reveal displays full data.");
                    All<CheckBox>(dialog).Single().IsChecked = false;
                    client.Fail = true; selector.SelectedIndex = 2;
                    Check(editor.Text == "" && !All<Button>(dialog).Single(b => Name(b) == "复制 JSON").IsEnabled, "Failed format load clears stale export data and disables actions.");
                    Check(!All<TextBlock>(dialog).Any(t => t.Text.Contains("fixture-secret-error")), "Internal errors never reveal raw credential details.");
                } finally { client.Fail = false; dialog.Close(); }
            }
        } finally { main.CloseForExit(); app.UseThemeSettings(restore); SynchronizationContext.SetSynchronizationContext(priorContext); Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); }
        Console.WriteLine($"PASS: {assertions} export dialog, default format, full copy/download, preview privacy and responsive action assertions.");
    }
    private static string Name(DependencyObject o) => AutomationProperties.GetName(o);
    private static void Click(Window window, string label) => All<Button>(window).Single(b => Name(b) == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject {
        if (parent is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Pump() {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static void Capture(Window window, string path) {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class Client : IAccountClient, IAccountExporter
    {
        public event Action? Changed { add { } remove { } }
        public AccountExportFormat Format; public string Last = ""; public bool Fail;
        public AccountSnapshot State { get; }
        private readonly LoginCredential credential;
        public Client() {
            var jwt = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"email":"export@example.test","https://api.openai.com/auth":{"chatgpt_account_id":"export-fixture","chatgpt_plan_type":"plus"}}""")).TrimEnd('=') + ".fixture";
            credential = LoginCredential.Parse(JsonSerializer.SerializeToUtf8Bytes(new { tokens = new { access_token = "fixture-access", id_token = jwt, refresh_token = "fixture-refresh", account_id = "export-fixture" } }));
            State = AccountSnapshot.Empty with { Settings = new("simplifiedChinese"), Accounts = Enumerable.Range(0, 3).Select(i => new AccountRow(new(Guid.NewGuid(), "导出测试", "export@example.test"), "E", new(65, DateTimeOffset.Now.AddDays(3), 85, DateTimeOffset.Now.AddHours(2), 2), null, PlanType: "plus")).ToArray() };
        }
        public string ExportAccount(Guid id, AccountExportFormat format) { if (Fail) throw new IOException("fixture-secret-error"); Format = format; return Last = AccountExport.Build(credential, State.Accounts.Single(r => r.Profile.Id == id), format); }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) => Task.CompletedTask;
    }
}
