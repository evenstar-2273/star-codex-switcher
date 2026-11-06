using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexAccountSwitcher;

// One live transform per refresh request lets rebuilt controls retain its phase.
internal sealed class RefreshRotation : IDisposable
{
    private readonly Guid? accountId;
    public RotateTransform Transform { get; } = new();

    public RefreshRotation(Guid? accountId)
    {
        this.accountId = accountId;
        Transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900))) {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    public bool Includes(Guid? id) => accountId == null || accountId == id;

    public void Dispose() => Transform.BeginAnimation(RotateTransform.AngleProperty, null);
}
