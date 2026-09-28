using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// Y:\Temp\sa_fix_plan_IchimokuFutureOffset.md / sa_improvement_plan_IchimokuFutureProjectionBars.md: the reserved right margin must cover the Senkou cloud, which extends
/// Displacement bars past the last candle, and the ViewModel must obtain that length from the parameter object rather than from an indicator-specific special case.
/// </summary>
public class ChartViewModelIchimokuFutureOffsetTests
{
    private static CoreIndicatorSettings Ichimoku(int? displacement)
    {
        var parameter = new CoreIchimokuParameter();
        if (displacement.HasValue) parameter.Offset = displacement.Value;
        return new CoreIndicatorSettings { TypeEnum = IndicatorType.Ichimoku, IsEnabled = true, IsOverlay = true, ParameterObject = parameter };
    }

    private static ChartViewModel CreateWith(params CoreIndicatorSettings[] indicators)
    {
        var vm = new ChartViewModel();
        foreach (CoreIndicatorSettings indicator in indicators) vm.Indicators.Add(indicator);
        vm.RecalculateFutureOffset();
        return vm;
    }

    [Fact]
    public void RecalculateFutureOffset_DefaultDisplacement_ReservesTheStandardCloudPlusBuffer()
    {
        ChartViewModel vm = CreateWith(Ichimoku(null));

        Assert.Equal(CoreIchimokuParameter.DefaultDisplacement + ChartConstants.FutureProjectionBuffer, vm.RequiredFutureOffset);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(100)]
    public void RecalculateFutureOffset_FollowsDisplacement(int displacement)
    {
        ChartViewModel vm = CreateWith(Ichimoku(displacement));

        Assert.Equal(displacement + ChartConstants.FutureProjectionBuffer, vm.RequiredFutureOffset);
    }

    [Fact]
    public void RecalculateFutureOffset_ZeroDisplacement_ReservesNothing()
    {
        // No cloud extends past the last candle, so there is nothing to project (the buffer only applies to an actual projection).
        Assert.Equal(0, CreateWith(Ichimoku(0)).RequiredFutureOffset);
    }

    [Fact]
    public void RecalculateFutureOffset_IndicatorWithoutProjection_ReservesNothing()
    {
        var sma = new CoreIndicatorSettings { TypeEnum = IndicatorType.SMA, IsEnabled = true, IsOverlay = true, ParameterObject = new CoreSmaParameter() };

        Assert.Equal(0, CreateWith(sma).RequiredFutureOffset);
    }

    [Fact]
    public void RecalculateFutureOffset_MultipleIndicators_UsesTheLongestProjection()
    {
        ChartViewModel vm = CreateWith(Ichimoku(10), Ichimoku(40));

        Assert.Equal(40 + ChartConstants.FutureProjectionBuffer, vm.RequiredFutureOffset);
    }

    [Fact]
    public void RecalculateFutureOffset_IchimokuWithoutParameterObject_ReservesNothing()
    {
        var bare = new CoreIndicatorSettings { TypeEnum = IndicatorType.Ichimoku, IsEnabled = true, IsOverlay = true };

        Assert.Equal(0, CreateWith(bare).RequiredFutureOffset);
    }
}
