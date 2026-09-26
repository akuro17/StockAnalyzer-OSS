using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Indicators;

/// <summary>
/// Locks the single source of truth for chart-suppressed indicator series. The set must match the
/// logic previously inlined in IndicatorRenderer (3 sites) and PanelValueRangeCalculator (1 site).
/// </summary>
public class IndicatorChartSuppressionTests
{
    [Theory]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "Main")]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "PhaseDelta")]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "LocalPeriod")]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "PhaseStability")]
    [InlineData(IndicatorType.PolarPhase, "Main")]
    [InlineData(IndicatorType.PolarPhase, "PhaseStability")]
    public void IsSuppressed_ForNonChartedSeries_ReturnsTrue(IndicatorType type, string seriesName)
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(type, seriesName));
    }

    [Theory]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "SineWave")]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "LeadSine")]
    [InlineData(IndicatorType.PolarPhase, "NormalizedInPhase")]
    [InlineData(IndicatorType.PolarPhase, "NormalizedQuadrature")]
    public void IsSuppressed_ForChartedSeries_ReturnsFalse(IndicatorType type, string seriesName)
    {
        Assert.False(IndicatorChartSuppression.IsSuppressed(type, seriesName));
    }

    [Fact]
    public void IsSuppressed_ForUnrelatedIndicatorType_ReturnsFalse()
    {
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.SMA, "Main"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.PolarCycleAngularFrequency, "Main"));
    }

    [Fact]
    public void IsSuppressed_ForNullType_ReturnsFalse()
    {
        Assert.False(IndicatorChartSuppression.IsSuppressed(null, "Main"));
    }

    [Fact]
    public void IsSuppressed_IsCaseSensitive_Ordinal()
    {
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.PolarPhase, "main"));
        Assert.False(IndicatorChartSuppression.IsSuppressed(IndicatorType.IFFTInstantaneousPhase, "phasestability"));
    }
}
