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
    [InlineData(IndicatorType.ClothoidOscillator, "RawCurvatureRate")]
    [InlineData(IndicatorType.ClothoidMovingAverage, "EffectiveLag")]
    [InlineData(IndicatorType.ClothoidMovingAverage, "EffectiveSampleSize")]
    [InlineData(IndicatorType.CrossCorrelation, "OptimalLag")]
    [InlineData(IndicatorType.GrangerCausality, "DccCorrelation")]
    [InlineData(IndicatorType.JohansenCointegration, "CointegrationSpreadZScore")]
    [InlineData(IndicatorType.JarqueBera, "JBStatistic")]
    [InlineData(IndicatorType.LjungBoxQ, "QStatistic")]
    [InlineData(IndicatorType.DownsideDeviation, "RollingSortino")]
    [InlineData(IndicatorType.DownsideDeviation, "SemivarianceAsymmetry")]
    [InlineData(IndicatorType.BipowerVariation, "JumpRatio")]
    [InlineData(IndicatorType.ADFStationarity, "HalfLife")]
    [InlineData(IndicatorType.ADFStationarity, "Rho")]
    [InlineData(IndicatorType.VarianceRatio, "ZStarStatistic")]
    [InlineData(IndicatorType.VarianceRatio, "PValue")]
    public void IsSuppressed_ForNonChartedSeries_ReturnsTrue(IndicatorType type, string seriesName)
    {
        Assert.True(IndicatorChartSuppression.IsSuppressed(type, seriesName));
    }

    [Theory]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "SineWave")]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "LeadSine")]
    [InlineData(IndicatorType.PolarPhase, "NormalizedInPhase")]
    [InlineData(IndicatorType.PolarPhase, "NormalizedQuadrature")]
    [InlineData(IndicatorType.ClothoidOscillator, "Main")]
    [InlineData(IndicatorType.ClothoidOscillator, "Signal")]
    [InlineData(IndicatorType.ClothoidOscillator, "Histogram")]
    [InlineData(IndicatorType.ClothoidMovingAverage, "Main")]
    [InlineData(IndicatorType.CrossCorrelation, "Main")]
    [InlineData(IndicatorType.CrossCorrelation, "CrossCorrelation")]
    [InlineData(IndicatorType.RollingBeta, "Main")]
    [InlineData(IndicatorType.RollingBeta, "RollingCorrelation")]
    [InlineData(IndicatorType.JarqueBera, "Main")]
    [InlineData(IndicatorType.JarqueBera, "SignificanceLevel")]
    [InlineData(IndicatorType.LjungBoxQ, "Main")]
    [InlineData(IndicatorType.LjungBoxQ, "SignificanceLevel")]
    [InlineData(IndicatorType.SkewnessKurtosis, "Main")]
    [InlineData(IndicatorType.SkewnessKurtosis, "ExcessKurtosis")]
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

    [Theory]
    [InlineData(IndicatorType.IFFTInstantaneousPhase, "PhaseDelta", true)]
    [InlineData(IndicatorType.ClothoidMovingAverage, "EffectiveLag", true)]
    [InlineData(IndicatorType.CrossCorrelation, "OptimalLag", false)]
    [InlineData(IndicatorType.JarqueBera, "JBStatistic", false)]
    [InlineData(IndicatorType.LjungBoxQ, "QStatistic", false)]
    public void IsDataWindowSuppressed_CorrectlySuppressesOrAllows(IndicatorType type, string seriesName, bool expectedSuppressed)
    {
        Assert.Equal(expectedSuppressed, IndicatorChartSuppression.IsDataWindowSuppressed(type, seriesName));
    }
}
