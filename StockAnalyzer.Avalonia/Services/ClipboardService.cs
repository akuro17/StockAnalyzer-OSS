using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Implementation of IClipboardService for Avalonia.
/// </summary>
public class ClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
            desktop.MainWindow?.Clipboard != null)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(text);
        }
        else
        {
            // Fallback for cases where MainWindow/Clipboard is not available
            System.Diagnostics.Debug.WriteLine("[ClipboardService] Clipboard not available or empty text.");
        }
    }
}
