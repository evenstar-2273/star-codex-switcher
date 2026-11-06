using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodexAccountSwitcher;

public sealed class QuotaRefreshSelector : UserControl
{
    private static readonly int[] Presets = [0, 2, 5, 10, 15];
    private readonly Func<int, Task> save;
    private readonly ComboBox choice = new();
    private readonly Grid editor = new();
    private readonly TextBox minutes = new() { MinWidth = 70, Padding = new Thickness(8, 6, 6, 6), FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock unit = new() { Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
    private readonly Button presets = new() { Content = "⌄", Width = 32, MinHeight = 36, Padding = new Thickness(4), FontSize = 18 };
    private readonly TextBlock feedback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 2), Visibility = Visibility.Collapsed };
    private bool chinese, enabled = true, custom, changing, saving, invalid, saveFailed, choosingPreset, reparenting, restoreFocus;
    private int current, selectionStart, selectionLength;

    public QuotaRefreshSelector(int initialMinutes, bool chinese, Func<int, Task> save)
    {
        this.save = save; this.chinese = chinese;
        current = Normalize(initialMinutes); custom = !Presets.Contains(current);
        Width = 200; VerticalAlignment = VerticalAlignment.Center;
        editor.ColumnDefinitions.Add(new ColumnDefinition());
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.Children.Add(minutes); Grid.SetColumn(unit, 1); editor.Children.Add(unit); Grid.SetColumn(presets, 2); editor.Children.Add(presets);
        presets.SetResourceReference(StyleProperty, "ActionButton");
        unit.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); feedback.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        var body = new StackPanel(); body.Children.Add(choice); body.Children.Add(editor); body.Children.Add(feedback); Content = body;
        minutes.Text = current.ToString(CultureInfo.InvariantCulture);
        choice.SelectionChanged += (_, _) => {
            if (changing || choice.SelectedIndex < 0 || saving) return;
            if (choice.SelectedIndex == Presets.Length) EnterCustom();
            else _ = SaveAsync(Presets[choice.SelectedIndex], useCustom: false);
        };
        minutes.TextChanged += (_, _) => { if (!changing && !saving) { invalid = false; saveFailed = false; UpdateFeedback(); } };
        minutes.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = CommitCustomAsync(); } };
        minutes.LostKeyboardFocus += (_, e) => { if (!reparenting && !choosingPreset && e.NewFocus != presets) _ = CommitCustomAsync(); };
        presets.LostKeyboardFocus += (_, e) => { if (!reparenting && !choosingPreset && e.NewFocus != minutes) _ = CommitCustomAsync(); };
        presets.Click += (_, _) => OpenPresets();
        ApplyPresentation();
    }

    private static int Normalize(int value) => value is >= 0 and <= 1440 ? value : 5;
    private string L(string zh, string en) => chinese ? zh : en;
    private string Label(int value) => value == 0 ? L("禁用", "Disabled") : L($"{value} 分钟", $"{value} minutes");
    private bool HasDraft => custom && minutes.Text != current.ToString(CultureInfo.InvariantCulture);

    public void UpdateState(int value, bool useChinese, bool isEnabled)
    {
        var hadDraft = HasDraft;
        var changed = Normalize(value) != current;
        current = Normalize(value); chinese = useChinese; enabled = isEnabled;
        if (changed && !hadDraft && !saving) {
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
        editor.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        unit.Text = L("分钟", "minutes");
        minutes.ToolTip = L("输入 1–1440 的整数；按 Enter 或移开焦点保存。", "Enter an integer from 1 to 1440; press Enter or move focus to save.");
        presets.ToolTip = L("切换为预设间隔", "Choose a preset interval");
        AutomationProperties.SetName(choice, L("额度刷新间隔", "Quota refresh interval"));
        AutomationProperties.SetName(minutes, L("自定义刷新分钟数", "Custom refresh minutes"));
        AutomationProperties.SetName(presets, L("选择刷新间隔", "Choose refresh interval"));
        AutomationProperties.SetName(feedback, L("刷新间隔提示", "Refresh interval feedback"));
        IsEnabled = enabled && !saving;
        changing = false; UpdateFeedback();
    }

    private void EnterCustom()
    {
        custom = true; invalid = saveFailed = false;
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
        foreach (var value in Presets) {
            var item = new MenuItem { Header = Label(value), IsCheckable = true, IsChecked = !HasDraft && current == value };
            item.Click += (_, _) => _ = SaveAsync(value, useCustom: false); menu.Items.Add(item);
        }
        var customItem = new MenuItem { Header = L("自定义…", "Custom…"), IsCheckable = true, IsChecked = custom };
        customItem.Click += (_, _) => EnterCustom(); menu.Items.Add(customItem);
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
        custom = useCustom; invalid = saveFailed = false;
        if (value == current) {
            minutes.Text = value.ToString(CultureInfo.InvariantCulture); ApplyPresentation(); return;
        }
        saving = true; ApplyPresentation();
        try {
            await save(value);
            current = value; minutes.Text = value.ToString(CultureInfo.InvariantCulture);
        } catch {
            saveFailed = true;
            if (!useCustom) custom = !Presets.Contains(current);
        } finally { saving = false; ApplyPresentation(); }
    }

    private void UpdateFeedback()
    {
        feedback.Text = invalid ? L("请输入 1–1440 的整数分钟。", "Enter a whole number from 1 to 1440.")
            : saveFailed ? L("保存失败，请重试。", "Could not save. Please try again.")
            : saving ? L("正在保存…", "Saving…") : "";
        feedback.SetResourceReference(TextBlock.ForegroundProperty, invalid || saveFailed ? "Danger" : "Muted");
        feedback.Visibility = feedback.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
