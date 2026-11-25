using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class AccountOrderWindow : Window
{
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), AllowDrop = true };
    private readonly NativeSettings settings;
    private readonly AccountRow[] accounts;
    private Guid[] order;
    private Point start;
    private Guid? dragging;
    private readonly bool chinese;
    public AccountOrderWindow(AccountRow[] accounts, NativeSettings settings, bool chinese)
    {
        this.accounts = accounts; this.settings = settings; this.chinese = chinese;
        order = NativeSettings.ReconcileOrder(settings.AccountOrder, accounts.Select(a => a.Profile.Id));
        Title = chinese ? "账号排序" : "Account order"; Width = 520; Height = 480; MinWidth = 380; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(ForegroundProperty, "Ink");
        var panel = new DockPanel { Margin = new Thickness(24) };
        var hint = new TextBlock { Text = chinese ? "拖动账号调整顺序，自动保存。当前使用的账号仍置顶。" : "Drag to reorder; saved automatically. The current account stays first.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        Add(chinese ? "上移" : "Up", () => MoveSelected(-1)); Add(chinese ? "下移" : "Down", () => MoveSelected(1));
        Add(chinese ? "恢复默认" : "Reset", () => Save(accounts.Select(a => a.Profile.Id).ToArray())); Add(chinese ? "完成" : "Done", Close);
        void Add(string label, Action action) {
            var button = new Button { Content = label, Style = (Style)FindResource("ActionButton"), Margin = new Thickness(6, 0, 0, 0) };
            button.Click += (_, _) => action(); actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        list.SetResourceReference(BackgroundProperty, "ListSurface"); list.SetResourceReference(ForegroundProperty, "Ink");
        AutomationProperties.SetName(list, chinese ? "拖拽排序账号列表" : "Account order list");
        list.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(list); dragging = ItemAt(e.OriginalSource as DependencyObject)?.Tag as Guid?; };
        list.PreviewMouseMove += (_, e) => {
            if (e.LeftButton != MouseButtonState.Pressed || dragging is not { } id) return;
            var delta = e.GetPosition(list) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragging = null; DragDrop.DoDragDrop(list, new DataObject("StarAccountOrder", id), DragDropEffects.Move);
        };
        list.DragOver += (_, e) => { e.Effects = e.Data.GetData("StarAccountOrder") is Guid id && order.Contains(id) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        list.Drop += (_, e) => {
            if (e.Data.GetData("StarAccountOrder") is Guid id && ItemAt(e.OriginalSource as DependencyObject)?.Tag is Guid target) Move(id, Array.IndexOf(order, target));
            e.Handled = true;
        };
        panel.Children.Add(list); Content = panel; Draw();
    }
    private static ListBoxItem? ItemAt(DependencyObject? node) {
        while (node != null && node is not ListBoxItem) node = VisualTreeHelper.GetParent(node);
        return node as ListBoxItem;
    }
    private void MoveSelected(int delta) { if (list.SelectedItem is ListBoxItem { Tag: Guid id }) Move(id, Array.IndexOf(order, id) + delta); }
    public void Move(Guid id, int index) {
        if (!order.Contains(id) || index < 0 || index >= order.Length) return;
        var next = order.Where(value => value != id).ToList(); next.Insert(index, id); Save(next.ToArray());
        list.SelectedItem = list.Items.OfType<ListBoxItem>().First(item => (Guid)item.Tag == id);
    }
    private void Save(Guid[] next) {
        try {
            // Save before accepting the visual change; an empty startup snapshot never erases stored order.
            settings.AccountOrder = next.Concat(settings.AccountOrder).Distinct().ToArray(); order = next; Draw();
        } catch (Exception) { MessageBox.Show(this, chinese ? "排序保存失败，请检查设置权限后重试。" : "Could not save order. Check settings permissions.", Title); }
    }
    private void Draw() {
        list.Items.Clear();
        foreach (var id in order) {
            var account = accounts.First(a => a.Profile.Id == id);
            list.Items.Add(new ListBoxItem { Tag = id, Content = "☰   " + (account.Profile.Email ?? account.Profile.DisplayName),
                Padding = new Thickness(12, 14, 12, 14), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
    }
}
