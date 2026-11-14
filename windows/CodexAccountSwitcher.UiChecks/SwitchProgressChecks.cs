using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexAccountSwitcher;
using CodexAccountSwitcher.Core;

internal static class SwitchProgressChecks
{
    private static int checks;
    public static void Run(App app, string output)
    {
        Directory.CreateDirectory(output);
        var key = @"Software\StarCodexSwitcher.UiChecks\Switch-" + Guid.NewGuid().ToString("N");
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var native = new NativeSettings(key); var client = new Client(); var window = new MainWindow(client, native) { Width = 950, Height = 700 };
        try {
            window.Show(); Pump();
            foreach (var dark in new[] { true, false }) {
                native.ThemePreference = dark ? "dark" : "light"; window.Width = dark ? 950 : 480; window.Height = dark ? 700 : 460; Pump();
                var run = (Task)typeof(MainWindow).GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object?[] { "switch", client.ID, null, null })!;
                Pump(); var panel = All<SwitchProgressPanel>(window).Single();
                Check(panel.Visibility == Visibility.Visible && panel.Running && window.IsBusy && !window.PageScrollViewer.IsEnabled, "Switch opens blocking overlay before the backend acknowledges mutation");
                Check(All<ProgressBar>(panel).Single().IsIndeterminate, "Waiting stage is indeterminate, not a fake download percentage");
                client.Report(new(58, "正在恢复本地项目分组…")); Pump();
                Check(All<TextBlock>(panel).Any(text => text.Text == "正在恢复本地项目分组…") && All<ProgressBar>(panel).Single().Value == 58, "Actual backend progress reaches the popup");
                Check(All<Border>(panel).Any(border => border.Background is SolidColorBrush brush && brush.Color.A == 255), "Popup has opaque themed surface over dimmed owner");
                Capture(window, Path.Combine(output, dark ? "switch-progress-dark.png" : "switch-progress-light-narrow.png"));
                var completedAt = Stopwatch.StartNew();
                client.Complete(dark ? null : "项目数据库正被其他进程占用，已恢复原登录。请关闭相关窗口后重试。");
                Wait(() => run.IsCompleted); Pump();
                Check(!panel.Running && !window.IsBusy && panel.Visibility == Visibility.Visible, "Result is initially visible after completion");
                Check(All<TextBlock>(panel).Any(text => dark ? text.Text == "操作完成" : text.Text == "操作未完成"), "Errors are not presented as success");
                Check(All<ProgressBar>(panel).Single().Value == (dark ? 100 : 58), "Failure must not advance to 100 percent");
                var close = All<Button>(panel).Single(button => AutomationProperties.GetName(button) == "关闭切号进度");
                Check(close.IsVisible && close.IsEnabled, "Result close action is reachable in a narrow window");
                Capture(window, Path.Combine(output, dark ? "switch-complete-dark.png" : "switch-failure-light-narrow.png"));
                if (dark) {
                    Wait(() => completedAt.ElapsedMilliseconds >= 1500);
                    Check(panel.Visibility == Visibility.Visible, "Success remains readable before the 2.5-second timeout");
                    Wait(() => panel.Visibility == Visibility.Collapsed);
                    Check(completedAt.ElapsedMilliseconds >= 2200 && completedAt.ElapsedMilliseconds < 3500, "Success automatically closes after 2.5 seconds");
                } else {
                    Wait(() => completedAt.ElapsedMilliseconds >= 2700);
                    Check(panel.Visibility == Visibility.Visible, "Failure stays open beyond the success timeout");
                    close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                }
                Check(panel.Visibility == Visibility.Collapsed && window.PageScrollViewer.IsEnabled, "Closing result restores account-list interactions");
            }
            var overlay = All<SwitchProgressPanel>(window).Single();
            overlay.Begin(false);
            overlay.Report(new(72, "正在检查会话…", "重复项目入口未强制合并，会话关联已保留。"));
            overlay.Finish(null);
            Check(All<TextBlock>(overlay).Any(text => text.Text == "重复项目入口未强制合并，会话关联已保留。"), "Ambiguous project result is not overwritten by a success claim");
            var firstResult = Stopwatch.StartNew();
            Wait(() => firstResult.ElapsedMilliseconds >= 1000);
            overlay.Begin(true);
            Wait(() => firstResult.ElapsedMilliseconds >= 2800);
            Check(overlay.Running && overlay.Visibility == Visibility.Visible, "An earlier result timer cannot close a new operation");
            overlay.Finish(null);
            Check(All<TextBlock>(overlay).Any(text => text.Text == "本地项目与会话显示检查已完成。"), "New operation clears the previous diagnostic");
            var button = All<Button>(overlay).Single(button => AutomationProperties.GetName(button) == "关闭切号进度");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            overlay.Begin(false);
            var manualClosedAt = Stopwatch.StartNew();
            Wait(() => manualClosedAt.ElapsedMilliseconds >= 2700);
            Check(overlay.Visibility == Visibility.Visible && overlay.Running, "Manual close cancels its pending timer");
            overlay.Finish("测试结束"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine($"PASS: {checks} switch progress assertions (dark/light, narrow, success/failure, input blocking).");
        } finally { window.CloseForExit(); Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
    private static void Pump() { var clock = Stopwatch.StartNew(); Wait(() => clock.ElapsedMilliseconds > 60); }
    private static void Wait(Func<bool> done) {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || clock.ElapsedMilliseconds > 5000) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!done()) throw new TimeoutException("UI fixture did not finish");
    }
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T t) yield return t; foreach (var item in All<T>(child)) yield return item; }
    }
    private static void Capture(Window window, string path) {
        window.UpdateLayout(); var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class Client : IAccountClient, ISwitchProgressSource {
        public Guid ID = Guid.NewGuid();
        public event Action? Changed;
        public event Action<SwitchProgress>? SwitchProgressChanged;
        private TaskCompletionSource? pending;
        public AccountSnapshot State { get; private set; } = AccountSnapshot.Empty with { Settings = new("simplifiedChinese") };
        public Client() { State = State with { Accounts = [new(new(ID, "本地测试", "fixture@example.test"), "F", new(70, DateTimeOffset.Now.AddDays(3), 90, DateTimeOffset.Now.AddHours(2)), null)] }; }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null) {
            if (command != "switch") return Task.CompletedTask;
            State = State with { Error = null }; Changed?.Invoke(); pending = new(TaskCreationOptions.RunContinuationsAsynchronously); return pending.Task;
        }
        public void Report(SwitchProgress progress) => SwitchProgressChanged?.Invoke(progress);
        public void Complete(string? error) { State = State with { Error = error }; Changed?.Invoke(); if (error == null) Report(new(100, "操作完成")); pending!.SetResult(); }
    }
}
