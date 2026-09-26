using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Implementation of IToastNotificationService that handles the delay logic
/// and property change notifications for UI binding.
/// </summary>
public partial class ToastNotificationService : ObservableObject, IToastNotificationService
{
    [ObservableProperty]
    private string? _notificationMessage;

    [ObservableProperty]
    private bool _isNotificationVisible;

    public void ShowNotification(string message)
    {
        _ = ShowNotificationCoreAsync(message);
    }

    private async Task ShowNotificationCoreAsync(string message)
    {
        try
        {
            NotificationMessage = message;
            IsNotificationVisible = true;
            
            // Wait for 2.5 seconds before auto-hiding
            await Task.Delay(2500);
            
            // Prevent hiding if another notification triggered while waiting
            if (NotificationMessage == message)
            {
                IsNotificationVisible = false;
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToastNotificationService] Error: {ex.Message}");
        }
    }
}
