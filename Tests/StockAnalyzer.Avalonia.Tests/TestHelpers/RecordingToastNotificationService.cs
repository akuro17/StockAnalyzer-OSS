using System.Collections.Generic;
using System.ComponentModel;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>Shared <see cref="IToastNotificationService"/> test double: keeps every message shown, in order.</summary>
public sealed class RecordingToastNotificationService : IToastNotificationService
{
    public List<string> Messages { get; } = new();
    public string? NotificationMessage { get; private set; }
    public bool IsNotificationVisible { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void ShowNotification(string message)
    {
        Messages.Add(message);
        NotificationMessage = message;
        IsNotificationVisible = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationMessage)));
    }
}
