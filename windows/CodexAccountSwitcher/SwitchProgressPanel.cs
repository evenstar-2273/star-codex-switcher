using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class SwitchProgressPanel : Grid
{
    private readonly TextBlock title, detail, status;
    private readonly ProgressBar progress;
    private readonly Button close;
    private bool running;
    public bool Running => running;
    public SwitchProgressPanel(Action dismissed)
    {
        Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        Visibility = Visibility.Collapsed; Focusable = true;
        AutomationProperties.SetName(this, "切号进度弹窗");
        var body = new StackPanel { Margin = new Thickness(26) };
        TextBlock Text(string text, double size, string color) {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
            block.SetResourceReference(TextBlock.ForegroundProperty, color); return block;
        }
        title = Text("正在切换账号", 23, "Ink"); title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
        detail = Text("正在准备…", 16, "Ink"); body.Children.Add(detail);
        AutomationProperties.SetName(detail, "切号当前阶段");
        progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 2, 0, 18) };
        progress.SetResourceReference(Control.ForegroundProperty, "Accent"); progress.SetResourceReference(Control.BackgroundProperty, "InputSurface");
        AutomationProperties.SetName(progress, "切号阶段进度"); body.Children.Add(progress);
        body.Children.Add(Text("验证账号 → 关闭 Codex → 恢复项目 → 修复会话 → 启动", 13, "Muted"));
        status = Text("只维护本机项目和会话显示，请等待操作完成。", 14, "Muted"); body.Children.Add(status);
        close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 88, Padding = new Thickness(16, 9, 16, 9), Visibility = Visibility.Collapsed };
        close.SetResourceReference(StyleProperty, "ActionButton"); AutomationProperties.SetName(close, "关闭切号进度");
        close.Click += (_, _) => { if (!running) { Visibility = Visibility.Collapsed; dismissed(); } }; body.Children.Add(close);
        var card = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 570, Margin = new Thickness(20), Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        card.SetResourceReference(Border.BackgroundProperty, "ListSurface"); card.SetResourceReference(Border.BorderBrushProperty, "Line");
        SizeChanged += (_, _) => card.MaxHeight = Math.Max(160, ActualHeight - 40);
        Children.Add(card);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) e.Handled = true; };
    }
    public void Begin(bool restart)
    {
        running = true; title.Text = restart ? "正在重启 Codex" : "正在切换账号"; detail.Text = "正在准备，请稍候…";
        progress.Value = 0; progress.IsIndeterminate = true; close.Visibility = Visibility.Collapsed;
        status.Text = "只维护本机项目和会话显示，请等待操作完成。"; status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Visibility = Visibility.Visible; Focus();
    }
    public void Report(SwitchProgress value)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Report(value))); return; }
        if (!running) return;
        progress.IsIndeterminate = false; progress.Value = value.Percent; detail.Text = value.Message;
    }
    public void Finish(string? error)
    {
        running = false; progress.IsIndeterminate = false; close.Visibility = Visibility.Visible;
        if (error == null) { progress.Value = 100; title.Text = "操作完成"; detail.Text = "Codex 已启动"; status.Text = "本地项目与会话显示检查已完成。"; status.SetResourceReference(TextBlock.ForegroundProperty, "Success"); }
        else { title.Text = "操作未完成"; detail.Text = "请查看下面的处理结果"; status.Text = error; status.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); }
        close.Focus();
    }
}
