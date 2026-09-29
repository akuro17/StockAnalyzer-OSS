using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Covers the last-resort fallback in <see cref="CoreIndicatorBase.GetDefaultSettings"/> that attaches a
/// <see cref="CoreSmaParameter"/> to indicators which expose a writable <c>int Period</c> but have neither a
/// naming-convention parameter class nor a <see cref="DefaultCoreIndicatorSettings"/> entry.
/// Previously these rendered as "No parameters" in the Indicator Manager / properties dialogs.
/// </summary>
public class IndicatorDefaultParameterFallbackTests
{
    [Theory]
    [InlineData(IndicatorType.CCI, 20)]
    [InlineData(IndicatorType.CMO, 14)]
    [InlineData(IndicatorType.Momentum, 10)]
    [InlineData(IndicatorType.NATR, 14)]
    [InlineData(IndicatorType.VolumeMA, 20)]
    public void GetDefaultSettings_PeriodOnlyIndicator_SeedsCoreSmaParameterWithClassDefault(
        IndicatorType type, int expectedPeriod)
    {
        var indicator = IndicatorFactory.Default.Create(type);
        Assert.NotNull(indicator);

        var settings = indicator!.GetDefaultSettings();

        var param = Assert.IsType<CoreSmaParameter>(settings.ParameterObject);
        Assert.Equal(expectedPeriod, param.Period);
    }

    /// <summary>
    /// TRIX has its own naming-convention parameter class (<see cref="CoreTrixParameter"/>), discovered
    /// ahead of the CoreSmaParameter last-resort fallback, because its true warmup requirement
    /// (3*Period-1, see GetRequiredWarmupBars) differs from a plain single-Period indicator's.
    /// </summary>
    [Fact]
    public void GetDefaultSettings_Trix_SeedsCoreTrixParameterWithClassDefault()
    {
        var indicator = IndicatorFactory.Default.Create(IndicatorType.TRIX);
        Assert.NotNull(indicator);

        var settings = indicator!.GetDefaultSettings();

        var param = Assert.IsType<CoreTrixParameter>(settings.ParameterObject);
        Assert.Equal(15, param.Period);
    }

    [Theory]
    [InlineData(IndicatorType.OBV)]
    [InlineData(IndicatorType.BOP)]
    [InlineData(IndicatorType.ADL)]
    public void GetDefaultSettings_PeriodlessIndicator_LeavesParameterObjectNull(IndicatorType type)
    {
        var indicator = IndicatorFactory.Default.Create(type);
        Assert.NotNull(indicator);

        var settings = indicator!.GetDefaultSettings();

        Assert.Null(settings.ParameterObject);
    }
}
