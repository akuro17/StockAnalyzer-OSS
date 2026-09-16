using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Regression coverage for <see cref="CoreIndicatorBase.GetDefaultSettings"/>'s naming-convention
/// parameter discovery (e.g. <c>CoreSmaIndicator</c> -&gt; <c>CoreSmaParameter</c>): the discovered
/// parameter object's <c>Period</c> must match the indicator class's own hardcoded <c>Period</c>
/// default. Bug: <see cref="CoreSmaParameter"/>'s own field default (14, shared by inheritance with
/// <see cref="CoreEmaParameter"/>) had drifted out of sync with <c>CoreSmaIndicator</c>/
/// <c>CoreEmaIndicator</c>'s own field default (20), so a freshly-added, never-customized SMA or EMA
/// channel/indicator would display and round-trip Period=14 in every UI that reads
/// <c>GetDefaultSettings()</c> (Training Wizard parameter picker, Indicator Settings dialog "add"
/// flow) while actually computing with Period=20 wherever <c>Create(type, parameters: null)</c> was
/// used instead. Fixed by seeding the discovered parameter object's <c>Period</c> from the
/// indicator's own current <c>Period</c> value, without changing either class's own default field
/// (which <c>IndicatorRegistrationViewModel.BindToSide</c>'s separate generic fallback still relies
/// on unchanged).
/// </summary>
public class CoreIndicatorBaseGetDefaultSettingsTests
{
    [Theory]
    [InlineData(IndicatorType.SMA)]
    [InlineData(IndicatorType.EMA)]
    public void GetDefaultSettings_PeriodMatchesIndicatorClassOwnDefault(IndicatorType type)
    {
        var indicator = IndicatorFactory.Default.Create(type);
        Assert.NotNull(indicator);

        var settings = indicator!.GetDefaultSettings();

        var indicatorPeriod = (int)indicator.GetType().GetProperty("Period")!.GetValue(indicator)!;
        var param = Assert.IsAssignableFrom<CoreSmaParameter>(settings.ParameterObject);
        Assert.Equal(indicatorPeriod, param.Period);
        Assert.Equal(20, param.Period);
    }

    [Fact]
    public void GetDefaultSettings_Rsi_PeriodStillMatchesItsOwnDefault()
    {
        // RSI's own indicator-class default (14) already agreed with CoreRsiParameter's inherited
        // CoreSmaParameter default before this fix; the seeding step must be a no-op here, not
        // introduce a new mismatch of its own.
        var indicator = IndicatorFactory.Default.Create(IndicatorType.RSI);
        Assert.NotNull(indicator);

        var settings = indicator!.GetDefaultSettings();

        var param = Assert.IsAssignableFrom<CoreSmaParameter>(settings.ParameterObject);
        Assert.Equal(14, param.Period);
    }
}
