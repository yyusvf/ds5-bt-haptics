using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DS5BtHaptics.App.Ui.Views;

/// <summary>Pixel-based wheel scrolling with a short, interruptible ease-out.</summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(SmoothScroll));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0d, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private sealed class State { public double Target; public bool Active; public int Revision; public int Direction; }
    private static void EnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if ((bool)e.NewValue)
        {
            viewer.SetValue(StateProperty, new State());
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += Interrupt;
            viewer.PreviewKeyDown += Interrupt;
            viewer.ScrollChanged += ScrollChanged;
            viewer.Unloaded += Unloaded;
        }
        else
        {
            Stop(viewer);
            viewer.PreviewMouseWheel -= Wheel;
            viewer.PreviewMouseDown -= Interrupt;
            viewer.PreviewKeyDown -= Interrupt;
            viewer.ScrollChanged -= ScrollChanged;
            viewer.Unloaded -= Unloaded;
            viewer.ClearValue(StateProperty);
        }
    }

    private static void Wheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.Handled || e.Delta == 0 || viewer.ScrollableHeight <= 0 || SystemParameters.WheelScrollLines == 0) return;
        // Let a nested scrollable list consume its own wheel, especially open device dropdowns.
        for (DependencyObject? child = e.OriginalSource as DependencyObject; child != null && child != viewer;)
        {
            if (child is ScrollViewer nested && nested.ScrollableHeight > 0) return;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }
        var state = (State)viewer.GetValue(StateProperty);
        double distance = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16;
        int direction = Math.Sign(e.Delta);
        double origin = state.Active && direction == state.Direction ? state.Target : viewer.VerticalOffset;
        double target = Math.Clamp(origin - e.Delta / 120d * distance, 0, viewer.ScrollableHeight);
        double from = viewer.VerticalOffset;
        Stop(viewer);
        state.Target = target;
        state.Direction = direction;
        e.Handled = true;
        if (!SystemParameters.ClientAreaAnimation) { viewer.ScrollToVerticalOffset(target); return; }
        state.Active = true;
        int revision = ++state.Revision;
        viewer.SetValue(OffsetProperty, from);
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            if (revision != state.Revision) return;
            Stop(viewer);
            viewer.ScrollToVerticalOffset(Math.Clamp(target, 0, viewer.ScrollableHeight));
        };
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Stop(ScrollViewer viewer)
    {
        if (viewer.GetValue(StateProperty) is not State state) return;
        if (!state.Active) { state.Target = viewer.VerticalOffset; return; }
        state.Active = false;
        ++state.Revision;
        double current = viewer.VerticalOffset;
        viewer.BeginAnimation(OffsetProperty, null);
        viewer.SetValue(OffsetProperty, current);
        viewer.ScrollToVerticalOffset(current);
        state.Target = current;
    }
    private static void Interrupt(object sender, InputEventArgs e) => Stop((ScrollViewer)sender);
    private static void Unloaded(object sender, RoutedEventArgs e) => Stop((ScrollViewer)sender);
    private static void ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource == sender && (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)) Stop((ScrollViewer)sender);
    }
}
