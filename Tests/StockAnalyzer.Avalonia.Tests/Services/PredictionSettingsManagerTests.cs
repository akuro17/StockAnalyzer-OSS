using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public class PredictionSettingsManagerTests
{
    [Fact]
    public void DefaultWindowSize_Is75()
    {
        var manager = new PredictionSettingsManager();

        Assert.Equal(75, manager.WindowSize);
    }

    [Fact]
    public void SetWindowSize_ValidValue_UpdatesWindowSize()
    {
        var manager = new PredictionSettingsManager();

        manager.SetWindowSize(120);

        Assert.Equal(120, manager.WindowSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public void SetWindowSize_ZeroOrNegative_IsIgnored(int invalidValue)
    {
        var manager = new PredictionSettingsManager();

        manager.SetWindowSize(invalidValue);

        Assert.Equal(75, manager.WindowSize);
    }
}
