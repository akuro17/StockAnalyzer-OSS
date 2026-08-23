using System;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.Behaviors;

/// <summary>
/// Attached behavior for Windows to handle geometry persistence and restoration messaging.
/// </summary>
public static class WindowPersistenceBehavior
{
    public static readonly AttachedProperty<bool> PersistenceEnabledProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("PersistenceEnabled", typeof(WindowPersistenceBehavior));

    public static bool GetPersistenceEnabled(Window element) => element.GetValue(PersistenceEnabledProperty);
    public static void SetPersistenceEnabled(Window element, bool value) => element.SetValue(PersistenceEnabledProperty, value);

    static WindowPersistenceBehavior()
    {
        PersistenceEnabledProperty.Changed.AddClassHandler<Window>((sender, e) =>
        {
            if (e.NewValue is bool enabled && enabled)
            {
                sender.Closing += OnWindowClosing;
            }
            else if (e.OldValue is bool wasEnabled && wasEnabled)
            {
                sender.Closing -= OnWindowClosing;
            }
        });
    }

    private static void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is Window window)
        {
            if (window.DataContext is WorkspaceViewItem item)
            {
                ProcessItem(window, item, e);
            }
            else if (window.DataContext is DetachedWindowViewModel vm)
            {
                // Capture geometry for the first item (representative of the window)
                // In a multi-tab window, all tabs share the same last known detached geometry
                foreach (var vmItem in vm.Items)
                {
                    ProcessItem(window, vmItem, e);
                }
            }
        }
    }

    private static void ProcessItem(Window window, WorkspaceViewItem item, WindowClosingEventArgs e)
    {
        // Capture geometry for persistence
        item.DetachedX = window.Position.X;
        item.DetachedY = window.Position.Y;
        item.DetachedWidth = window.Bounds.Width;
        item.DetachedHeight = window.Bounds.Height;

        // Only send the restore request if the user personally closes the window (not app shutdown cascaded close)
        if (window.IsActive && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            WeakReferenceMessenger.Default.Send(new RestoreRequestMessage(item));
        }
    }
}
