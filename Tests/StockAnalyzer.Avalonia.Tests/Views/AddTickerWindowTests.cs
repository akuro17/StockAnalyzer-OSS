using Avalonia.Headless.XUnit;
using Xunit;
using StockAnalyzer.Avalonia.Views.Dialogs;

namespace StockAnalyzer.Avalonia.Tests.Views;

public class AddTickerWindowTests
{
    [AvaloniaFact]
    public void AddTickerWindow_ShouldInitializeWithoutCrashing()
    {
        var window = new AddTickerWindow();
        Assert.NotNull(window);
    }
}
