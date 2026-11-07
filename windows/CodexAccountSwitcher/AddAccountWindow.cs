using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexAccountSwitcher;

public enum AddAccountMethod { Browser, Json, Local }

public sealed class AddAccountWindow : Window
{
    public AddAccountMethod? SelectedMethod { get; private set; }

    public AddAccountWindow(bool chinese = true, bool canImport = true, Func<Task>? readLocal = null, bool recovery = false)
    {
        string L(string zh, string en) => chinese ? zh : en;
        Title = (recovery ? L("恢复登录", "Restore sign-in") : L("添加账号", "Add account")) + " · " + App.DisplayName;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Width = Math.Min(560, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(ForegroundProperty, "Ink");
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = recovery ? L("恢复登录", "Restore sign-in") : L("添加账号", "Add account"), FontSize = 26, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock { Text = recovery
            ? L("重新授权同一账号以更新登录，或导入有效 JSON、获取本地账号。", "Authorize the same account again to update its sign-in, or import valid JSON or get the local account.")
            : L("选择一种添加方式", "Choose how to add your account"), TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 8, 0, 22) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(intro);
        var methods = new List<Button>();
        var reading = false;
        Button Method(AddAccountMethod method, string name, string description, string glyph)
        {
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            var symbol = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 23, VerticalAlignment = VerticalAlignment.Center };
            symbol.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk"); content.Children.Add(symbol);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeights.SemiBold });
            var hint = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 6, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); text.Children.Add(hint); Grid.SetColumn(text, 1); content.Children.Add(text);
            var arrow = new TextBlock { Text = "\uE76C", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(arrow, 2); content.Children.Add(arrow);
            var button = new Button { Content = content, Style = (Style)FindResource("ActionButton"), Padding = new Thickness(18),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 98, Margin = new Thickness(0, 0, 0, 12) };
            // Explicit content width lets both option cards share the same text wrapping.
            button.SizeChanged += (_, _) => content.Width = Math.Max(0, button.ActualWidth - button.Padding.Left - button.Padding.Right - 2);
            button.SetResourceReference(BackgroundProperty, "ListSurface"); AutomationProperties.SetName(button, name);
            AutomationProperties.SetHelpText(button, description);
            if (method != AddAccountMethod.Local) button.Click += (_, _) => { if (!reading) { SelectedMethod = method; DialogResult = true; } };
            methods.Add(button);
            return button;
        }
        panel.Children.Add(Method(AddAccountMethod.Browser, L("浏览器授权", "Browser authorization"),
            recovery ? L("在浏览器中重新登录此账号，更新已保存的登录凭据。", "Sign in to this same account in the browser to update its saved credentials.")
                : L("打开浏览器，使用 ChatGPT 账号登录并授权。", "Sign in with your ChatGPT account in the browser."), "\uE774"));
        var import = Method(AddAccountMethod.Json, L("JSON 导入", "Import JSON"),
            L("粘贴账号 JSON 或 auth.json，也可选择文件导入。", "Paste account JSON or auth.json, or choose a file."), "\uE896");
        import.IsEnabled = canImport; panel.Children.Add(import);
        var local = Method(AddAccountMethod.Local, L("获取本地账号", "Get local account"),
            L("读取本机 Codex 已登录的账号，添加到账号列表。", "Read the account signed in to Codex on this computer and save it to the list."), "\uE7F8");
        local.IsEnabled = readLocal != null; panel.Children.Add(local);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        AutomationProperties.SetName(status, L("本地账号读取状态", "Local account status")); panel.Children.Add(status);
        var cancel = new Button { Content = L("取消", "Cancel"), Style = (Style)FindResource("ActionButton"), MinWidth = 88,
            IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(cancel, L("取消", "Cancel")); cancel.Click += (_, _) => Close(); panel.Children.Add(cancel);
        local.Click += async (_, _) => {
            if (reading || readLocal == null) return;
            reading = true;
            foreach (var method in methods) method.IsEnabled = false;
            cancel.IsEnabled = false;
            status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); status.Text = L("正在读取本地账号…", "Reading local account…");
            status.ToolTip = null;
            var succeeded = false;
            try { await readLocal(); SelectedMethod = AddAccountMethod.Local; succeeded = true; }
            catch (Exception ex) {
                status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                status.Text = WindowsLocalization.Error(ex.Message, chinese ? "simplifiedChinese" : "english");
                status.ToolTip = WindowsLocalization.TechnicalDetails(ex.Message, chinese ? "simplifiedChinese" : "english");
            } finally {
                reading = false;
                foreach (var method in methods) method.IsEnabled = true;
                import.IsEnabled = canImport; cancel.IsEnabled = true;
            }
            if (succeeded) Close();
        };
        Closing += (_, e) => { if (reading) e.Cancel = true; };
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
