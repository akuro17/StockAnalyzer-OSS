using Avalonia;
using Avalonia.Controls;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// The toast notification overlay shared by the views that show <see cref="IToastNotificationService"/> messages:
/// a rounded panel with an optional leading icon and the message, visible while the service reports a notification.
/// The host positions it (alignment, margin, grid placement, ZIndex) and binds <see cref="Service"/>; a null service
/// keeps it hidden.
/// </summary>
public partial class ToastOverlay : UserControl
{
    public static readonly StyledProperty<IToastNotificationService?> ServiceProperty =
        AvaloniaProperty.Register<ToastOverlay, IToastNotificationService?>(nameof(Service));

    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<ToastOverlay, string?>(nameof(Icon));

    public ToastOverlay()
    {
        InitializeComponent();
    }

    /// <summary>The service whose <see cref="IToastNotificationService.NotificationMessage"/> is shown.</summary>
    public IToastNotificationService? Service
    {
        get => GetValue(ServiceProperty);
        set => SetValue(ServiceProperty, value);
    }

    /// <summary>Leading glyph shown before the message; no icon when null or empty.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
