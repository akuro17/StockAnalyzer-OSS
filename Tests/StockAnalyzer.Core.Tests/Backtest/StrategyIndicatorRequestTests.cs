using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class StrategyIndicatorRequestTests
{
    [Fact]
    public void Frame_OmittedByExistingThreeArgConstruction_DefaultsToNull()
    {
        // Every pre-existing call site (NoOpBacktestStrategy wiring, BacktestEngineTests,
        // BacktestAcceptanceTests) constructs this with exactly 3 positional args — the 2026-09-18
        // trailing optional Frame parameter (plan section 3.2.4 / Task 2a) must not change that.
        var request = new StrategyIndicatorRequest("sma", IndicatorType.SMA, null);

        Assert.Null(request.Frame);
    }

    [Fact]
    public void Frame_WhenSupplied_RoundTrips()
    {
        var request = new StrategyIndicatorRequest("weekly-sma", IndicatorType.SMA, null, TimeFrame.W1);

        Assert.Equal(TimeFrame.W1, request.Frame);
    }

    [Fact]
    public void OutputName_OmittedByExistingThreeOrFourArgConstruction_DefaultsToMain()
    {
        // Task 6a (Y:\Temp\sa_implementation_plan_BacktestOutputNameSupport.md) safe-extension: every
        // pre-existing call site (3-arg and 4-arg, including the Frame-only Task 2a call sites) must not
        // change behavior.
        var threeArg = new StrategyIndicatorRequest("sma", IndicatorType.SMA, null);
        var fourArg = new StrategyIndicatorRequest("weekly-sma", IndicatorType.SMA, null, TimeFrame.W1);

        Assert.Equal(IndicatorResult.MainSeriesName, threeArg.OutputName);
        Assert.Equal(IndicatorResult.MainSeriesName, fourArg.OutputName);
    }

    [Fact]
    public void OutputName_WhenSupplied_RoundTrips()
    {
        var request = new StrategyIndicatorRequest("macd-signal", IndicatorType.MACD, null, null, "Signal");

        Assert.Equal("Signal", request.OutputName);
    }
}
