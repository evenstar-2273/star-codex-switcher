using System;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace CodexAccountSwitcher;

// Fill the available content width with equally sized cards, retaining usable action rows.
// Card margins provide half the gap on each side; the surrounding negative margin
// keeps the first and last visible card aligned with the rest of the page.
public sealed class AdaptiveCardGrid : UniformGrid
{
    public const double MinimumCardWidth = 380;
    public const double CardGap = 16;
    public const int MaximumColumns = 3;

    protected override Size MeasureOverride(Size constraint)
    {
        Columns = double.IsFinite(constraint.Width)
            ? Math.Clamp((int)Math.Floor(constraint.Width / (MinimumCardWidth + CardGap)), 1, MaximumColumns)
            : 1;
        return base.MeasureOverride(constraint);
    }
}
