using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using System.Linq;
using System.Windows.Input;

namespace StockAnalyzer.Avalonia.Behaviors;

/// <summary>
/// Attached behavior for TabControl to enable intra-panel reordering via Drag and Drop.
/// Uses a simple FSM to distinguish between selection clicks and dragging.
/// </summary>
public static class TabReorderBehavior
{
    private enum DragState { Idle, Pressed, Dragging }

    private static DragState _state = DragState.Idle;
    private static global::Avalonia.Point _startPos;
    private static TabItem? _sourceItem;
    private const double DragThreshold = 8.0;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TabControl, bool>("IsEnabled", typeof(TabReorderBehavior));

    public static readonly AttachedProperty<ICommand> ReorderCommandProperty =
        AvaloniaProperty.RegisterAttached<TabControl, ICommand>("ReorderCommand", typeof(TabReorderBehavior));

    public static bool GetIsEnabled(TabControl element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TabControl element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static ICommand GetReorderCommand(TabControl element) => element.GetValue(ReorderCommandProperty);
    public static void SetReorderCommand(TabControl element, ICommand value) => element.SetValue(ReorderCommandProperty, value);

    static TabReorderBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<TabControl>((s, e) =>
        {
            if (e.NewValue is bool enabled && enabled)
            {
                s.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
                s.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
                s.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
                s.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel);

                DragDrop.SetAllowDrop(s, true);
                s.AddHandler(DragDrop.DropEvent, OnDrop);
            }
        });
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TabControl tabControl) return;

        // Only the left button initiates a reorder drag. Capturing on right-click would
        // steal the pointer from Avalonia's native context menu (e.g. "Detach Window").
        if (!e.GetCurrentPoint(tabControl).Properties.IsLeftButtonPressed) return;

        var visual = e.Source as Visual;
        _sourceItem = visual?.FindAncestorOfType<TabItem>();

        if (_sourceItem != null)
        {
            _state = DragState.Pressed;
            _startPos = e.GetPosition(tabControl);
            e.Pointer.Capture(tabControl);
        }
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not TabControl tabControl || _state != DragState.Pressed || _sourceItem == null) return;

        var currentPos = e.GetPosition(tabControl);
        var delta = currentPos - _startPos;

        if (Math.Abs(delta.X) > DragThreshold || Math.Abs(delta.Y) > DragThreshold)
        {
            _state = DragState.Dragging;
            e.Pointer.Capture(null);

#pragma warning disable CS0618
            var data = new DataObject();
            data.Set("TabItem", _sourceItem);
            data.Set("SourceIndex", tabControl.Items.Cast<object>().ToList().IndexOf(_sourceItem.Content!)); 

            DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
            _state = DragState.Idle;
            _sourceItem = null;
        }
    }

    private static void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only release capture if THIS behavior was the one tracking a press (i.e. it
        // actually captured the pointer in OnPointerPressed). This handler is registered
        // with Tunnel routing directly on the TabControl, so it fires for every
        // PointerReleased within its bounds -- including clicks on plain content controls
        // (ToggleSwitch, Button, ToggleButton) that captured the pointer to themselves.
        // Unconditionally calling Capture(null) here stole that capture before the release
        // ever reached those controls, so they could never detect "released within my
        // bounds" and their Click/IsCheckedChanged never fired.
        if (_state != DragState.Idle)
        {
            e.Pointer.Capture(null);
        }

        _state = DragState.Idle;
        _sourceItem = null;
    }

    // Safety net: if capture is lost for any reason other than our own explicit
    // release/transfer (e.g. a Popup or another control forcibly stealing it), the FSM
    // must still reset. Without this, a missed PointerReleased leaves _state stuck at
    // Pressed with the pointer captured by this TabControl, silently swallowing all
    // subsequent clicks on plain in-tree controls hosted in its content (ToggleSwitch,
    // Button, ToggleButton) until another full press/release cycle happens to clear it.
    private static void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _state = DragState.Idle;
        _sourceItem = null;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var data = e.Data;
        if (sender is not TabControl tabControl || !data.Contains("TabItem")) return;

        var sourceItem = data.Get("TabItem") as TabItem;
        if (sourceItem == null) return;

        // Ensure we are in the same TabControl (Intra-panel only)
        if (!tabControl.IsVisualAncestorOf(sourceItem)) return;

        var sourceIndex = (int)data.Get("SourceIndex")!;
#pragma warning restore CS0618
        
        // Determine target index based on drop position
        var dropPos = e.GetPosition(tabControl);
        var targetItem = e.Source as Visual;
        var targetTabItem = targetItem?.FindAncestorOfType<TabItem>();

        int targetIndex;
        if (targetTabItem != null)
        {
            targetIndex = tabControl.Items.Cast<object>().ToList().IndexOf(targetTabItem.Content!);
        }
        else
        {
            targetIndex = tabControl.Items.Cast<object>().Count() - 1;
        }

        if (sourceIndex != targetIndex)
        {
            string panelName = tabControl.Tag?.ToString() ?? "Unknown";
            var command = GetReorderCommand(tabControl);
            
            if (command != null && command.CanExecute($"{panelName}:{sourceIndex}:{targetIndex}"))
            {
                command.Execute($"{panelName}:{sourceIndex}:{targetIndex}");
            }
        }
    }
}
