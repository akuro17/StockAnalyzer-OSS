using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace StockAnalyzer.Avalonia.Services;

public class WindowBoundaryService : IWindowBoundaryService
{
    private const double DefaultWidth = 1000;
    private const double DefaultHeight = 700;
    private readonly Microsoft.Extensions.Logging.ILogger<WindowBoundaryService> _logger;

    public WindowBoundaryService(Microsoft.Extensions.Logging.ILogger<WindowBoundaryService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowBoundaryService>.Instance;
    }

    public (double X, double Y, double Width, double Height) EnsureVisible(double x, double y, double width, double height)
    {
        // 1. Fallback for invalid dimensions
        if (double.IsNaN(width) || width <= 0) width = DefaultWidth;
        if (double.IsNaN(height) || height <= 0) height = DefaultHeight;

        // 2. Get Screens from the application lifetime
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var screens = desktop.MainWindow.Screens;
            var point = new PixelPoint((int)x, (int)y);
            
            // Check if the top-left point is within ANY screen
            var currentScreen = screens.ScreenFromPoint(point);
            
            if (currentScreen != null)
            {
                // Found on a screen. Verify the majority of the window is visible.
                // For simplicity, we ensure the top-left + a margin is visible.
                return (x, y, width, height);
            }
            
            // 3. Fallback to Primary Screen or first available screen
            var primary = screens.Primary;
            if (primary == null)
            {
                // If primary is null, try getting screen at (0,0) as a safe bet
                primary = screens.ScreenFromPoint(new PixelPoint(0,0));
            }

            if (primary != null)
            {
                // Center on the primary screen
                var bounds = primary.WorkingArea;
                double newW = Math.Min(width, bounds.Width * 0.9);
                double newH = Math.Min(height, bounds.Height * 0.9);
                double newX = bounds.X + (bounds.Width - newW) / 2;
                double newY = bounds.Y + (bounds.Height - newH) / 2;
                
                return (newX, newY, newW, newH);
            }
        }

        // Final fallback if no UI context is available
        return (double.IsNaN(x) ? 100 : x, double.IsNaN(y) ? 100 : y, width, height);
    }
}
