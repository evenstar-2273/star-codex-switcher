using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexAccountSwitcher;

public sealed class QuotaRefreshSelector : UserControl
{
    private static readonly int[] Presets = [0, 2, 5, 10, 15];
    private readonly Func<int, Task> save;
    private readonly ComboBox choice = new();
    private readonly Grid editor = new();
    private readonly Border inputFrame = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), MinHeight = 38 };
    private readonly TextBox minutes = new() { MinWidth = 70, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(11, 7, 4, 7), FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock unit = new() { Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
    private readonly Button presets = new() { Width = 34, MinHeight = 36, Padding = new Thickness(0) };
    private readonly Button saveButton = new() { MinWidth = 64, MinHeight = 32, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock saveCaption = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock feedback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Grid resultRow = new() { Margin = new Thickness(0, 8, 0, 0) };
    private bool chinese, enabled = true, custom, changing, saving, invalid, saveFailed, saveSucceeded, choosingPreset, reparenting, restoreFocus;
    private int current, selectionStart, selectionLength;

    public QuotaRefreshSelector(int initialMinutes, bool chinese, Func<int, Task> save)
    {
        this.save = save; this.chinese = chinese;
        current = Normalize(initialMinutes); custom = !Presets.Contains(current);
        Width = 220; VerticalAlignment = VerticalAlignment.Center;
        editor.ColumnDefinitions.Add(new ColumnDefinition());
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.Children.Add(minutes); Grid.SetColumn(unit, 1); editor.Children.Add(unit); Grid.SetColumn(presets, 2); editor.Children.Add(presets);
        inputFrame.Child = editor; inputFrame.SetResourceReference(Border.BackgroundProperty, "InputSurface"); inputFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        editor.GotKeyboardFocus += (_, _) => inputFrame.SetResourceReference(Border.BorderBrushProperty, "AccentBorder");
        editor.LostKeyboardFocus += (_, _) => { if (!editor.IsKeyboardFocusWithin) inputFrame.SetResourceReference(Border.BorderBrushProperty, "Line"); };
        var arrow = new TextBlock { Text = "\uE70D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); presets.Content = arrow;
        presets.Template = ParseTemplate("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Border x:Name="Chrome" Background="Transparent" CornerRadius="0,7,7,0"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Selected}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        saveButton.SetResourceReference(StyleProperty, "ActionButton"); saveButton.SetResourceReference(Control.BackgroundProperty, "Accent");
        saveButton.SetResourceReference(Control.BorderBrushProperty, "AccentBorder"); saveButton.SetResourceReference(Control.ForegroundProperty, "OnAccent");
        saveCaption.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = saveButton }); saveButton.Content = saveCaption;
        unit.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); feedback.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        resultRow.ColumnDefinitions.Add(new ColumnDefinition()); resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resultRow.Children.Add(feedback); Grid.SetColumn(saveButton, 1); resultRow.Children.Add(saveButton);
        var body = new StackPanel(); body.Children.Add(choice); body.Children.Add(inputFrame); body.Children.Add(resultRow); Content = body;
        minutes.Text = current.ToString(CultureInfo.InvariantCulture);
        choice.SelectionChanged += (_, _) => {
            if (changing || choice.SelectedIndex < 0 || saving) return;
            if (choice.SelectedIndex == Presets.Length) EnterCustom();
            else _ = SaveAsync(Presets[choice.SelectedIndex], useCustom: false);
        };
        minutes.TextChanged += (_, _) => { if (!changing && !saving) { invalid = false; saveFailed = false; saveSucceeded = false; UpdateFeedback(); } };
        minutes.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = CommitCustomAsync(); } };
        minutes.LostKeyboardFocus += (_, e) => { if (!reparenting && !choosingPreset && e.NewFocus != presets && e.NewFocus != saveButton) _ = CommitCustomAsync(); };
        presets.LostKeyboardFocus += (_, e) => { if (!reparenting && !choosingPreset && e.NewFocus != minutes && e.NewFocus != saveButton) _ = CommitCustomAsync(); };
        saveButton.LostKeyboardFocus += (_, e) => { if (!reparenting && !choosingPreset && e.NewFocus != minutes && e.NewFocus != presets) _ = CommitCustomAsync(); };
        presets.Click += (_, _) => OpenPresets();
        saveButton.Click += (_, _) => _ = CommitCustomAsync();
        ApplyPresentation();
    }

    private static int Normalize(int value) => value is >= 0 and <= 1440 ? value : 5;
    private static ControlTemplate ParseTemplate(string xaml) => (ControlTemplate)XamlReader.Parse(xaml);
    private string L(string zh, string en) => chinese ? zh : en;
    private string Label(int value) => value == 0 ? L("禁用", "Disabled") : L($"{value} 分钟", $"{value} minutes");
    private bool HasDraft => custom && minutes.Text != current.ToString(CultureInfo.InvariantCulture);

    public void UpdateState(int value, bool useChinese, bool isEnabled)
    {
        var hadDraft = HasDraft;
        var changed = Normalize(value) != current;
        current = Normalize(value); chinese = useChinese; enabled = isEnabled;
        if (changed && !hadDraft && !saving) {
            saveSucceeded = false;
            custom = !Presets.Contains(current);
            minutes.Text = current.ToString(CultureInfo.InvariantCulture);
        }
        ApplyPresentation();
    }

    internal void PrepareForReparent()
    {
        reparenting = true;
        // A blur-triggered save can publish its settings snapshot while WPF is
        // still dispatching the focus change. Do not steal focus back afterwards.
        if (saving) restoreFocus = false;
        else if (minutes.IsKeyboardFocusWithin) {
            restoreFocus = true; selectionStart = minutes.SelectionStart; selectionLength = minutes.SelectionLength;
        }
        if (Parent is Panel parent) parent.Children.Remove(this);
    }

    internal void FinishReparent(bool onSettingsPage)
    {
        reparenting = false;
        if (restoreFocus && onSettingsPage && custom && IsEnabled) {
            minutes.Focus(); minutes.Select(Math.Min(selectionStart, minutes.Text.Length), Math.Min(selectionLength, Math.Max(0, minutes.Text.Length - selectionStart)));
        }
        restoreFocus = false;
    }

    private void ApplyPresentation()
    {
        changing = true;
        choice.ItemsSource = Presets.Select(Label).Append(L("自定义…", "Custom…")).ToArray();
        choice.SelectedIndex = custom ? Presets.Length : Array.IndexOf(Presets, current);
        choice.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        inputFrame.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        unit.Text = L("分钟", "minutes");
        minutes.ToolTip = L("输入 1–1440 的整数，点击保存；也可按 Enter 或移开焦点保存。", "Enter an integer from 1 to 1440 and click Save. Enter or moving focus also saves.");
        presets.ToolTip = L("切换为预设间隔", "Choose a preset interval");
        AutomationProperties.SetName(choice, L("额度刷新间隔", "Quota refresh interval"));
        AutomationProperties.SetName(minutes, L("自定义刷新分钟数", "Custom refresh minutes"));
        AutomationProperties.SetName(presets, L("选择刷新间隔", "Choose refresh interval"));
        AutomationProperties.SetName(saveButton, L("保存刷新间隔", "Save refresh interval"));
        AutomationProperties.SetName(feedback, L("刷新间隔提示", "Refresh interval feedback"));
        IsEnabled = enabled && !saving;
        changing = false; UpdateFeedback();
    }

    private void EnterCustom()
    {
        custom = true; invalid = saveFailed = saveSucceeded = false;
        minutes.Text = (current > 0 ? current : 5).ToString(CultureInfo.InvariantCulture);
        ApplyPresentation(); minutes.Focus(); minutes.SelectAll();
    }

    private void OpenPresets()
    {
        if (!enabled || saving) return;
        var menu = new ContextMenu();
        menu.SetResourceReference(Control.BackgroundProperty, "InputSurface");
        menu.SetResourceReference(Control.ForegroundProperty, "Ink");
        menu.SetResourceReference(Control.BorderBrushProperty, "Line");
        menu.MinWidth = Width; menu.Padding = new Thickness(5);
        menu.Template = ParseTemplate("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ContextMenu">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="8" Padding="{TemplateBinding Padding}">
                <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
              </Border>
            </ControlTemplate>
            """);
        var menuItemTemplate = ParseTemplate("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuItem">
              <Border x:Name="ItemChrome" Background="Transparent" CornerRadius="5" Padding="10,8">
                <Grid><TextBlock x:Name="Checkmark" Text="&#xE73E;" FontFamily="Segoe MDL2 Assets" FontSize="12" Foreground="{DynamicResource AccentInk}" Visibility="Hidden" VerticalAlignment="Center"/>
                <ContentPresenter ContentSource="Header" Margin="26,0,0,0" VerticalAlignment="Center" TextElement.Foreground="{DynamicResource Ink}"/></Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemChrome" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Checkmark" Property="Visibility" Value="Visible"/><Setter TargetName="ItemChrome" Property="Background" Value="{DynamicResource Selected}"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        foreach (var value in Presets) {
            var item = new MenuItem { Header = Label(value), IsCheckable = true, IsChecked = !HasDraft && current == value };
            item.Template = menuItemTemplate; item.Click += (_, _) => _ = SaveAsync(value, useCustom: false); menu.Items.Add(item);
        }
        var customItem = new MenuItem { Header = L("自定义…", "Custom…"), IsCheckable = true, IsChecked = custom };
        customItem.Template = menuItemTemplate; customItem.Click += (_, _) => EnterCustom(); menu.Items.Add(customItem);
        menu.Closed += (_, _) => choosingPreset = false;
        choosingPreset = true; presets.ContextMenu = menu; menu.PlacementTarget = presets; menu.IsOpen = true;
    }

    private Task CommitCustomAsync()
    {
        if (!custom || !enabled || saving || reparenting) return Task.CompletedTask;
        if (!int.TryParse(minutes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 1440) {
            invalid = true; saveFailed = false; UpdateFeedback(); return Task.CompletedTask;
        }
        return SaveAsync(value, useCustom: true);
    }

    private async Task SaveAsync(int value, bool useCustom)
    {
        if (!enabled || saving) return;
        custom = useCustom; invalid = saveFailed = saveSucceeded = false; feedback.ToolTip = null;
        if (value == current) {
            minutes.Text = value.ToString(CultureInfo.InvariantCulture); saveSucceeded = true; ApplyPresentation(); return;
        }
        saving = true; ApplyPresentation();
        try {
            await save(value);
            current = value; minutes.Text = value.ToString(CultureInfo.InvariantCulture); saveSucceeded = true;
        } catch (Exception ex) {
            feedback.ToolTip = WindowsLocalization.TechnicalDetails(ex.Message, chinese ? "simplifiedChinese" : "english");
            saveFailed = true;
            if (!useCustom) custom = !Presets.Contains(current);
        } finally { saving = false; ApplyPresentation(); }
    }

    private void UpdateFeedback()
    {
        feedback.Text = invalid ? L("请输入 1–1440 的整数分钟。", "Enter a whole number from 1 to 1440.")
            : saveFailed ? L($"保存失败 · 仍为 {Label(current)}", $"Not saved. Still {Label(current)}.")
            : saving ? L("正在保存…", "Saving…")
            : custom && HasDraft ? L($"未保存 · 当前{Label(current)}", $"Unsaved · current {Label(current)}")
            : saveSucceeded ? L($"已保存 · {Label(current)}", $"Saved · {Label(current)}")
            : custom ? L($"当前已生效：{Label(current)}", $"Active: {Label(current)}") : "";
        feedback.SetResourceReference(TextBlock.ForegroundProperty, invalid || saveFailed ? "Danger" : saveSucceeded ? "Success" : "Muted");
        feedback.Visibility = feedback.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        saveCaption.Text = saving ? L("保存中", "Saving") : saveFailed ? L("重试", "Retry") : saveSucceeded && !HasDraft ? L("已保存", "Saved")
            : !HasDraft && !saveFailed ? L("已生效", "Active") : L("保存", "Save");
        saveButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        saveButton.IsEnabled = enabled && !saving && (HasDraft || saveFailed);
        resultRow.Visibility = custom || feedback.Text.Length != 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
