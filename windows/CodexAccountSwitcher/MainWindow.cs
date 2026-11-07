using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class MainWindow : Window
{
    private readonly IAccountClient client;
    private readonly NativeSettings native;
    private readonly Func<DateTimeOffset> now;
    private readonly DispatcherTimer presentationTimer;
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Grid surface = new();
    private readonly Border updateOverlay = new() { Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), Visibility = Visibility.Collapsed };
    private UpdateAvailableWindow? updateDialog;
    private bool updateEvaluationQueued;
    private string page = "accounts";
    private string? renderedPage;
    private AccountSnapshot? renderedState;
    private double? pendingScrollOffset;
    private long renderGeneration;
    private AccountRow? target;
    private string? localError;
    private string confirmationReturnPage = "accounts";
    private int renderedLayout;
    private AdaptiveCardGrid? accountGrid;
    private RefreshRotation? refreshRotation;
    private RefreshRotation? resetRotation;
    private QuotaRefreshSelector? quotaRefreshSelector;
    private bool layoutPending, closingForExit;
    public bool IsBusy => refreshRotation != null || resetRotation != null || client.State.IsMutating || native.IsInstalling;
    public string CurrentPage => page;
    public ScrollViewer PageScrollViewer => scroll;
    private double LayoutWidth => ActualWidth > 0 ? ActualWidth : Width;
    public int AccountColumnCount => accountGrid?.Columns ?? 1;
    private int LayoutKey => LayoutWidth < 720 ? 1 : 0;
    private AccountSnapshot State => client.State;
    private bool Chinese => WindowsLocalization.IsChinese(State.Settings.Language);
    private string L(string chinese, string english) => Chinese ? chinese : english;
    private string T(string key) {
        if (Chinese && key == "auto_reset") return "到期前自动使用重置卡";
        if (Chinese && key == "auto_reset_hint") return "重置卡到期前 10 分钟，自动使用所有已保存账号的可用重置卡；应用须运行且电脑保持唤醒。";
        return key switch {
            "accounts" => L("账号", "Accounts"), "settings" => L("设置", "Settings"), "back" => L("返回", "Back"),
            "cancel" => L("取消", "Cancel"), "remove" => L("删除", "Delete"), "switch" => L("切换账号", "Switch account"),
            "add_account" => L("添加账号", "Add account"), "cancel_add_account" => L("取消添加", "Cancel sign-in"),
            "register_current_account" => L("获取本地账号", "Get local account"),
            "unknown" => L("未知", "Unknown"), "usage_unavailable" => L("暂时无法获取额度", "Quota unavailable"),
            "no_accounts" => L("还没有账号，可先导入或添加账号。", "No accounts yet. Import or add an account."),
            "active_unconfirmed" => L("当前登录尚未确认，请检查账号状态。", "Current login is not confirmed."),
            "sign_in_pending_hint" => L("请在浏览器完成登录，完成后账号会显示在这里。", "Finish signing in in your browser; the account will appear here."),
            "status_bar_usage_window" => L("托盘显示额度", "Tray quota window"),
            "five_hour" => L("5 小时", "5 hours"), "weekly" => L("7 天", "7 days"),
            "launch_at_login" => L("开机启动", "Launch at login"), "language" => L("语言", "Language"),
            "system_default" => L("跟随系统", "System"), "english" => L("英语", "English"), "simplified_chinese" => "简体中文",
            "auto_reset" => L("自动重置额度", "Automatically reset quota"),
            "auto_reset_hint" => L("启用后由账号核心在满足条件时使用重置卡。", "The account core uses reset credits when its automatic reset conditions are met."),
            "open_reset_log" => L("打开重置日志目录", "Open reset log folder"),
            "automatically_check_updates" => L("自动检查更新", "Automatically check for updates"),
            "check_for_updates" => L("检查更新", "Check for updates"),
            "current_version" => L("当前版本：%@", "Current version: %@"),
            _ => State.Text(key)
        };
    }

    public MainWindow(IAccountClient client, NativeSettings? native = null, Func<DateTimeOffset>? now = null)
    {
        this.client = client; this.native = native ?? new NativeSettings(); this.now = now ?? (() => DateTimeOffset.Now);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Title = App.DisplayName;
        Width = Math.Min(1100, SystemParameters.WorkArea.Width - 24); Height = Math.Min(900, SystemParameters.WorkArea.Height - 24);
        MinWidth = 480; MinHeight = 440; SizeToContent = SizeToContent.Manual;
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "Surface"); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        scroll.SetResourceReference(BackgroundProperty, "Surface");
        surface.Children.Add(scroll); surface.Children.Add(updateOverlay); Content = surface;
        AutomationProperties.SetName(updateOverlay, "更新遮罩");
        if (Application.Current is App application) application.UseThemeSettings(this.native);
        SizeChanged += (_, _) => {
            if (LayoutKey == renderedLayout || layoutPending) return;
            layoutPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { layoutPending = false; if (LayoutKey != renderedLayout) Render(); }));
        };
        presentationTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        presentationTimer.Tick += (_, _) => { if (IsVisible && (page is "accounts" or "resetQuota") && !IsBusy) Render(); }; presentationTimer.Start();
        client.Changed += OnClientChanged; this.native.Changed += Render;
        Activated += (_, _) => EvaluateUpdatePrompt();
        IsVisibleChanged += (_, _) => EvaluateUpdatePrompt();
        StateChanged += (_, _) => EvaluateUpdatePrompt();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !IsBusy && page != "accounts") { Navigate("accounts"); e.Handled = true; } };
        Closing += (_, e) => { if (!closingForExit) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => {
            presentationTimer.Stop(); client.Changed -= OnClientChanged; this.native.Changed -= Render;
            refreshRotation?.Dispose();
            resetRotation?.Dispose();
        };
        Render();
    }
    public void CloseForExit() { closingForExit = true; updateDialog?.CloseForShutdown(); Close(); }
    public void OpenWindow() {
        if (State.IsAddingAccount) Navigate("accounts");
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); _ = Run("refresh");
    }
    public void Navigate(string destination) { page = destination == "manage" ? "accounts" : destination; target = null; localError = null; Render(); }
    private async Task Run(string command, Guid? id = null, bool? value = null, string? language = null) {
        if (command == "resetQuota") {
            if (IsBusy || State.IsAddingAccount || closingForExit || id == null ||
                State.Accounts.FirstOrDefault(row => row.Profile.Id == id)?.Usage?.ResetCreditsRemaining is not > 0) return;
            // The request includes the quota/credit readback. Keep the confirmation
            // busy until all of it completes, including before the host acknowledges it.
            var rotation = new RefreshRotation(id);
            resetRotation = rotation; localError = null;
            Render();
            try { await client.CommandAsync(command, id, value, language); }
            catch (Exception ex) { localError = ex.Message; }
            finally {
                rotation.Dispose();
                if (ReferenceEquals(resetRotation, rotation)) resetRotation = null;
                // Some backend failures are reported in State.Error without throwing.
                // Leave their exact outcome visible; never automatically retry a reset.
                if (localError == null && State.Error == null && page == "resetQuota" && target?.Profile.Id == id)
                    Navigate(confirmationReturnPage);
                else Render();
            }
            return;
        }
        if (command == "refresh") {
            if (IsBusy || State.IsAddingAccount || closingForExit) return;
            // Keep one animation clock for the entire awaited request. Re-rendered icons
            // use the same transform, so progress notifications do not restart the spin.
            var rotation = new RefreshRotation(id);
            refreshRotation = rotation; localError = null;
            Render();
            try { await client.CommandAsync(command, id, value, language); }
            catch (Exception ex) { localError = ex.Message; }
            finally {
                rotation.Dispose();
                if (ReferenceEquals(refreshRotation, rotation)) refreshRotation = null;
                Render();
            }
            return;
        }
        try { await client.CommandAsync(command, id, value, language); }
        catch (Exception ex) { localError = ex.Message; Render(); }
    }
    private TextBlock Text(string text, double size = 16, bool muted = false, bool bold = false) {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
    private TextBlock IconText(string glyph, double size = 18) {
        var icon = Text(glyph, size); icon.FontFamily = new FontFamily("Segoe MDL2 Assets"); return icon;
    }
    private Button Button(string label, Action action, string? glyph = null) {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (glyph != null) { var icon = IconText(glyph, 17); icon.Margin = new Thickness(0, 0, label.Length > 0 ? 8 : 0, 0); content.Children.Add(icon); }
        if (label.Length > 0) content.Children.Add(Text(label, 15));
        foreach (var child in content.Children.OfType<TextBlock>())
            child.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button { Content = content, Style = (Style)FindResource("ActionButton"), IsEnabled = !IsBusy, MinWidth = 36 };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    private void Primary(Button button) {
        button.SetResourceReference(Control.BackgroundProperty, "Accent"); button.SetResourceReference(Control.BorderBrushProperty, "Accent");
        button.SetResourceReference(Control.ForegroundProperty, "OnAccent");
    }
    private Button RefreshButton(string label, Guid? accountId = null) {
        var button = Button(label, () => _ = Run("refresh", accountId), "\uE72C");
        button.IsEnabled &= !State.IsAddingAccount;
        var icon = ((StackPanel)button.Content).Children.OfType<TextBlock>().First();
        icon.RenderTransformOrigin = new Point(.5, .5);
        icon.RenderTransform = refreshRotation?.Includes(accountId) == true ? refreshRotation.Transform : new RotateTransform();
        return button;
    }
    private Border Rule() { var rule = new Border { Height = 1 }; rule.SetResourceReference(Border.BackgroundProperty, "Line"); return rule; }
    private Border Box(UIElement child, string background = "ListSurface", double radius = 12) {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, background); border.SetResourceReference(Border.BorderBrushProperty, "Line"); return border;
    }
    private Grid Pair(UIElement left, UIElement right) {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1); grid.Children.Add(left); grid.Children.Add(right); return grid;
    }
    private void StartConfirmation(AccountRow row, string destination) {
        if (IsBusy || State.IsAddingAccount) return;
        if (destination == "switch" && QuotaReadException.IsAuthenticationFailure(State.Accounts.FirstOrDefault(account => account.Profile.Id == row.Profile.Id)?.UsageError)) return;
        confirmationReturnPage = "accounts"; target = row; page = destination; Render();
    }
    private void OnClientChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(OnClientChanged)); return; }
        updateDialog?.RefreshState();
        var latest = State;
        if (renderedState is { } displayed && displayed with { Accounts = latest.Accounts, Strings = latest.Strings, UsageRefreshRevision = latest.UsageRefreshRevision } == latest &&
            displayed.Accounts.SequenceEqual(latest.Accounts) && displayed.Strings.Count == latest.Strings.Count && displayed.Strings.All(item => latest.Strings.TryGetValue(item.Key, out var translation) && translation == item.Value)) {
            // Revision-only background notifications do not replace focused controls or flash the list.
            renderedState = latest; return;
        }
        if (page == "settings" && renderedState is { } previous &&
            previous.Settings with { QuotaRefreshIntervalMinutes = State.Settings.QuotaRefreshIntervalMinutes } == State.Settings &&
            previous.IsMutating == State.IsMutating && previous.IsAddingAccount == State.IsAddingAccount && previous.Error == State.Error) {
            // Saving on blur must not remove the control that is receiving focus.
            // This setting has its own stable editor and can update in place.
            if (previous.Settings.QuotaRefreshIntervalMinutes != State.Settings.QuotaRefreshIntervalMinutes)
                quotaRefreshSelector?.UpdateState(State.Settings.QuotaRefreshIntervalMinutes, Chinese, !IsBusy && !State.IsAddingAccount);
            renderedState = State; return;
        }
        Render();
    }

    private void Render()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Render)); return; }
        if (closingForExit) return;
        updateDialog?.RefreshState();
        if (target != null) {
            target = State.Accounts.FirstOrDefault(row => row.Profile.Id == target.Profile.Id);
            if (target == null) page = "accounts";
        }
        var offset = renderedPage == page ? pendingScrollOffset ?? scroll.VerticalOffset : 0;
        if (page == "settings") quotaRefreshSelector?.PrepareForReparent();
        pendingScrollOffset = offset; renderedPage = page; renderedState = State;
        var generation = ++renderGeneration;
        renderedLayout = LayoutKey;
        var body = new StackPanel { Margin = new Thickness(26, 0, 26, 24) };
        if (State.Error != null || localError != null) {
            var message = Text(WindowsLocalization.Error(localError ?? State.Error!, State.Settings.Language), 14); message.TextWrapping = TextWrapping.Wrap; message.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            message.ToolTip = WindowsLocalization.TechnicalDetails(localError ?? State.Error, State.Settings.Language);
            var dismiss = Button("", () => { localError = null; _ = Run("dismissError"); Render(); }, "\uE711"); AutomationProperties.SetName(dismiss, L("关闭错误提示", "Dismiss error"));
            var alert = Box(Pair(message, dismiss), "WarningSurface"); alert.Padding = new Thickness(14); alert.Margin = new Thickness(0, 16, 0, 0); body.Children.Add(alert);
        }
        if (page == "accounts") {
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Text(T("accounts"), 28, bold: true));
            var count = Text(L($"{State.Accounts.Length} 个账号", $"{State.Accounts.Length} accounts"), 16, muted: true); count.Margin = new Thickness(20, 0, 0, 0); title.Children.Add(count);
            var commands = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var add = Button(T(State.IsAddingAccount ? "cancel_add_account" : "add_account"), OpenAddAccount, State.IsAddingAccount ? "\uE711" : "\uE710"); Primary(add); commands.Children.Add(add);
            if (client is IAccountImporter) {
                var import = Button(L("导入", "Import"), OpenJsonImport, "\uE896"); import.IsEnabled &= !State.IsAddingAccount; import.Margin = new Thickness(10, 0, 0, 0);
                AutomationProperties.SetName(import, L("粘贴 JSON", "Paste JSON")); commands.Children.Add(import);
            }
            var refresh = RefreshButton(L("刷新", "Refresh")); refresh.Margin = new Thickness(10, 0, 0, 0); commands.Children.Add(refresh);
            var settings = Button(T("settings"), () => Navigate("settings"), "\uE713"); settings.Margin = new Thickness(10, 0, 0, 0);
            if (native.AvailableUpdate != null) settings.ToolTip = L("有新版本可用", "An update is available"); commands.Children.Add(settings);
            FrameworkElement header;
            if (LayoutWidth < 720) {
                var narrow = new StackPanel(); narrow.Children.Add(title); commands.HorizontalAlignment = HorizontalAlignment.Left;
                commands.Margin = new Thickness(0, 18, 0, 0); narrow.Children.Add(commands); header = narrow;
            } else header = Pair(title, commands);
            header.Margin = new Thickness(0, 26, 0, 24); body.Children.Add(header);
            if (State.IsAddingAccount) { var pending = Text(T("sign_in_pending_hint"), 14, muted: true); pending.TextWrapping = TextWrapping.Wrap; pending.Margin = new Thickness(0, 0, 0, 16); body.Children.Add(pending); }
            if (native.ShouldShowUpdateBanner || native.IsInstalling) {
                var notice = Text(native.InstallStatus ?? WindowsLocalization.Error(native.UpdateError, State.Settings.Language) ?? L($"发现新版本 {NativeSettings.FormatVersion(native.AvailableUpdate!.Version)}", $"Update {NativeSettings.FormatVersion(native.AvailableUpdate!.Version)} available"), 14); notice.TextWrapping = TextWrapping.Wrap;
                notice.ToolTip = WindowsLocalization.TechnicalDetails(native.UpdateDiagnostic ?? native.UpdateError, State.Settings.Language);
                var update = Button(L("更新并重启", "Update & restart"), native.RequestUpdatePrompt); Primary(update); update.IsEnabled &= !State.IsAddingAccount;
                var banner = Box(Pair(notice, update)); banner.Padding = new Thickness(16); banner.Margin = new Thickness(0, 0, 0, 18); body.Children.Add(banner);
            }
            Accounts(body);
        } else {
            var back = Button("", () => Navigate(page is "remove" or "resetQuota" ? confirmationReturnPage : "accounts"), "\uE72B"); AutomationProperties.SetName(back, T("back"));
            var heading = Text(page switch { "settings" => T("settings"), "resetQuota" => L("重置账号额度", "Reset account quota"), "remove" => L("删除账号", "Delete account"), _ => L("切换账号", "Switch account") }, 26, bold: true);
            heading.Margin = new Thickness(14, 0, 0, 0);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 26, 0, 24) }; header.Children.Add(back); header.Children.Add(heading); body.Children.Add(header);
            if (page == "settings") Settings(body); else Confirmation(body);
        }
        scroll.Content = body;
        scroll.ScrollToVerticalOffset(offset);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            if (generation != renderGeneration) return;
            scroll.UpdateLayout(); quotaRefreshSelector?.FinishReparent(page == "settings");
            scroll.ScrollToVerticalOffset(offset); pendingScrollOffset = null;
        }));
        EvaluateUpdatePrompt();
    }
    public void EvaluateUpdatePrompt()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(EvaluateUpdatePrompt)); return; }
        if (closingForExit || Dispatcher.HasShutdownStarted || updateEvaluationQueued) return;
        updateEvaluationQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
            updateEvaluationQueued = false;
            if (closingForExit || updateDialog != null || !IsVisible || !IsActive || !IsEnabled || WindowState == WindowState.Minimized ||
                IsBusy || State.IsAddingAccount || native.IsChecking || (page != "accounts" && page != "settings") ||
                OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return;
            var prompt = native.BeginUpdatePrompt(allowAutomatic: page == "accounts");
            if (prompt == null) return;
            var dialog = new UpdateAvailableWindow(native, prompt, Chinese, () => !IsBusy && !State.IsAddingAccount) { Owner = this };
            updateDialog = dialog; updateOverlay.Visibility = Visibility.Visible;
            try { dialog.ShowDialog(); }
            finally {
                updateDialog = null; updateOverlay.Visibility = Visibility.Collapsed;
                native.CompleteUpdatePrompt(prompt, dialog.Choice);
                EvaluateUpdatePrompt();
            }
        }));
    }
    private void Accounts(StackPanel body)
    {
        if (!State.ActiveIdentityConfirmed) { var warning = Text(T("active_unconfirmed"), 14, muted: true); warning.TextWrapping = TextWrapping.Wrap; warning.Margin = new Thickness(0, 0, 0, 16); body.Children.Add(warning); }
        var list = new AdaptiveCardGrid { Margin = new Thickness(-8, -8, -8, 0) };
        accountGrid = list;
        var activeAccountId = State.ActiveAccountID;
        foreach (var row in State.Accounts.OrderByDescending(row => row.Profile.Id == activeAccountId)) list.Children.Add(AccountItem(row));
        if (State.Accounts.Length == 0) {
            var empty = Text(T("no_accounts"), 17, muted: true); empty.Margin = new Thickness(24, 54, 24, 54); empty.TextWrapping = TextWrapping.Wrap; body.Children.Add(Box(empty));
        } else body.Children.Add(list);
    }
    private void OpenAddAccount() => OpenAddAccount(false);
    private void OpenAddAccount(bool recovery, Guid? recoveryID = null)
    {
        if (IsBusy) return;
        if (State.IsAddingAccount) { _ = Run("cancelAdd"); return; }
        var choice = new AddAccountWindow(Chinese, client is IAccountImporter, async () => {
            await client.CommandAsync("dismissError");
            await client.CommandAsync("register");
            if (State.Error is { } failure) throw new System.IO.IOException(failure);
            if (!State.ActiveIdentityConfirmed || State.ActiveAccountID == null)
                throw new System.IO.IOException(L("未读取到本地登录账号，请先在 Codex 中完成登录后重试。", "No local signed-in account was found. Sign in to Codex and retry."));
        }, recovery) { Owner = this };
        if (choice.ShowDialog() != true || IsBusy || State.IsAddingAccount) return;
        if (choice.SelectedMethod == AddAccountMethod.Browser) _ = Run("add", recoveryID);
        else if (choice.SelectedMethod == AddAccountMethod.Json) OpenJsonImport();
    }
    private void OpenJsonImport() {
        if (client is IAccountImporter importer && !IsBusy && !State.IsAddingAccount) new JsonImportWindow(importer) { Owner = this }.ShowDialog();
    }

    private FrameworkElement AccountHeader(AccountRow row, bool current)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var initials = Text(row.Initials, 19, bold: true); initials.HorizontalAlignment = HorizontalAlignment.Center;
        var avatar = new Border { Width = 40, Height = 42, CornerRadius = new CornerRadius(8), Child = initials, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        avatar.SetResourceReference(Border.BackgroundProperty, "Hover"); grid.Children.Add(avatar);
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = row.Profile.Email ?? row.Profile.DisplayName;
        var name = Text(title, 18, bold: true); name.ToolTip = title; details.Children.Add(name);
        if (current) { var active = Text(L("当前使用", "Current"), 13, bold: true); active.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk"); active.Margin = new Thickness(0, 4, 0, 0); details.Children.Add(active); }
        Grid.SetColumn(details, 1); grid.Children.Add(details);
        var plan = AccountVisuals.PlanBadge(row.Usage?.PlanType ?? row.PlanType, chinese: Chinese);
        var badgeText = Text(plan.Label, 12, bold: true); badgeText.SetResourceReference(TextBlock.ForegroundProperty, "PlanForeground_" + plan.Key);
        var badgeInside = new Grid(); badgeText.Margin = new Thickness(10, 3, 10, 3); badgeInside.Children.Add(badgeText);
        var sheen = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(9, 1, 9, 0), Background = new SolidColorBrush(Color.FromArgb(105, 255, 255, 255)), IsHitTestVisible = false };
        badgeInside.Children.Add(sheen);
        var badge = new Border { Child = badgeInside, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 2, 0, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "PlanBackground_" + plan.Key); badge.SetResourceReference(Border.BorderBrushProperty, "PlanBorder_" + plan.Key);
        AutomationProperties.SetName(badge, L("套餐 ", "Plan ") + plan.Label); Grid.SetColumn(badge, 2); grid.Children.Add(badge);
        return grid;
    }
    private FrameworkElement AccountItem(AccountRow row)
    {
        var active = row.Profile.Id == State.ActiveAccountID;
        var card = new Grid { Margin = new Thickness(20) };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 138 });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Children.Add(AccountHeader(row, active));
        var quotas = new StackPanel { Margin = new Thickness(0, 14, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        var cachedQuota = !string.IsNullOrWhiteSpace(row.UsageError);
        if (row.Usage is { } usage) {
            if (usage.FiveHourRemainingPercent.HasValue) quotas.Children.Add(UsageLine(L("5 小时剩余", "5 hours left"), usage.FiveHourRemainingPercent, usage.FiveHourResetsAt, false, cachedQuota));
            quotas.Children.Add(UsageLine(L("7 天剩余", "7 days left"), usage.RemainingPercent, usage.ResetsAt, true, cachedQuota));
        } else {
            var missing = Text(row.UsageStatus == "idle" && string.IsNullOrWhiteSpace(row.UsageError) ? L("额度读取中…", "Loading quota…") : T("usage_unavailable"), 15, muted: true);
            missing.ToolTip = WindowsLocalization.Error(row.UsageError, State.Settings.Language); missing.TextWrapping = TextWrapping.Wrap; quotas.Children.Add(missing);
        }
        if (!string.IsNullOrWhiteSpace(row.UsageError)) {
            var expiredLogin = QuotaReadException.IsAuthenticationFailure(row.UsageError);
            var tone = expiredLogin ? "Danger" : "Warning";
            var warning = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            warning.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            warning.ColumnDefinitions.Add(new ColumnDefinition());
            warning.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var symbol = new System.Windows.Shapes.Path {
                Data = Geometry.Parse("M8,1 A7,7 0 1 1 7.999,1 M8,4 L8,8 M8,11 L8,11.1"),
                Width = 16, Height = 16, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
            };
            symbol.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, tone); warning.Children.Add(symbol);
            var fullReason = WindowsLocalization.Error(row.UsageError, State.Settings.Language);
            var summary = row.UsageError == QuotaReadException.AuthorizationRevoked
                ? L("登录授权已被服务端撤销，无法自动刷新。", "Sign-in authorization was revoked by the server and cannot refresh automatically.")
                : expiredLogin ? L("登录授权已失效，请重新授权。", "Sign-in authorization is no longer valid. Authorize again.") : fullReason;
            var reason = Text(summary, 13, bold: expiredLogin);
            reason.SetResourceReference(TextBlock.ForegroundProperty, tone);
            reason.ToolTip = fullReason; reason.TextWrapping = TextWrapping.Wrap; reason.LineHeight = 18;
            reason.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; reason.MaxHeight = 36;
            AutomationProperties.SetName(reason, L("额度刷新失败原因", "Quota refresh error"));
            AutomationProperties.SetHelpText(reason, fullReason); Grid.SetColumn(reason, 1); warning.Children.Add(reason);
            if (expiredLogin) {
                var authorize = Button(L("重新授权", "Authorize again"), () => OpenAddAccount(true, row.Profile.Id));
                authorize.Padding = new Thickness(8, 4, 8, 4); authorize.MinHeight = 28;
                foreach (var label in ((StackPanel)authorize.Content).Children.OfType<TextBlock>()) label.FontSize = 13;
                authorize.Margin = new Thickness(10, 0, 0, 0); authorize.VerticalAlignment = VerticalAlignment.Center;
                authorize.IsEnabled &= !State.IsAddingAccount; Grid.SetColumn(authorize, 2); warning.Children.Add(authorize);
            }
            var alert = Box(warning, expiredLogin ? "DangerSurface" : "WarningSurface", 9);
            alert.SetResourceReference(Border.BorderBrushProperty, tone); alert.Margin = new Thickness(0, 8, 0, 4);
            AutomationProperties.SetName(alert, expiredLogin ? L("登录失效提示", "Expired sign-in alert") : L("刷新失败提示", "Refresh failure alert")); quotas.Children.Add(alert);
        }
        Grid.SetRow(quotas, 1); card.Children.Add(quotas);
        var metadata = new StackPanel(); metadata.Children.Add(Rule());
        var expiry = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var expiryIcon = IconText("\uE787", 16); expiryIcon.Margin = new Thickness(0, 0, 10, 0); expiry.Children.Add(expiryIcon);
        if (row.Subscription is { } subscription) {
            var currentTime = now();
            var currentLoginExpiry = row.SubscriptionStatus == "login_snapshot" && subscription.Source == "login_snapshot" &&
                row.SubscriptionError == null && subscription.ActiveUntil > currentTime;
            var needsVerification = !currentLoginExpiry && (!subscription.IsVerified || row.SubscriptionStatus is "checking" or "stale" or "unverified");
            var expiredSnapshot = needsVerification && subscription.ActiveUntil <= currentTime;
            var date = Text(expiredSnapshot ? L("有效期：待核验", "Expiry: verification needed") : L("有效期：", "Expires: ") + subscription.ActiveUntil.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), 14, muted: true);
            date.ToolTip = subscription.ActiveUntil.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "\n" +
                (currentLoginExpiry ? L("来自当前登录信息，未进行在线核验。\n本次读取时间：", "From the current sign-in; not verified online.\nRead at: ") :
                    subscription.IsVerified ? L("订阅查询时间：", "Subscription checked: ") : L("登录信息时间：", "Login snapshot time: ")) +
                subscription.LastChecked.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + (row.SubscriptionError == null ? "" : "\n" + WindowsLocalization.Error(row.SubscriptionError, State.Settings.Language));
            expiry.Children.Add(date);
            if (!expiredSnapshot) {
                var remaining = Text(" · " + (needsVerification ? L("待核验", "Unverified") : AccountVisuals.ExpiryRemaining(subscription.ActiveUntil, currentTime, Chinese)), 14, bold: !needsVerification);
                remaining.SetResourceReference(TextBlock.ForegroundProperty, needsVerification ? "Muted" : AccountVisuals.ExpiryTone(subscription.ActiveUntil, currentTime)); expiry.Children.Add(remaining);
            }
        } else {
            var missing = Text(row.SubscriptionStatus switch {
                "checking" => L("有效期：核验中…", "Expiry: checking…"),
                "stale" or "unavailable" => L("有效期：待核验", "Expiry: verification needed"),
                _ => L("有效期：未知", "Expiry: unknown")
            }, 14, muted: true);
            missing.ToolTip = WindowsLocalization.Error(row.SubscriptionError, State.Settings.Language); expiry.Children.Add(missing);
        }
        metadata.Children.Add(expiry);
        var creditLine = ResetCreditExpiryLine(row.Usage, compact: true); creditLine.Margin = new Thickness(0, 8, 0, 10); metadata.Children.Add(creditLine);
        metadata.Children.Add(Rule()); Grid.SetRow(metadata, 2); card.Children.Add(metadata);
        var actions = new AccountActionsPanel { Margin = new Thickness(0, 12, 0, 0) };
        var accountActions = new StackPanel { Orientation = Orientation.Horizontal };
        var utilityActions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(accountActions); actions.Children.Add(utilityActions);
        var change = Button(active ? L("使用中", "In use") : L("切换", "Switch"), () => { if (!active) StartConfirmation(row, "switch"); }, active ? "\uE73E" : "\uE8AB");
        var authenticationFailed = QuotaReadException.IsAuthenticationFailure(row.UsageError);
        change.MinWidth = 86; change.IsEnabled &= !active && !State.IsAddingAccount && !authenticationFailed; if (!active) Primary(change);
        AutomationProperties.SetName(change, row.Profile.Email ?? row.Profile.DisplayName);
        change.ToolTip = active ? L("当前使用的账号", "Current account") : authenticationFailed ? L("登录已失效，请先重新授权后再切换", "Sign-in expired; authorize again before switching") : L("确认后切换此账号", "Confirm before switching to this account"); accountActions.Children.Add(change);
        var count = row.Usage?.ResetCreditsRemaining?.ToString(CultureInfo.CurrentCulture) ?? T("unknown");
        var resetting = resetRotation?.Includes(row.Profile.Id) == true;
        var reset = Button(resetting ? L("重置中…", "Resetting…") : L("重置 " + count + " 次", "Reset " + count), () => { if (row.Usage?.ResetCreditsRemaining > 0) StartConfirmation(row, "resetQuota"); }, "\uE777");
        if (resetting) AttachResetRotation(reset);
        reset.IsEnabled &= !State.IsAddingAccount && row.Usage?.ResetCreditsRemaining > 0; reset.Margin = new Thickness(10, 0, 0, 0);
        reset.ToolTip = row.Usage?.ResetCreditsRemaining switch { null => L("可用重置次数未知，请先刷新。", "Reset credits are unknown; refresh first."), <= 0 => L("没有可用重置次数。", "No reset credits available."), _ => L("确认后消耗 1 张重置卡。", "Confirm to use one reset credit.") };
        AutomationProperties.SetName(reset, L("重置额度", "Reset quota")); accountActions.Children.Add(reset);
        var refresh = RefreshButton("", row.Profile.Id);
        refresh.ToolTip = L("刷新此账号", "Refresh this account"); AutomationProperties.SetName(refresh, L("刷新此账号", "Refresh this account")); utilityActions.Children.Add(refresh);
        var remove = Button("", () => StartConfirmation(row, "remove"), "\uE74D"); remove.Margin = new Thickness(8, 0, 0, 0);
        remove.IsEnabled &= !State.IsAddingAccount; remove.ToolTip = L("删除此账号", "Delete this account");
        AutomationProperties.SetName(remove, T("remove")); utilityActions.Children.Add(remove);
        Grid.SetRow(actions, 3); card.Children.Add(actions);
        var border = Box(card, active ? "Selected" : "ListSurface", 12); border.Margin = new Thickness(8); border.MinHeight = 350;
        if (active) border.SetResourceReference(Border.BorderBrushProperty, "AccentBorder");
        return border;
    }
    private Grid UsageLine(string title, int? percent, DateTimeOffset? reset = null, bool weekly = false, bool cached = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (var index = 0; index < 3; index++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = Text(title, 15);
        var caption = new StackPanel { Orientation = Orientation.Horizontal }; caption.Children.Add(label);
        if (cached) {
            var stale = Text(L("上次额度", "Cached"), 11, muted: true); stale.Margin = new Thickness(8, 0, 0, 0);
            stale.ToolTip = L("显示上次额度，仅供参考", "Showing the last known quota for reference"); caption.Children.Add(stale);
        }
        var remaining = Text(percent.HasValue ? percent + "%" : "—", 17, bold: true); remaining.SetResourceReference(TextBlock.ForegroundProperty, AccountVisuals.QuotaTone(percent));
        remaining.HorizontalAlignment = HorizontalAlignment.Right; grid.Children.Add(Pair(caption, remaining));
        var track = new Grid { Height = 6, Margin = new Thickness(0, 5, 0, 5) };
        var rail = new Border { CornerRadius = new CornerRadius(3) }; rail.SetResourceReference(Border.BackgroundProperty, "Track"); track.Children.Add(rail);
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, AccountVisuals.QuotaTone(percent));
        track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * Math.Clamp(percent ?? 0, 0, 100) / 100.0; track.Children.Add(fill);
        AutomationProperties.SetName(track, title + " " + (percent?.ToString() ?? T("unknown")) + (percent.HasValue ? "%" : ""));
        Grid.SetRow(track, 1); grid.Children.Add(track);
        var scheduleText = L("恢复时间未知", "Renewal time unknown");
        string? scheduleTip = null;
        if (reset is { } time) {
            var currentTime = now();
            var localTime = time.ToLocalTime();
            var when = !weekly && localTime.Date == currentTime.ToLocalTime().Date ? L("今天 ", "Today ") + localTime.ToString("HH:mm") : localTime.ToString("MM-dd HH:mm");
            scheduleText = when + L(" 恢复（", " renews (") + AccountVisuals.QuotaCountdown(time, currentTime, Chinese) + L("）", ")");
            scheduleTip = localTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        var schedule = Text(scheduleText, 14, bold: true); schedule.SetResourceReference(TextBlock.ForegroundProperty, "Schedule");
        schedule.TextWrapping = TextWrapping.Wrap; schedule.TextTrimming = TextTrimming.None;
        schedule.ToolTip = scheduleTip; Grid.SetRow(schedule, 2); grid.Children.Add(schedule);
        return grid;
    }
    private Grid ResetCreditExpiryLine(Usage? usage, bool compact = false) {
        var currentTime = now();
        var expiry = usage?.ResetCreditsExpireAt;
        var tone = AccountVisuals.ResetExpiryTone(expiry, currentTime);
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); line.ColumnDefinitions.Add(new ColumnDefinition());
        AutomationProperties.SetName(line, L("重置卡到期", "Reset credit expiry"));
        var icon = IconText("\uE777", 16); icon.SetResourceReference(TextBlock.ForegroundProperty, tone); line.Children.Add(icon);
        var label = Text(AccountVisuals.ResetExpiryLabel(expiry, currentTime, Chinese, compact), 14, bold: tone is "Warning" or "Danger");
        label.SetResourceReference(TextBlock.ForegroundProperty, tone);
        label.TextWrapping = TextWrapping.Wrap; label.TextTrimming = TextTrimming.None;
        if (expiry is { } time) label.ToolTip = time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Grid.SetColumn(label, 1); line.Children.Add(label);
        return line;
    }
    private string ResetSchedule(Usage usage) => (usage.FiveHourRemainingPercent.HasValue
        ? L("5 小时 ", "5h ") + (usage.FiveHourResetsAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? T("unknown")) + "  ·  " : "") +
        L("7 天 ", "7d ") + usage.ResetsAt.ToLocalTime().ToString("MM-dd HH:mm");
    private void Confirmation(StackPanel body)
    {
        if (target == null) return;
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(AccountHeader(target, target.Profile.Id == State.ActiveAccountID));
        if (page == "resetQuota" && target.Usage is { } usage) {
            var quotas = new StackPanel { Margin = new Thickness(0, 22, 0, 12) };
            if (usage.FiveHourRemainingPercent.HasValue) quotas.Children.Add(UsageLine(L("5 小时剩余", "5 hours left"), usage.FiveHourRemainingPercent, usage.FiveHourResetsAt));
            quotas.Children.Add(UsageLine(L("7 天剩余", "7 days left"), usage.RemainingPercent, usage.ResetsAt, true)); content.Children.Add(quotas);
            content.Children.Add(Rule());
            var credits = Text(L("可用重置次数：", "Available reset credits: ") + (usage.ResetCreditsRemaining?.ToString(CultureInfo.CurrentCulture) ?? T("unknown")), 16, bold: true);
            credits.Margin = new Thickness(0, 16, 0, 0); content.Children.Add(credits);
            var expiry = ResetCreditExpiryLine(usage); expiry.Margin = new Thickness(0, 10, 0, 0); content.Children.Add(expiry);
            var renewal = Text(L("额度自动恢复：", "Automatic quota renewal: ") + ResetSchedule(usage), 14, muted: true); renewal.TextWrapping = TextWrapping.Wrap;
            renewal.Margin = new Thickness(0, 8, 0, 0); content.Children.Add(renewal);
        }
        var copy = page switch {
            "switch" => L("先联网验证目标账号；凭据失效或网络异常时保留当前登录。验证通过后，将强制结束并重新打开 Codex。未保存的状态可能丢失，请先完成当前任务。", "The target login is checked before Codex closes. Invalid credentials or network errors preserve the current login. Codex then closes forcibly and restarts. Finish active tasks first; unsaved work may be lost."),
            "resetQuota" => L("本次操作将消耗 1 张重置卡。", "This uses one reset credit."),
            _ => L("仅从此工具删除保存的账号，不会退出 Codex 当前登录，也不会删除 ChatGPT 账号。以后可重新授权、导入或获取本地账号。此操作无法撤销。", "Remove only the saved login from this tool. Codex stays signed in and your ChatGPT account is not deleted. You can authorize, import, or get the local account again. This cannot be undone.")
        };
        var text = Text(copy, 15, bold: page == "resetQuota"); text.TextWrapping = TextWrapping.Wrap;
        var note = new StackPanel { Margin = new Thickness(16) }; note.Children.Add(text);
        if (page == "resetQuota") {
            text.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
            var detail = Text(L("额度与次数将在完成后刷新；不会切换当前登录。", "Quota and credits refresh afterwards. Your current login is unchanged."), 13, muted: true);
            detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 8, 0, 0); note.Children.Add(detail);
        }
        var notice = Box(note, page == "resetQuota" ? "WarningSurface" : "InputSurface", 9); notice.Margin = new Thickness(0, 22, 0, 22); content.Children.Add(notice);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(Button(T("cancel"), () => Navigate(confirmationReturnPage)));
        var command = page; var id = target.Profile.Id;
        var resetting = page == "resetQuota" && resetRotation?.Includes(id) == true;
        var confirm = Button(page == "resetQuota" ? resetting ? L("重置中…", "Resetting…") : L("确认重置", "Confirm reset") : T(command), () => {
            if (IsBusy || State.IsAddingAccount || command == "resetQuota" && State.Accounts.FirstOrDefault(row => row.Profile.Id == id)?.Usage?.ResetCreditsRemaining is not > 0 ||
                command == "switch" && QuotaReadException.IsAuthenticationFailure(State.Accounts.FirstOrDefault(row => row.Profile.Id == id)?.UsageError)) return;
            if (command == "resetQuota") _ = Run(command, id);
            else { Navigate(confirmationReturnPage); _ = Run(command, id); }
        }, resetting ? "\uE777" : null);
        if (resetting) AttachResetRotation(confirm);
        confirm.IsEnabled &= !State.IsAddingAccount && (page != "resetQuota" || target.Usage?.ResetCreditsRemaining > 0) &&
            (page != "switch" || !QuotaReadException.IsAuthenticationFailure(target.UsageError));
        if (page == "resetQuota") AutomationProperties.SetName(confirm, resetting ? L("重置中…", "Resetting…") : L("确认重置 · 消耗 1 张", "Confirm reset · use one credit"));
        confirm.Margin = new Thickness(10, 0, 0, 0); Primary(confirm); buttons.Children.Add(confirm); content.Children.Add(buttons);
        var box = Box(content); box.MaxWidth = 720; box.HorizontalAlignment = HorizontalAlignment.Stretch; body.Children.Add(box);
    }
    private void AttachResetRotation(Button button) {
        var icon = ((StackPanel)button.Content).Children.OfType<TextBlock>().First();
        icon.RenderTransformOrigin = new Point(.5, .5);
        icon.RenderTransform = resetRotation!.Transform;
    }

    private void Settings(StackPanel body)
    {
        var settings = new StackPanel { MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Stretch };
        StackPanel Section(string title) {
            var inner = new StackPanel(); var header = Text(title, 14, muted: true, bold: true); header.Margin = new Thickness(18, 10, 18, 10);
            var head = new Border { Child = header, CornerRadius = new CornerRadius(11, 11, 0, 0) }; head.SetResourceReference(Border.BackgroundProperty, "SectionHeader");
            inner.Children.Add(head); var rows = new StackPanel { Margin = new Thickness(18, 2, 18, 10) }; inner.Children.Add(rows);
            var section = Box(inner); section.Margin = new Thickness(0, 0, 0, 18); settings.Children.Add(section); return rows;
        }
        void Row(StackPanel section, string title, string glyph, UIElement control, string? hint = null, bool alignTop = false) {
            var label = new Grid(); label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); label.ColumnDefinitions.Add(new ColumnDefinition());
            if (alignTop) { label.VerticalAlignment = VerticalAlignment.Top; label.Margin = new Thickness(0, 9, 0, 0); }
            label.Children.Add(IconText(glyph, 18)); var text = Text(title, 16); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(text, 1); label.Children.Add(text); var row = Pair(label, control); row.MinHeight = 52; section.Children.Add(row);
            if (hint != null) { var description = Text(hint, 13, muted: true); description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(34, 0, 0, 10); section.Children.Add(description); }
        }
        CheckBox Toggle(string title, bool current, Action<bool> action) {
            var toggle = new CheckBox { IsChecked = current, IsEnabled = !IsBusy, Style = (Style)FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, title);
            toggle.Click += (_, _) => { try { action(toggle.IsChecked == true); } catch (Exception ex) { localError = ex.Message; Render(); } }; return toggle;
        }
        ComboBox Choice(string name, string[] items, int selected, Action<int> action) {
            var choice = new ComboBox { Width = 160, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !IsBusy, ItemsSource = items, SelectedIndex = selected };
            AutomationProperties.SetName(choice, name);
            choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex < 0) return; try { action(choice.SelectedIndex); } catch (Exception ex) { localError = ex.Message; Render(); } }; return choice;
        }
        var appearance = Section(L("外观", "Appearance")); var themeKeys = new[] { "dark", "light", "system" };
        Row(appearance, L("主题", "Theme"), "\uE790", Choice(L("主题", "Theme"), new[] { L("深色", "Dark"), L("浅色", "Light"), L("跟随系统", "System") },
            Array.IndexOf(themeKeys, native.ThemePreference), index => native.ThemePreference = themeKeys[index]), L("主题切换立即生效", "Theme changes apply immediately"));
        appearance.Children.Add(Rule());
        Row(appearance, L("主题色", "Accent color"), "\uE2B1", Text(L("即选即用", "Applies instantly"), 13, muted: true));
        var accents = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var (key, label) in new[] { ("blue", L("海洋蓝", "Ocean blue")), ("purple", L("暮光紫", "Twilight purple")),
            ("teal", L("青碧绿", "Jade teal")), ("orange", L("落日橙", "Sunset orange")) }) {
            var selected = native.AccentPreference == key;
            var choice = Button(label, () => native.AccentPreference = key);
            choice.Margin = new Thickness(0, 0, 10, 10); choice.Padding = new Thickness(12, 8, 12, 8);
            AutomationProperties.SetName(choice, L("主题色：", "Accent color: ") + label);
            AutomationProperties.SetHelpText(choice, selected ? L("已选中", "Selected") : L("选择此主题色", "Select this accent color"));
            var swatch = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Background = App.CreateAccentBrush(key), Margin = new Thickness(0, 0, 8, 0) };
            ((StackPanel)choice.Content).Children.Insert(0, swatch);
            if (selected) {
                choice.BorderThickness = new Thickness(2);
                choice.SetResourceReference(Control.BorderBrushProperty, "AccentBorder"); choice.SetResourceReference(Control.BackgroundProperty, "Selected");
                var checkmark = IconText("\uE73E", 12); checkmark.Margin = new Thickness(8, 0, 0, 0); ((StackPanel)choice.Content).Children.Add(checkmark);
            }
            accents.Children.Add(choice);
        }
        appearance.Children.Add(accents);
        var general = Section(L("常规与托盘", "General and tray"));
        Row(general, T("launch_at_login"), "\uE7E8", Toggle(T("launch_at_login"), native.LaunchAtLogin, value => native.LaunchAtLogin = value)); general.Children.Add(Rule());
        Row(general, L("托盘显示剩余百分比", "Show remaining quota in tray"), "\uE9D9",
            Toggle(L("托盘显示剩余百分比", "Show remaining quota in tray"), State.Settings.ShowsMenuBarPercentage, value => _ = Run("percentage", value: value)));
        var windows = new[] { "fiveHour", "weekly" };
        Row(general, T("status_bar_usage_window"), "\uE787", Choice(T("status_bar_usage_window"), new[] { T("five_hour"), T("weekly") },
            State.Settings.StatusBarUsageWindow == "fiveHour" ? 0 : 1, index => _ = Run("statusBarUsageWindow", language: windows[index])));
        var languages = new[] { "system", "english", "simplifiedChinese" };
        Row(general, T("language"), "\uE774", Choice(T("language"), new[] { T("system_default"), T("english"), T("simplified_chinese") },
            Math.Max(0, Array.IndexOf(languages, State.Settings.Language)), index => _ = Run("language", language: languages[index])));
        var trayHint = Text(L("关闭窗口后保留在托盘。账号支持 5 小时额度时会自动显示。", "Closing the window keeps the app in the tray. Five-hour quota appears when supported."), 13, muted: true);
        trayHint.TextWrapping = TextWrapping.Wrap; trayHint.Margin = new Thickness(0, 8, 0, 4); general.Children.Add(trayHint);
        var quotaRefresh = Section(L("额度刷新", "Quota refresh"));
        quotaRefreshSelector ??= new QuotaRefreshSelector(State.Settings.QuotaRefreshIntervalMinutes, Chinese, async minutes => {
            await client.CommandAsync("quotaRefreshInterval", language: minutes.ToString(CultureInfo.InvariantCulture));
            if (State.Settings.QuotaRefreshIntervalMinutes != minutes) throw new InvalidOperationException("The refresh interval was not confirmed by the account service.");
        });
        quotaRefreshSelector.UpdateState(State.Settings.QuotaRefreshIntervalMinutes, Chinese, !IsBusy && !State.IsAddingAccount);
        Row(quotaRefresh, L("刷新间隔", "Refresh interval"), "\uE72C", quotaRefreshSelector,
            L("禁用仅停止定时刷新，不影响手动刷新。应用驻留托盘时仍会定时刷新，电脑需保持唤醒。自定义范围为 1–1440 分钟，点击保存，也可按 Enter 或移开焦点保存。",
                "Disabling stops scheduled refreshes only; manual refresh remains available. Scheduled refreshes continue in the tray while the computer is awake. Custom: 1–1440 minutes; click Save, press Enter or move focus to save."), alignTop: true);
        var automation = Section(L("自动重置", "Automatic reset"));
        Row(automation, T("auto_reset"), "\uE777", Toggle(T("auto_reset"), State.Settings.AutoResetEnabled, value => _ = Run("autoReset", value: value)), T("auto_reset_hint"));
        var log = Button(T("open_reset_log"), () => {
            try {
                var directory = Environment.GetEnvironmentVariable("CODEX_SWITCHER_DATA_HOME") ?? App.DefaultDataDirectory;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            } catch (Exception ex) { localError = ex.Message; Render(); }
        }, "\uE8B7"); log.HorizontalAlignment = HorizontalAlignment.Left; log.Margin = new Thickness(34, 0, 0, 4); automation.Children.Add(log);
        var updates = Section(L("更新", "Updates"));
        Row(updates, T("automatically_check_updates"), "\uE72C", Toggle(T("automatically_check_updates"), native.AutomaticallyCheckUpdates, value => native.AutomaticallyCheckUpdates = value));
        var checkSchedule = Text(L("启用后启动时检查一次，此后每 60 分钟检查；同一版本本次运行仅自动提醒一次。",
            "When enabled, check at startup and every 60 minutes. Each version is prompted automatically only once per run."), 13, muted: true);
        checkSchedule.TextWrapping = TextWrapping.Wrap; checkSchedule.Margin = new Thickness(0, 0, 0, 14); updates.Children.Add(checkSchedule);
        var status = native.InstallStatus ?? WindowsLocalization.Error(native.UpdateError, State.Settings.Language) ?? (native.IsChecking ? L("正在检查更新…", "Checking for updates…")
            : native.AvailableUpdate is { } release ? L($"发现新版本 {NativeSettings.FormatVersion(release.Version)}", $"Version {NativeSettings.FormatVersion(release.Version)} is available")
            : native.HasChecked ? L("当前已是最新版本。", "You are up to date.")
            : L("从 GitHub 检查更新；更新后自动重新启动，保留账号数据。", "Checks GitHub for updates. Updating restarts this tool and preserves account data."));
        var hint = Text(status, 14, muted: true); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 2, 0, 14); updates.Children.Add(hint);
        hint.ToolTip = WindowsLocalization.TechnicalDetails(native.UpdateDiagnostic ?? native.UpdateError, State.Settings.Language);
        if (native.AvailableUpdate is { } update && !string.IsNullOrWhiteSpace(update.Notes)) {
            var notes = Text(update.Notes, 14, muted: true); notes.TextWrapping = TextWrapping.Wrap; notes.Margin = new Thickness(0, 0, 0, 14); updates.Children.Add(notes);
        }
        var check = Button(T("check_for_updates"), () => _ = native.CheckUpdatesAsync(manual: true)); check.IsEnabled &= !native.IsChecking && !State.IsAddingAccount;
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(check);
        if (native.AvailableUpdate != null) {
            var install = Button(L("更新并重启", "Update & restart"), native.RequestUpdatePrompt); Primary(install); install.IsEnabled &= !State.IsAddingAccount;
            install.Margin = new Thickness(10, 0, 0, 0); buttons.Children.Add(install);
        }
        var version = Text(T("current_version").Replace("%@", native.DisplayVersion), 14, muted: true); version.Margin = new Thickness(0, 0, 12, 0);
        var footer = Pair(version, buttons); footer.Margin = new Thickness(0, 0, 0, 4); updates.Children.Add(footer); body.Children.Add(settings);
    }
}
