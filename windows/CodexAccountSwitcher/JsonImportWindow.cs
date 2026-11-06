using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class JsonImportWindow : Window
{
    private bool importing;
    public JsonImportWindow(IAccountImporter importer)
    {
        Title = "导入账号 · " + App.DisplayName;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Width = Math.Min(820, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(620, SystemParameters.WorkArea.Height - 24);
        MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Ink");
        var panel = new Grid { Margin = new Thickness(28) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        heading.Children.Add(new TextBlock { Text = "导入账号", FontSize = 26, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock { Text = "粘贴账号 JSON 或 auth.json，支持多个账号。", FontSize = 15,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        heading.Children.Add(subtitle); panel.Children.Add(heading);
        var editor = new TextBox {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 15, Padding = new Thickness(16),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            IsUndoEnabled = false, MaxLength = 16 * 1024 * 1024
        };
        editor.SetResourceReference(TextBox.ForegroundProperty, "Ink");
        editor.SetResourceReference(TextBox.CaretBrushProperty, "Ink");
        AutomationProperties.SetName(editor, "账号 JSON 文本");
        var editorGrid = new Grid(); editorGrid.Children.Add(editor);
        var placeholder = new TextBlock { Text = "在此粘贴完整 JSON 内容…", FontSize = 15, Margin = new Thickness(18),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); editorGrid.Children.Add(placeholder);
        var editorBorder = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = editorGrid };
        editorBorder.SetResourceReference(Border.BackgroundProperty, "InputSurface"); editorBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
        Grid.SetRow(editorBorder, 1); panel.Children.Add(editorBorder);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 12, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        AutomationProperties.SetName(status, "导入状态");
        Grid.SetRow(status, 2); panel.Children.Add(status);
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var hint = new TextBlock { Text = "导入成功后自动关闭 · 不会切换当前登录", FontSize = 14, TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(hint);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Button Action(string label) {
            var button = new Button { Content = label, Style = (Style)FindResource("ActionButton"), Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(8, 4, 0, 0), MinHeight = 40 };
            AutomationProperties.SetName(button, label); return button;
        }
        var file = Action("选择文件"); AutomationProperties.SetName(file, "选择 JSON 文件导入");
        var clear = Action("清空");
        var close = Action("取消"); close.IsCancel = true;
        var submit = Action("导入账号"); submit.IsEnabled = false;
        submit.SetResourceReference(Button.BackgroundProperty, "Accent"); submit.SetResourceReference(Button.ForegroundProperty, "OnAccent");
        submit.SetResourceReference(Button.BorderBrushProperty, "Accent");
        AutomationProperties.SetName(submit, "导入粘贴的 JSON");
        clear.Click += (_, _) => { editor.Clear(); status.Text = ""; editor.Focus(); };
        close.Click += (_, _) => Close();
        editor.TextChanged += (_, _) => {
            submit.IsEnabled = !importing && !string.IsNullOrWhiteSpace(editor.Text);
            placeholder.Visibility = string.IsNullOrEmpty(editor.Text) ? Visibility.Visible : Visibility.Collapsed;
        };
        async System.Threading.Tasks.Task ImportAsync(string[]? files = null) {
            if (importing) return;
            var succeeded = false;
            importing = true; submit.IsEnabled = false; clear.IsEnabled = false; close.IsEnabled = false; file.IsEnabled = false; editor.IsReadOnly = true;
            status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            status.Text = "正在验证格式并导入…";
            try {
                _ = files == null ? await importer.ImportTextAsync(editor.Text) : await importer.ImportFilesAsync(files);
                editor.Clear(); succeeded = true;
            } catch (Exception ex) {
                status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                status.Text = ex.Message;
            } finally {
                importing = false; clear.IsEnabled = true; close.IsEnabled = true; file.IsEnabled = true; editor.IsReadOnly = false;
                submit.IsEnabled = !string.IsNullOrWhiteSpace(editor.Text);
            }
            if (succeeded) Close();
        }
        submit.Click += async (_, _) => await ImportAsync();
        file.Click += async (_, _) => {
            if (importing) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择账号 JSON", Filter = "JSON 文件 (*.json)|*.json", Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileNames);
        };
        buttons.Children.Add(clear); buttons.Children.Add(file); buttons.Children.Add(close); buttons.Children.Add(submit);
        Grid.SetRow(buttons, 1); footer.Children.Add(buttons);
        Grid.SetRow(footer, 3); panel.Children.Add(footer); Content = panel;
        Loaded += (_, _) => editor.Focus();
        Closing += (_, e) => { if (importing) e.Cancel = true; };
        Closed += (_, _) => editor.Clear();
    }
}
