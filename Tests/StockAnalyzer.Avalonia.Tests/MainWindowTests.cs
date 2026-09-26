using Avalonia.Headless.XUnit;
using Xunit;
using StockAnalyzer.Avalonia.Views;

namespace StockAnalyzer.Avalonia.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindow_ShouldInitialize()
    {
        // Arrange & Act
        // Ensure that the Avalonia application is initialized in a headless environment.
        // [AvaloniaFact] handles the threading and context.
        var window = new MainWindow();
        
        // Assert
        Assert.NotNull(window);
    }
}
