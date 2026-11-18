using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreLjungBoxQParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.LjungBoxDefaultPeriod;
    private int _maxLag = IndicatorDefaultConstants.LjungBoxDefaultMaxLag;
    private double _alpha = IndicatorDefaultConstants.TestSignificanceDefault;

    [CoreParameterRange(IndicatorDefaultConstants.LjungBoxMinPeriod, IndicatorDefaultConstants.LjungBoxMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of log returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.LjungBoxMinMaxLag, IndicatorDefaultConstants.LjungBoxMaxMaxLag)]
    [DisplayName("Max Lag")]
    [Description("Highest autocorrelation lag m of the test. The lag actually tested is min(Max Lag, floor(Period / 5)); below 1 the bar has no value.")]
    [Category("Estimation")]
    public int MaxLag
    {
        get => _maxLag;
        set => SetProperty(ref _maxLag, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.TestSignificanceMin, IndicatorDefaultConstants.TestSignificanceMax)]
    [DisplayName("Significance Level")]
    [Description("Level of the reference line drawn on the p-value (a p-value below it rejects the absence of autocorrelation).")]
    [Category("Estimation")]
    public double Alpha
    {
        get => _alpha;
        set => SetProperty(ref _alpha, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {MaxLag})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.LjungBoxMinPeriod, IndicatorDefaultConstants.LjungBoxMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(MaxLag, IndicatorDefaultConstants.LjungBoxMinMaxLag, IndicatorDefaultConstants.LjungBoxMaxMaxLag, nameof(MaxLag));
        if (!(Alpha >= IndicatorDefaultConstants.TestSignificanceMin && Alpha <= IndicatorDefaultConstants.TestSignificanceMax))
        {
            throw new ArgumentOutOfRangeException(nameof(Alpha));
        }
    }
}
