using System.ComponentModel;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Interface for a service that manages temporary toast notification messages.
/// Ensures standard MVVM bindings to NotificationMessage and IsNotificationVisible.
/// </summary>
public interface IToastNotificationService : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the current notification message to display.
    /// </summary>
    string? NotificationMessage { get; }

    /// <summary>
    /// Gets a value indicating whether the notification should be visible.
    /// </summary>
    bool IsNotificationVisible { get; }

    /// <summary>
    /// Displays a temporary notification message that auto-hides after a delay.
    /// </summary>
    /// <param name="message">The message to display.</param>
    void ShowNotification(string message);
}
