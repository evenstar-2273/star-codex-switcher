using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexAccountSwitcher;

public sealed class UpdateAvailableWindow : Window
{
    private readonly NativeSettings native;
    private readonly Func<bool> canInstall;
    private readonly bool chinese;
    private readonly TextBlock status;
    private readonly ProgressBar progress;
    private readonly StackPanel progressArea;
    private readonly Button skip, later, update;
    private bool allowShutdown, startingInstall;
    private string? localError;
    public UpdatePromptChoice Choice { get; private set; } = UpdatePromptChoice.Later;
    public UpdatePromptRequest Request { get; }
    private string L(string zh, string en) => chinese ? zh : en;

    public UpdateAvailableWindow(NativeSettings native, UpdatePromptRequest request, bool chinese, Func<bool> canInstall)
    {
        this.native = native; this.canInstall = canInstall; this.chinese = chinese; Request = request;
        Title = L("发现新版本", "Update available") + " · " + App.DisplayName;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Width = Math.Min(640, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 32);
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(ForegroundProperty, "Ink");
        var body = new Grid { Margin = new Thickness(26) };
        for (var i = 0; i < 6; i++) body.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        TextBlock Text(string text, double size, string color = "Ink", bool bold = false) {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            block.SetResourceReference(TextBlock.ForegroundProperty, color); return block;
        }
        var heading = Text(L("发现新版本 ", "Update available: ") + NativeSettings.FormatVersion(request.Release.Version), 24, bold: true);
        body.Children.Add(heading);
        var introduction = Text(L($"当前版本 {native.DisplayVersion} · 更新后自动重新启动，保留账号数据。",
            $"Current version {native.DisplayVersion}. The app will restart and keep your accounts."), 14, "Muted");
        introduction.Margin = new Thickness(0, 10, 0, 20); Grid.SetRow(introduction, 1); body.Children.Add(introduction);
        var notesTitle = Text(L("更新内容", "What's new"), 16, bold: true); notesTitle.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(notesTitle, 2); body.Children.Add(notesTitle);
        var notes = Text(string.IsNullOrWhiteSpace(request.Release.Notes) ? L("此版本未提供更新说明。", "No release notes were provided.") : request.Release.Notes, 14);
        AutomationProperties.SetName(notes, L("更新说明", "Release notes"));
        var notesScroll = new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MinHeight = 72, MaxHeight = 240, Padding = new Thickness(16) };
        var notesBox = new Border { Child = notesScroll, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        notesBox.SetResourceReference(BackgroundProperty, "ListSurface"); notesBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        Grid.SetRow(notesBox, 3); body.Children.Add(notesBox);
        var feedback = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        progressArea = new StackPanel { Visibility = Visibility.Collapsed };
        progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 7, Margin = new Thickness(0, 0, 0, 10) };
        progress.SetResourceReference(Control.ForegroundProperty, "Accent"); progress.SetResourceReference(Control.BackgroundProperty, "Track");
        AutomationProperties.SetName(progress, L("更新下载进度", "Update download progress")); progressArea.Children.Add(progress); feedback.Children.Add(progressArea);
        status = Text("", 14, "Muted"); status.Visibility = Visibility.Collapsed; feedback.Children.Add(status);
        Grid.SetRow(feedback, 4); body.Children.Add(feedback);
        Button Action(string label, Action action) {
            var caption = new TextBlock { Text = label };
            caption.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
            var button = new Button { Content = caption, Style = (Style)FindResource("ActionButton"), Margin = new Thickness(0, 8, 10, 0), Padding = new Thickness(16, 10, 16, 10) };
            AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
        }
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, -10, 0) };
        skip = Action(L("跳过版本", "Skip version"), () => { if (CanClose) { Choice = UpdatePromptChoice.SkipVersion; Close(); } });
        later = Action(L("暂不更新", "Not now"), () => { if (CanClose) { Choice = UpdatePromptChoice.Later; Close(); } });
        update = Action(L("更新并重启", "Update & restart"), () => _ = InstallAsync());
        update.SetResourceReference(Control.BackgroundProperty, "Accent"); update.SetResourceReference(Control.BorderBrushProperty, "Accent"); update.SetResourceReference(Control.ForegroundProperty, "OnAccent");
        actions.Children.Add(skip); actions.Children.Add(later); actions.Children.Add(update); Grid.SetRow(actions, 5); body.Children.Add(actions);
        Content = body;
        PreviewKeyDown += (_, e) => { if (e.Key != Key.Escape) return; e.Handled = true; if (CanClose) { Choice = UpdatePromptChoice.Later; Close(); } };
        Closing += OnClosing;
        native.Changed += RefreshState;
        Closed += (_, _) => native.Changed -= RefreshState;
        RefreshState();
    }
    private bool CanClose => !native.IsInstalling && !startingInstall;
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowShutdown) return;
        if (!CanClose) { e.Cancel = true; return; }
        if (Choice != UpdatePromptChoice.SkipVersion) Choice = UpdatePromptChoice.Later;
    }
    public void CloseForShutdown() { allowShutdown = true; Close(); }
    public void RefreshState()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RefreshState)); return; }
        var installing = native.IsInstalling || startingInstall;
        skip.IsEnabled = later.IsEnabled = !installing;
        update.IsEnabled = !installing && !native.IsChecking && canInstall();
        progressArea.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        var detail = native.InstallProgress;
        progress.IsIndeterminate = detail?.Percent == null;
        progress.Value = detail?.Percent ?? 0;
        var message = localError ?? native.UpdateError;
        if (installing) {
            message = chinese ? native.InstallStatus ?? "准备下载更新……" : detail?.Stage switch {
                UpdateInstallStage.Downloading => detail.Percent is { } percent ? $"Downloading update {percent:0}%" : "Downloading update…",
                UpdateInstallStage.Verifying => "Verifying the update package…",
                UpdateInstallStage.Restarting => "Preparing installation and restart…",
                _ => "Preparing the update…"
            };
        }
        status.Text = message ?? ""; status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        status.SetResourceReference(TextBlock.ForegroundProperty, !installing && message != null ? "Danger" : "Muted");
    }
    private async Task InstallAsync()
    {
        if (startingInstall || native.IsInstalling || native.IsChecking) return;
        if (!canInstall()) { localError = L("请等待当前账号操作完成后再更新。", "Wait for the current account operation before updating."); RefreshState(); return; }
        startingInstall = true; localError = null; Choice = UpdatePromptChoice.UpdateNow; RefreshState();
        try { await native.InstallUpdateAsync(); }
        finally { startingInstall = false; RefreshState(); }
    }
}
