using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexAccountSwitcher;

public enum AddAccountMethod { Browser, Json }

public sealed class AddAccountWindow : Window
{
    public AddAccountMethod? SelectedMethod { get; private set; }

    public AddAccountWindow(bool chinese = true, bool canImport = true)
    {
        string L(string zh, string en) => chinese ? zh : en;
        Title = L("添加账号", "Add account") + " · " + App.DisplayName;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Width = Math.Min(560, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(ForegroundProperty, "Ink");
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = L("添加账号", "Add account"), FontSize = 26, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock { Text = L("选择一种添加方式", "Choose how to add your account"), FontSize = 15, Margin = new Thickness(0, 8, 0, 22) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(intro);
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
            button.Click += (_, _) => { SelectedMethod = method; DialogResult = true; };
            return button;
        }
        panel.Children.Add(Method(AddAccountMethod.Browser, L("浏览器授权", "Browser authorization"),
            L("打开浏览器，使用 ChatGPT 账号登录并授权。", "Sign in with your ChatGPT account in the browser."), "\uE774"));
        var import = Method(AddAccountMethod.Json, L("JSON 导入", "Import JSON"),
            L("粘贴账号 JSON 或 auth.json，也可选择文件导入。", "Paste account JSON or auth.json, or choose a file."), "\uE896");
        import.IsEnabled = canImport; panel.Children.Add(import);
        var cancel = new Button { Content = L("取消", "Cancel"), Style = (Style)FindResource("ActionButton"), MinWidth = 88,
            IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(cancel, L("取消", "Cancel")); cancel.Click += (_, _) => Close(); panel.Children.Add(cancel);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
