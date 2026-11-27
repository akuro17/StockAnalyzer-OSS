using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The window length shared by the price-native window averages (Trimmed, Winsorized, Huber, Biweight, Geometric, Harmonic moving average). The window holds the last
/// <see cref="Period"/> prices of the chosen price source; a window with an unusable price gives no value.
/// </summary>
public abstract class CoreWindowAverageParameterBase : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.WindowAverageDefaultPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.WindowAverageMinPeriod, IndicatorDefaultConstants.WindowAverageMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of prices in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    /// <summary>The setting shown after the period in the label, or an empty string.</summary>
    protected virtual string LabelSuffix() => string.Empty;

    public override string GetDisplayName(string type) => $"{type} ({Period}{LabelSuffix()})";

    // The first value is at bar index Period - 1.
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.WindowAverageMinPeriod, IndicatorDefaultConstants.WindowAverageMaxPeriod, nameof(Period));
    }
}
