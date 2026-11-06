using System;
using System.Windows;
using System.Windows.Controls;

namespace CodexAccountSwitcher;

// Keep the account actions together and the utility actions right aligned.
// When translated labels need more space, move utilities to the next line.
internal sealed class AccountActionsPanel : Panel
{
    private const double HorizontalGap = 10;
    private const double VerticalGap = 8;

    protected override Size MeasureOverride(Size constraint)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        if (InternalChildren.Count != 2) return new Size();
        var leading = InternalChildren[0].DesiredSize;
        var trailing = InternalChildren[1].DesiredSize;
        var requiredWidth = leading.Width + HorizontalGap + trailing.Width;
        var wrap = requiredWidth > constraint.Width;
        return new Size(Math.Min(constraint.Width, requiredWidth), wrap ? leading.Height + VerticalGap + trailing.Height : Math.Max(leading.Height, trailing.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        var leading = InternalChildren[0].DesiredSize;
        var trailing = InternalChildren[1].DesiredSize;
        var wrap = leading.Width + HorizontalGap + trailing.Width > finalSize.Width;
        InternalChildren[0].Arrange(new Rect(0, 0, leading.Width, leading.Height));
        InternalChildren[1].Arrange(new Rect(Math.Max(0, finalSize.Width - trailing.Width), wrap ? leading.Height + VerticalGap : 0, trailing.Width, trailing.Height));
        return finalSize;
    }
}
