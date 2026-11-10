using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexAccountSwitcher;

// Keep the account actions together and the utility actions right aligned.
// Slightly shrink both groups together before falling back to a second line.
internal sealed class AccountActionsPanel : Panel
{
    private const double HorizontalGap = 10;
    private const double VerticalGap = 8;
    private static double Scale(double available, double leading, double trailing) =>
        Math.Clamp((available - HorizontalGap) / Math.Max(1, leading + trailing), .88, 1);

    protected override Size MeasureOverride(Size constraint)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        if (InternalChildren.Count != 2) return new Size();
        var leading = InternalChildren[0].DesiredSize;
        var trailing = InternalChildren[1].DesiredSize;
        var requiredWidth = leading.Width + HorizontalGap + trailing.Width;
        var scale = Scale(constraint.Width, leading.Width, trailing.Width);
        var wrap = (leading.Width + trailing.Width) * scale + HorizontalGap > constraint.Width + .1;
        return new Size(Math.Min(constraint.Width, requiredWidth), wrap ? (leading.Height + trailing.Height) * scale + VerticalGap : Math.Max(leading.Height, trailing.Height) * scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        var leading = InternalChildren[0].DesiredSize;
        var trailing = InternalChildren[1].DesiredSize;
        var scale = Scale(finalSize.Width, leading.Width, trailing.Width);
        foreach (UIElement child in InternalChildren) child.RenderTransform = new ScaleTransform(scale, scale);
        var wrap = (leading.Width + trailing.Width) * scale + HorizontalGap > finalSize.Width + .1;
        InternalChildren[0].Arrange(new Rect(0, 0, leading.Width, leading.Height));
        InternalChildren[1].Arrange(new Rect(Math.Max(0, finalSize.Width - trailing.Width * scale), wrap ? leading.Height * scale + VerticalGap : 0, trailing.Width, trailing.Height));
        return finalSize;
    }
}
