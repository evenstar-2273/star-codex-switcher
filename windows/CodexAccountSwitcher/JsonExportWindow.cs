using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class JsonExportWindow : Window
{
    private readonly TextBox preview;
    private readonly TextBlock status;
    private readonly CheckBox reveal;
    private readonly Button copy, download;
    private readonly ComboBox formats;
    private string? content;
    private bool saving;
    public JsonExportWindow(IAccountExporter exporter, AccountRow account, Action<string>? copyJson = null, Func<string, string?>? selectDownload = null)
    {
        Title = "导出账号 · " + App.DisplayName;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(App.IconUri);
        Width = Math.Min(820, SystemParameters.WorkArea.Width - 24); Height = Math.Min(650, SystemParameters.WorkArea.Height - 24);
        MinWidth = 480; MinHeight = 450; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(ForegroundProperty, "Ink");
        var panel = new Grid { Margin = new Thickness(28) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new());
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        heading.Children.Add(new TextBlock { Text = "导出账号", FontSize = 26, FontWeight = FontWeights.SemiBold });
        var identity = new TextBlock { Text = account.Profile.Email ?? account.Profile.DisplayName, FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
        identity.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); heading.Children.Add(identity); panel.Children.Add(heading);
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        options.Children.Add(new TextBlock { Text = "导出格式", FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        formats = new ComboBox { Width = 238, ItemsSource = new[] { "Star Codex Switcher", "官方 auth.json", "sub2api", "CPA" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 18, 0) };
        AutomationProperties.SetName(formats, "导出格式"); options.Children.Add(formats);
        reveal = new CheckBox { Content = "显示完整凭据", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetName(reveal, "显示完整凭据"); options.Children.Add(reveal); Grid.SetRow(options, 1); panel.Children.Add(options);
        preview = new TextBox { IsReadOnly = true, IsUndoEnabled = false, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 14, Padding = new Thickness(16), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        preview.SetResourceReference(ForegroundProperty, "Ink"); AutomationProperties.SetName(preview, "导出 JSON 预览");
        var frame = new Border { Child = preview, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "InputSurface"); frame.SetResourceReference(Border.BorderBrushProperty, "Line"); Grid.SetRow(frame, 2); panel.Children.Add(frame);
        status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(status, "导出状态"); Grid.SetRow(status, 3); panel.Children.Add(status);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var hint = new TextBlock { Text = "预览默认隐藏凭据；复制和下载包含完整账号数据，请妥善保管。", FontSize = 13, TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(hint);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Button Action(string label) {
            var caption = new TextBlock { Text = label };
            caption.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            var button = new Button { Content = caption, Style = (Style)FindResource("ActionButton"), Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8, 4, 0, 0) };
            AutomationProperties.SetName(button, label); buttons.Children.Add(button); return button;
        }
        var close = Action("关闭"); close.IsCancel = true; close.Click += (_, _) => Close();
        copy = Action("复制 JSON"); download = Action("下载 JSON");
        download.SetResourceReference(BackgroundProperty, "Accent"); download.SetResourceReference(ForegroundProperty, "OnAccent"); download.SetResourceReference(BorderBrushProperty, "Accent");
        footer.Children.Add(buttons); Grid.SetRow(footer, 4); panel.Children.Add(footer); Content = panel;
        void ShowPreview() => preview.Text = content == null ? "" : reveal.IsChecked == true ? content : AccountExport.Preview(content);
        void LoadContent() {
            status.Text = "";
            try { content = exporter.ExportAccount(account.Profile.Id, (AccountExportFormat)formats.SelectedIndex); }
            catch { content = null; Message("无法读取此账号的导出数据，请检查本地凭据后重试。", true); }
            copy.IsEnabled = download.IsEnabled = content != null; ShowPreview();
        }
        formats.SelectionChanged += (_, _) => LoadContent(); reveal.Checked += (_, _) => ShowPreview(); reveal.Unchecked += (_, _) => ShowPreview();
        copy.Click += (_, _) => {
            if (content == null) return;
            try { (copyJson ?? Clipboard.SetText)(content); Message("已复制完整 JSON。", false); }
            catch { Message("剪贴板暂时不可用，请重试或下载文件。", true); }
        };
        download.Click += async (_, _) => {
            if (content == null || saving) return;
            var suffix = ((AccountExportFormat)formats.SelectedIndex) switch { AccountExportFormat.Star => "star-codex-switcher", AccountExportFormat.AuthJson => "auth", AccountExportFormat.Sub2api => "sub2api", _ => "cpa" };
            var fileName = $"{suffix}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            string? destination;
            if (selectDownload != null) destination = selectDownload(fileName);
            else {
                var picker = new Microsoft.Win32.SaveFileDialog { Title = "下载账号 JSON", Filter = "JSON 文件 (*.json)|*.json", DefaultExt = ".json", AddExtension = true,
                    FileName = fileName, OverwritePrompt = true };
                destination = picker.ShowDialog(this) == true ? picker.FileName : null;
            }
            if (destination == null) return;
            saving = true; formats.IsEnabled = copy.IsEnabled = download.IsEnabled = close.IsEnabled = false;
            try { await System.Threading.Tasks.Task.Run(() => SecureFiles.AtomicWrite(destination, new UTF8Encoding(false).GetBytes(content))); Message("JSON 文件已保存。", false); }
            catch { Message("保存失败，请检查目标路径和写入权限后重试。", true); }
            finally { saving = false; formats.IsEnabled = close.IsEnabled = true; copy.IsEnabled = download.IsEnabled = content != null; }
        };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Closed += (_, _) => { content = null; preview.Clear(); };
        LoadContent();
    }
    private void Message(string text, bool error) { status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Success"); status.Text = text; }
}
