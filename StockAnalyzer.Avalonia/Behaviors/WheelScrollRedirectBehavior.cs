using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;

namespace StockAnalyzer.Avalonia.Behaviors;

/// <summary>
/// Attached behavior for a <see cref="ScrollViewer"/> that hosts a dense settings form
/// (e.g. <c>DrawingSettingsDialog</c>): while enabled, a wheel gesture anywhere inside the
/// <see cref="ScrollViewer"/> always scrolls the form.
///
/// Two Avalonia quirks otherwise defeat wheel scrolling in such a dialog:
/// <list type="bullet">
///   <item><see cref="ComboBox"/> (dropdown closed) and <see cref="NumericUpDown"/> consume
///   <c>PointerWheelChanged</c> for value stepping while focused, marking it handled so it never
///   reaches the <see cref="ScrollViewer"/> -- and right after editing a field the pointer is
///   almost always over one of those controls, so the wheel silently changes a value instead of
///   scrolling.</item>
///   <item>The dialog's scoped <c>SidebarScrollViewerTheme</c> template does not paint the
///   <see cref="ScrollViewer"/>'s background, so wheel events over the empty regions between
///   controls do not route through the <see cref="ScrollViewer"/> subtree at all and its own
///   native <c>OnPointerWheelChanged</c> never runs there.</item>
/// </list>
///
/// A tunnelling handler on the <see cref="ScrollViewer"/> therefore intercepts wheel gestures routed
/// through it, scrolls vertically by the same pixel step Avalonia's native handler uses, and marks
/// the event handled so inner controls never step their value. The host must still make the content
/// hit-testable (e.g. a <c>Transparent</c> background on the content root) so wheel events over blank
/// areas reach this handler.
///
/// The gesture is <b>not</b> intercepted when its origin sits inside a nearer container that can
/// itself still scroll -- a nested <see cref="ListBox"/>, an inner settings <see cref="ScrollViewer"/>
/// (with its own copy of this behavior), a multi-line text box, or an open <see cref="ComboBox"/>
/// dropdown popup. That inner container owns the wheel; when it reaches its own scroll limit Avalonia
/// bubbles the unhandled event out to this <see cref="ScrollViewer"/> anyway.
/// </summary>
public static class WheelScrollRedirectBehavior
{
    /// <summary>
    /// Vertical pixels scrolled per wheel notch. Mirrors the non-logical wheel step used by
    /// Avalonia 11.3's <c>ScrollViewer.OnPointerWheelChanged</c> (50px per notch) so a redirected
    /// gesture feels identical to scrolling over a plain area of the same dialog.
    /// </summary>
    private const double WheelPixelStep = 50.0;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(WheelScrollRedirectBehavior));

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    static WheelScrollRedirectBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((scrollViewer, e) =>
        {
            if (e.NewValue is true)
            {
                scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnTunnelWheel, RoutingStrategies.Tunnel);
            }
        });
    }

    private static void OnTunnelWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer scrollViewer || e.Source is not Visual source)
        {
            return;
        }

        // An open ComboBox shows its own scrollable list in a popup -- leave that gesture alone.
        var combo = source.FindAncestorOfType<ComboBox>(includeSelf: true);
        if (combo is { IsDropDownOpen: true })
        {
            return;
        }

        // If the wheel started inside a nearer scroll container that can still scroll (a nested
        // ListBox, an inner settings ScrollViewer, a multi-line text box), let that container
        // consume it. Its own limit-then-bubble behaviour hands the gesture back to us when it
        // cannot scroll further. Single-line TextBox / NumericUpDown editors carry an inner
        // ScrollViewer too, but it never overflows, so those still fall through to the redirect.
        var innerScroller = source.FindAncestorOfType<ScrollViewer>(includeSelf: true);
        if (innerScroller != null
            && !ReferenceEquals(innerScroller, scrollViewer)
            && innerScroller.Extent.Height > innerScroller.Viewport.Height)
        {
            return;
        }

        double delta = e.Delta.Y;
        if (delta != 0)
        {
            double maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            if (maxOffset > 0)
            {
                double newY = Math.Clamp(scrollViewer.Offset.Y - delta * WheelPixelStep, 0, maxOffset);
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, newY);
            }
        }

        // Swallow the gesture even when there is nothing to scroll, so a ComboBox / NumericUpDown
        // under the pointer does not step its value instead.
        e.Handled = true;
    }
}
