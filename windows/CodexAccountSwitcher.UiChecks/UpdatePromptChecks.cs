using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;

internal static class UpdatePromptChecks
{
    private const string KeyRoot = @"Software\StarCodexSwitcher.UiChecks\UpdatePrompt-";
    private static int passed;
    private static readonly string LongNotes = string.Join("\n\n", Enumerable.Range(1, 30).Select(i =>
        $"{i}. 修复账号列表与更新交互，保留已有账号和设置。This synthetic release note verifies readable wrapping in a narrow update dialog."));
    private static UpdateRelease Release(string version = "9.0.0.0", string? notes = null) => new(new Version(version),
        new Uri("https://github.com/evenstar-2273/star-codex-switcher/releases/download/v9.0.0/fixture.exe"),
        new string('a', 64), notes ?? "修复更新提醒与取消登录。\n保留已有账号和设置。", Distribution.PackageType, new string('b', 64));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); passed++; }

    public static int RunChild(string key, string version, bool expected)
    {
        if (!key.StartsWith(KeyRoot, StringComparison.Ordinal)) throw new ArgumentException("Only an isolated update fixture key is allowed.");
        var native = new NativeSettings(key, _ => Task.FromResult(Release(version)));
        native.CheckUpdatesAsync().GetAwaiter().GetResult();
        var request = native.BeginUpdatePrompt(true);
        Check((request != null) == expected, "A fresh process reads only the persisted exact skipped version, not the previous process's Later decision.");
        if (request != null) native.CompleteUpdatePrompt(request, UpdatePromptChoice.Later);
        Console.WriteLine("PASS: fresh-process update prompt policy."); return 0;
    }

    public static void Run(App app, string output, NativeSettings? restoreSettings = null)
    {
        Directory.CreateDirectory(output);
        var priorContext = SynchronizationContext.Current; var shutdown = app.ShutdownMode;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try {
            PolicyRules();
            DeferredPrompts(app);
            DialogActionsAndAppearance(app, output);
            ProgressAndRetry(app, output);
            SuccessfulHandoff(app);
            ManualEntriesAndSettingsScroll(app, output);
            Console.WriteLine($"PASS: {passed} update-prompt assertions; automatic/manual timing, fresh-process policy, exact persistent skip, modal choices, overlay, progress, retry and stable settings scroll (synthetic delegates only).");
            File.WriteAllText(Path.Combine(output, "update-prompt-result.txt"), $"PASS: {passed} update-prompt assertions; no network, downloads, installations or real accounts.");
        } finally {
            if (restoreSettings != null) app.UseThemeSettings(restoreSettings);
            app.ShutdownMode = shutdown;
            SynchronizationContext.SetSynchronizationContext(priorContext);
        }
    }

    private static void PolicyRules()
    {
        var key = KeyRoot + Guid.NewGuid().ToString("N");
        try {
            var release = Release();
            var native = new NativeSettings(key, _ => Task.FromResult(release));
            native.CheckUpdatesAsync().GetAwaiter().GetResult();
            Check(native.BeginUpdatePrompt(false) == null, "An automatic result remains queued when the owner is not eligible.");
            var first = native.BeginUpdatePrompt(true)!;
            Check(first is { IsManual: false } && native.IsUpdatePromptOpen, "An eligible automatic result opens one prompt.");
            Check(native.BeginUpdatePrompt(true) == null, "Reentrant notifications cannot open a second prompt.");
            native.CompleteUpdatePrompt(first with { IsManual = true }, UpdatePromptChoice.SkipVersion);
            Check(native.IsUpdatePromptOpen && native.SkippedUpdateVersion == null, "A stale/copied prompt cannot complete a different active request.");
            native.CompleteUpdatePrompt(first, UpdatePromptChoice.Later);
            native.CheckUpdatesAsync().GetAwaiter().GetResult();
            Check(native.BeginUpdatePrompt(true) == null && native.ShouldShowUpdateBanner, "Not now suppresses repeat popups in this process while keeping the passive update entry.");
            Check(native.SkippedUpdateVersion == null, "Not now never persists a skipped version.");
            FreshProcess(key, "9.0.0.0", expected: true);
            native.CheckUpdatesAsync(manual: true).GetAwaiter().GetResult();
            var manual = native.BeginUpdatePrompt(false)!;
            Check(manual.IsManual, "Manual checking overrides this session's Later choice even off the home page.");
            native.CompleteUpdatePrompt(manual, UpdatePromptChoice.SkipVersion);
            Check(native.SkippedUpdateVersion == new Version("9.0.0.0") && !native.ShouldShowUpdateBanner, "Skip persists the exact normalized version and hides its banner.");
            FreshProcess(key, "9.0.0", expected: false);
            FreshProcess(key, "9.0.1.0", expected: true);
            native.CheckUpdatesAsync(manual: true).GetAwaiter().GetResult();
            var skippedManual = native.BeginUpdatePrompt(false)!;
            Check(skippedManual.IsManual, "Manual check still offers an explicitly skipped version.");
            native.CompleteUpdatePrompt(skippedManual, UpdatePromptChoice.Later);
            Check(new NativeSettings(key).SkippedUpdateVersion == new Version("9.0.0.0"), "Viewing a skipped update manually does not erase the saved skip preference.");
            release = Release("9.0.1.0"); native.CheckUpdatesAsync().GetAwaiter().GetResult();
            var higher = native.BeginUpdatePrompt(true);
            Check(higher != null && !higher.IsManual, "A higher version remains eligible after skipping an earlier release.");
            native.CompleteUpdatePrompt(higher!, UpdatePromptChoice.Later);

            var gate = new TaskCompletionSource<UpdateRelease>(TaskCreationOptions.RunContinuationsAsynchronously);
            var concurrent = new NativeSettings(key, _ => gate.Task);
            var automatic = concurrent.CheckUpdatesAsync();
            concurrent.CheckUpdatesAsync(manual: true).GetAwaiter().GetResult();
            Check(concurrent.IsChecking && concurrent.BeginUpdatePrompt(true) == null, "No prompt opens before an in-flight feed request finishes.");
            gate.SetResult(Release()); PumpUntil(() => automatic.IsCompleted); automatic.GetAwaiter().GetResult();
            var joined = concurrent.BeginUpdatePrompt(false);
            Check(joined is { IsManual: true }, "A manual click during an automatic fetch retains its explicit intent and overrides skip.");
            concurrent.CompleteUpdatePrompt(joined!, UpdatePromptChoice.Later);

            foreach (var clickBeforeCheck in new[] { false, true }) {
                var nextFetch = new TaskCompletionSource<UpdateRelease>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fetches = 0;
                var racing = new NativeSettings(key, _ => ++fetches == 1 ? Task.FromResult(Release("9.0.1.0")) : nextFetch.Task);
                racing.CheckUpdatesAsync().GetAwaiter().GetResult();
                if (clickBeforeCheck) racing.RequestUpdatePrompt();
                var refresh = racing.CheckUpdatesAsync();
                if (!clickBeforeCheck) racing.RequestUpdatePrompt();
                nextFetch.SetResult(Release("9.0.2.0")); PumpUntil(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
                var requestedLatest = racing.BeginUpdatePrompt(false);
                Check(requestedLatest is { IsManual: true } && requestedLatest.Release.Version == new Version("9.0.2.0"),
                    "A manual update entry before/during a background check follows its newest release instead of losing intent to an old version.");
                racing.CompleteUpdatePrompt(requestedLatest!, UpdatePromptChoice.Later);
            }
        } finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }

    private static void DeferredPrompts(App app)
    {
        foreach (var reason in new[] { "hidden", "minimized", "background", "settings", "busy", "adding" }) {
            using var fixture = new Fixture(app); fixture.Show();
            Window? foreground = null;
            switch (reason) {
                case "hidden": fixture.Window.Hide(); break;
                case "minimized": fixture.Window.WindowState = WindowState.Minimized; break;
                case "background": foreground = new Window { Title = "Synthetic foreground", Width = 240, Height = 120 }; foreground.Show(); foreground.Activate(); PumpFor(80); break;
                case "settings": fixture.Window.Navigate("settings"); break;
                case "busy": fixture.Client.Set(fixture.Client.State with { IsMutating = true }); break;
                case "adding": fixture.Client.Set(fixture.Client.State with { IsAddingAccount = true }); break;
            }
            try {
                fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult(); fixture.Window.EvaluateUpdatePrompt();
                AssertNoPrompt(app, "An automatic update cannot interrupt " + reason + ".");
                ExpectDialog(app, dialog => { Click(dialog, "暂不更新"); return Task.CompletedTask; }, () => {
                    foreground?.Close(); foreground = null;
                    fixture.Client.Set(fixture.Client.State with { IsMutating = false, IsAddingAccount = false });
                    fixture.Window.WindowState = WindowState.Normal; fixture.Window.Navigate("accounts"); fixture.Show();
                    fixture.Window.EvaluateUpdatePrompt();
                });
                Check(!fixture.Native.IsUpdatePromptOpen, "The deferred " + reason + " prompt completes after the owner becomes usable.");
                fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult(); fixture.Window.EvaluateUpdatePrompt();
                AssertNoPrompt(app, "Returning/refetching cannot repeat the already shown version in this process.");
            } finally { foreground?.Close(); }
        }
        using (var fixture = new Fixture(app)) {
            fixture.Show();
            ExpectDialog(app, dialog => { Click(dialog, "暂不更新"); return Task.CompletedTask; }, () => {
                var other = new AddAccountWindow { Owner = fixture.Window };
                other.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                    fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult(); fixture.Window.EvaluateUpdatePrompt();
                    AssertNoPrompt(app, "An existing account-method modal prevents update interruption.");
                    Check(!WindowEnabled(fixture.Window), "The blocking fixture is a real modal owner relationship.");
                    other.Close();
                }));
                other.ShowDialog(); fixture.Window.Activate(); fixture.Window.EvaluateUpdatePrompt();
            });
        }
    }

    private static void DialogActionsAndAppearance(App app, string output)
    {
        foreach (var dark in new[] { false, true }) {
            using var fixture = new Fixture(app); fixture.Native.ThemePreference = dark ? "dark" : "light"; fixture.Show();
            ExpectDialog(app, dialog => {
                Check(dialog.Owner == fixture.Window && !WindowEnabled(fixture.Window) && dialog.Icon != null, "The update is a branded, genuinely modal owned dialog.");
                Check(All<Button>(dialog).Select(Name).Order().SequenceEqual(new[] { "暂不更新", "更新并重启", "跳过版本" }.Order()), "All three update decisions are present.");
                Check(All<TextBlock>(Button(dialog, "更新并重启")).All(text => text.Foreground == app.Resources["OnAccent"]),
                    "Primary update text uses the legible on-accent foreground in both light and dark palettes.");
                Check(All<TextBlock>(dialog).Any(t => t.Text.Contains("9.0.0")) && All<TextBlock>(dialog).Any(t => t.Text.Contains(fixture.Native.DisplayVersion)), "The prompt identifies both installed and available versions.");
                Check(Overlay(fixture).Visibility == Visibility.Visible && ((SolidColorBrush)Overlay(fixture).Background).Color.A is > 0 and < 255, "A real semi-transparent overlay dims the owner during the modal.");
                Capture(dialog, Path.Combine(output, $"update-dialog-{(dark ? "dark" : "light")}.png"));
                CaptureScene(fixture.Window, dialog, Path.Combine(output, $"update-overlay-{(dark ? "dark" : "light")}.png"));
                Click(dialog, "暂不更新"); return Task.CompletedTask;
            }, () => fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult());
            Check(Overlay(fixture).Visibility == Visibility.Collapsed && WindowEnabled(fixture.Window) && fixture.InstallCalls == 0, "Later removes the overlay and performs no installation.");
        }
        foreach (var choice in new[] { "skip", "close", "escape" }) {
            using var fixture = new Fixture(app); fixture.Show(); UpdateAvailableWindow? shown = null;
            ExpectDialog(app, dialog => {
                shown = dialog;
                if (choice == "skip") Click(dialog, "跳过版本");
                else if (choice == "close") dialog.Close();
                else Escape(dialog);
                return Task.CompletedTask;
            }, () => fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult());
            Check(shown!.Choice == (choice == "skip" ? UpdatePromptChoice.SkipVersion : UpdatePromptChoice.Later), "The " + choice + " close action reports its intended choice.");
            Check(fixture.InstallCalls == 0 && (fixture.Native.SkippedUpdateVersion != null) == (choice == "skip"), "Only the skip button persists a skipped release; X/Esc are Later.");
        }
        using (var fixture = new Fixture(app)) {
            fixture.Release = Release(notes: LongNotes); fixture.Window.Width = 760; fixture.Show();
            ExpectDialog(app, dialog => {
                dialog.SizeToContent = SizeToContent.Manual; dialog.Width = 460; dialog.Height = 560; dialog.UpdateLayout();
                Check(dialog.ActualWidth <= 470, "The narrow fixture is actually arranged at a narrow width.");
                var notes = All<ScrollViewer>(dialog).Single();
                Check(notes.ScrollableHeight > 0, "Long release notes scroll independently of the decisions.");
                notes.ScrollToEnd(); dialog.UpdateLayout();
                foreach (var button in All<Button>(dialog)) {
                    var bottom = button.TranslatePoint(new Point(0, button.ActualHeight), dialog).Y;
                    Check(button.IsVisible && button.IsEnabled && bottom <= dialog.ActualHeight, "Every decision remains reachable in the narrow update window.");
                }
                CaptureScene(fixture.Window, dialog, Path.Combine(output, "update-narrow-long-notes.png"));
                Click(dialog, "暂不更新"); return Task.CompletedTask;
            }, () => fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult());
        }
    }

    private static void ProgressAndRetry(App app, string output)
    {
        using var fixture = new Fixture(app); fixture.Release = Release(notes: LongNotes); fixture.Show();
        var gates = new List<TaskCompletionSource>(); var reporters = new List<IProgress<string>>();
        fixture.Install = (_, progress, _) => { reporters.Add(progress); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); gates.Add(gate); return gate.Task; };
        UpdateAvailableWindow? shown = null;
        try {
            ExpectDialog(app, async dialog => {
                shown = dialog; Click(dialog, "更新并重启"); Click(dialog, "更新并重启");
                Check(fixture.InstallCalls == 1 && fixture.Native.IsInstalling, "Repeated clicks call the existing injected installer exactly once per attempt.");
                Check(All<Button>(dialog).All(button => !button.IsEnabled), "All decision buttons are disabled while installation is running.");
                dialog.Close(); Escape(dialog);
                Check(dialog.IsVisible && Overlay(fixture).IsVisible, "X/Esc cannot dismiss an in-progress handoff or remove its overlay.");
                var bar = All<ProgressBar>(dialog).Single();
                Check(bar.IsIndeterminate, "Preparation starts with indeterminate progress instead of a made-up percentage.");
                reporters[0].Report("正在下载更新 42%");
                await UntilAsync(() => fixture.Native.InstallProgress?.Percent == 42);
                Check(!bar.IsIndeterminate && Math.Abs(bar.Value - 42) < .1, "The progress bar shows the downloader's actual percentage.");
                Check(All<TextBlock>(dialog).Any(t => t.Text == LongNotes), "Version notes remain present while downloading.");
                CaptureScene(fixture.Window, dialog, Path.Combine(output, "update-progress-42-percent.png"));
                reporters[0].Report("正在下载更新 4 MB"); await UntilAsync(() => fixture.Native.InstallProgress?.Percent == null);
                Check(bar.IsIndeterminate && fixture.Native.InstallProgress?.Stage == UpdateInstallStage.Downloading, "Unknown content length remains indeterminate while retaining the download stage.");
                Capture(dialog, Path.Combine(output, "update-progress-unknown-length.png"));
                reporters[0].Report("正在校验更新包……"); await UntilAsync(() => fixture.Native.InstallProgress?.Stage == UpdateInstallStage.Verifying);
                Check(bar.IsIndeterminate, "Checksum verification does not invent download completion percentages.");
                reporters[0].Report("正在下载更新 90%"); await Task.Delay(30);
                Check(fixture.Native.InstallProgress?.Stage == UpdateInstallStage.Verifying, "A delayed earlier-stage progress callback cannot regress verification.");
                reporters[0].Report("校验完成，正在启动安装程序……"); await UntilAsync(() => fixture.Native.InstallProgress?.Stage == UpdateInstallStage.Restarting);
                Capture(dialog, Path.Combine(output, "update-progress-handoff.png"));
                gates[0].SetException(new IOException("模拟下载失败，可安全重试。"));
                await UntilAsync(() => !fixture.Native.IsInstalling && Button(dialog, "更新并重启").IsEnabled);
                Check(dialog.IsVisible && All<Button>(dialog).All(b => b.IsEnabled) && All<TextBlock>(dialog).Any(t => t.Text.Contains("模拟下载失败")), "A failed update remains in the same modal with visible error and restored decisions.");
                Check(fixture.ExitCalls == 0, "A failed synthetic update never exits the application.");
                CaptureScene(fixture.Window, dialog, Path.Combine(output, "update-failed-retry.png"));
                Click(dialog, "更新并重启"); Check(fixture.InstallCalls == 2, "Retry invokes a fresh installer attempt once.");
                reporters[0].Report("正在下载更新 99%"); await Task.Delay(30);
                Check(fixture.Native.InstallProgress?.Stage == UpdateInstallStage.Preparing, "A previous failed attempt cannot overwrite a fresh retry's progress.");
                gates[1].SetException(new IOException("模拟重试失败，当前版本和账号保留。"));
                await UntilAsync(() => !fixture.Native.IsInstalling && Button(dialog, "更新并重启").IsEnabled);
                dialog.Close();
            }, () => fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult());
            Check(shown!.Choice == UpdatePromptChoice.Later && Overlay(fixture).Visibility == Visibility.Collapsed, "X after a failed retry is Later and restores the owner.");
        } finally { foreach (var gate in gates) gate.TrySetException(new IOException("Synthetic fixture ended.")); }
    }

    private static void ManualEntriesAndSettingsScroll(App app, string output)
    {
        using var fixture = new Fixture(app); fixture.Show(); fixture.Window.Navigate("settings");
        fixture.Window.Height = 540; fixture.Window.UpdateLayout();
        fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult(); fixture.Window.EvaluateUpdatePrompt();
        AssertNoPrompt(app, "Automatic discovery stays quiet on settings.");
        var scroll = fixture.Window.PageScrollViewer; scroll.ScrollToEnd(); PumpFor(60); fixture.Window.UpdateLayout();
        var position = scroll.VerticalOffset;
        Check(position > 100, "The update settings fixture starts below the top.");
        ExpectDialog(app, dialog => {
            Check(dialog.Request.IsManual, "The settings check button requests an explicit prompt.");
            CaptureScene(fixture.Window, dialog, Path.Combine(output, "update-from-settings.png"));
            Click(dialog, "跳过版本"); return Task.CompletedTask;
        }, () => Click(fixture.Window, "检查更新"));
        PumpFor(60); fixture.Window.UpdateLayout();
        Check(ReferenceEquals(scroll, fixture.Window.PageScrollViewer) && Math.Abs(scroll.VerticalOffset - position) < 1, "Checking/dismissing an update retains the settings scroll position.");
        ExpectDialog(app, dialog => { Check(dialog.Request.IsManual, "Manual settings check overrides persistent skip."); Click(dialog, "暂不更新"); return Task.CompletedTask; },
            () => Click(fixture.Window, "检查更新"));
        Check(fixture.InstallCalls == 0, "Checking and viewing updates cannot start the installer.");
        fixture.Release = Release("9.0.1.0");
        ExpectDialog(app, dialog => { Click(dialog, "暂不更新"); return Task.CompletedTask; }, () => fixture.Native.CheckUpdatesAsync(manual: true).GetAwaiter().GetResult());
        fixture.Window.Navigate("accounts"); fixture.Window.Activate(); PumpFor(40);
        ExpectDialog(app, dialog => {
            Check(dialog.Request.IsManual && All<Button>(dialog).Any(b => Name(b) == "更新并重启"), "The home banner enters the same modal rather than invoking installation directly.");
            Click(dialog, "暂不更新"); return Task.CompletedTask;
        }, () => Click(fixture.Window, "更新并重启"));
        Check(fixture.InstallCalls == 0, "The home banner waits for a decision inside the shared modal.");
    }

    private static void SuccessfulHandoff(App app)
    {
        using var fixture = new Fixture(app); fixture.Show();
        fixture.Install = (_, _, _) => Task.CompletedTask;
        fixture.Native.ExitForUpdate = () => { fixture.ExitCalls++; fixture.Window.CloseForExit(); return Task.CompletedTask; };
        UpdateAvailableWindow? shown = null;
        ExpectDialog(app, dialog => { shown = dialog; Click(dialog, "更新并重启"); return Task.CompletedTask; },
            () => fixture.Native.CheckUpdatesAsync().GetAwaiter().GetResult());
        Check(fixture.InstallCalls == 1 && fixture.ExitCalls == 1 && fixture.Closed && shown!.Choice == UpdatePromptChoice.UpdateNow,
            "A successful injected handoff calls the existing installer/exit delegates once and closes the modal through the explicit shutdown path.");
    }

    private static void FreshProcess(string key, string version, bool expected)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        var runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        if (File.Exists(Path.Combine(runtimeRoot, "dotnet.exe"))) start.Environment["DOTNET_ROOT"] = runtimeRoot;
        foreach (var arg in new[] { "--test-update-prompt-child", key, version, expected ? "yes" : "no" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start isolated update policy child.");
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Update policy child timed out."); }
        Check(process.ExitCode == 0, "Fresh-process policy failed: " + process.StandardError.ReadToEnd());
    }
    private static void ExpectDialog(App app, Func<UpdateAvailableWindow, Task> action, Action trigger)
    {
        Exception? failure = null; var started = false; var completed = false;
        var timer = new DispatcherTimer(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += async (_, _) => {
            if (started) return;
            var dialog = app.Windows.OfType<UpdateAvailableWindow>().FirstOrDefault(w => w.IsVisible);
            if (dialog == null) return;
            started = true;
            try { dialog.UpdateLayout(); await action(dialog); }
            catch (Exception ex) { failure = ex; dialog.CloseForShutdown(); }
            finally { completed = true; }
        };
        timer.Start();
        try {
            trigger();
            PumpUntil(() => completed && !app.Windows.OfType<UpdateAvailableWindow>().Any(w => w.IsVisible), 8000,
                () => { foreach (var dialog in app.Windows.OfType<UpdateAvailableWindow>().ToArray()) dialog.CloseForShutdown(); });
            if (failure != null) throw failure;
            Check(started, "The expected modal must actually be shown.");
        } finally { timer.Stop(); }
    }
    private static void AssertNoPrompt(App app, string message)
    {
        var unexpected = false;
        var timer = new DispatcherTimer(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { foreach (var dialog in app.Windows.OfType<UpdateAvailableWindow>().Where(w => w.IsVisible).ToArray()) { unexpected = true; dialog.CloseForShutdown(); } };
        timer.Start(); try { PumpFor(100); } finally { timer.Stop(); }
        Check(!unexpected && !app.Windows.OfType<UpdateAvailableWindow>().Any(w => w.IsVisible), message);
    }
    private static void PumpFor(int milliseconds) { var watch = Stopwatch.StartNew(); PumpUntil(() => watch.ElapsedMilliseconds >= milliseconds, milliseconds + 2000); }
    private static void PumpUntil(Func<bool> done, int timeout = 5000, Action? onTimeout = null)
    {
        var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var timedOut = false;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => {
            if (done()) { frame.Continue = false; return; }
            if (watch.ElapsedMilliseconds > timeout) { timedOut = true; onTimeout?.Invoke(); frame.Continue = false; }
        };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (timedOut) throw new TimeoutException("Synthetic update prompt did not reach the expected UI state.");
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(10); }
        throw new TimeoutException("Synthetic installer progress did not reach the expected state.");
    }
    private static Border Overlay(Fixture fixture) => All<Border>(fixture.Window).Single(b => Name(b) == "更新遮罩");
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);
    private static bool WindowEnabled(Window window) => IsWindowEnabled(new WindowInteropHelper(window).Handle);
    private static string Name(DependencyObject value) => AutomationProperties.GetName(value);
    private static Button Button(DependencyObject owner, string name) => All<Button>(owner).Single(b => Name(b) == name);
    private static void Click(DependencyObject owner, string name) => Button(owner, name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static void Escape(Window window) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var descendant in All<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); SaveVisual(window.ActualWidth, window.ActualHeight, path, drawing => drawing.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight)));
    }
    private static void CaptureScene(MainWindow owner, Window dialog, string path)
    {
        owner.UpdateLayout(); dialog.UpdateLayout();
        SaveVisual(owner.ActualWidth, owner.ActualHeight, path, drawing => {
            drawing.DrawRectangle(new VisualBrush(owner), null, new Rect(0, 0, owner.ActualWidth, owner.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(dialog), null, new Rect(Math.Max(0, (owner.ActualWidth - dialog.ActualWidth) / 2), Math.Max(0, (owner.ActualHeight - dialog.ActualHeight) / 2), dialog.ActualWidth, dialog.ActualHeight));
        });
    }
    private static void SaveVisual(double width, double height, string path, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) draw(drawing);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 1.5), (int)Math.Ceiling(height * 1.5), 144, 144, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string key = KeyRoot + Guid.NewGuid().ToString("N");
        public readonly Client Client = new();
        public readonly NativeSettings Native;
        public readonly MainWindow Window;
        public UpdateRelease Release = UpdatePromptChecks.Release();
        public int InstallCalls, ExitCalls;
        public bool Closed;
        public Func<UpdateRelease, IProgress<string>, CancellationToken, Task> Install = (_, _, _) => throw new IOException("Synthetic installation must be configured explicitly.");
        public Fixture(App app) {
            Native = new NativeSettings(key, _ => Task.FromResult(Release), (release, progress, ct) => { InstallCalls++; return Install(release, progress, ct); });
            Native.ExitForUpdate = () => { ExitCalls++; return Task.CompletedTask; };
            Native.ThemePreference = "light"; Native.AccentPreference = "purple";
            Window = new MainWindow(Client, Native) { Width = 900, Height = 760 };
            Window.Closed += (_, _) => Closed = true;
        }
        public void Show() { Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); Window.UpdateLayout(); }
        public void Dispose() { if (!Closed) Window.CloseForExit(); Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }
    private sealed class Client : IAccountClient
    {
        public event Action? Changed;
        public AccountSnapshot State { get; private set; }
        public Client() {
            var id = Guid.NewGuid(); var now = DateTimeOffset.Now;
            State = AccountSnapshot.Empty with { Accounts = [new(new(id, "Fixture", "alex@example.test"), "A", new(83, now.AddDays(6), 91, now.AddHours(3), 2, null, "plus"), null,
                Subscription: new(now.AddDays(29), now, "fixture", true), PlanType: "plus", SubscriptionStatus: "verified")], ActiveAccountID = id, Settings = new("simplifiedChinese"),
                Strings = new() { ["settings_general"] = "通用", ["settings_automation"] = "自动化", ["settings_updates"] = "软件更新", ["manage"] = "管理账号" } };
        }
        public void Set(AccountSnapshot state) { State = state; Changed?.Invoke(); }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) => Task.CompletedTask;
    }
}
