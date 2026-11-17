using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreHurstExponentParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.HurstDefaultPeriod;
    private int _neweyWestLag = IndicatorDefaultConstants.HurstDefaultNeweyWestLag;
    private int _varianceRatioLag = IndicatorDefaultConstants.HurstDefaultVarianceRatioLag;

    [CoreParameterRange(IndicatorDefaultConstants.HurstMinPeriod, IndicatorDefaultConstants.HurstMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of log returns in the window (bars). The minimum keeps at least three sub-interval lengths, so that the regression is defined. A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.HurstMinNeweyWestLag, IndicatorDefaultConstants.HurstMaxNeweyWestLag)]
    [DisplayName("Newey-West Lag")]
    [Description("Truncation lag q for Lo's Modified R/S long-run variance estimator. 0 selects the automatic Newey-West bandwidth floor(4 * (n / 100)^(2/9)).")]
    [Category("Estimation")]
    public int NeweyWestLag
    {
        get => _neweyWestLag;
        set => SetProperty(ref _neweyWestLag, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.HurstMinVarianceRatioLag, IndicatorDefaultConstants.HurstMaxVarianceRatioLag)]
    [DisplayName("Variance Ratio Lag")]
    [Description("Aggregation horizon q (bars) for Lo-MacKinlay heteroskedasticity-robust Variance Ratio VR(q) and Z*(q).")]
    [Category("Estimation")]
    public int VarianceRatioLag
    {
        get => _varianceRatioLag;
        set => SetProperty(ref _varianceRatioLag, value);
    }

    public override string GetDisplayName(string type) =>
        NeweyWestLag == IndicatorDefaultConstants.HurstDefaultNeweyWestLag &&
        VarianceRatioLag == IndicatorDefaultConstants.HurstDefaultVarianceRatioLag
            ? $"{type} ({Period})"
            : $"{type} ({Period}, NW:{NeweyWestLag}, VR:{VarianceRatioLag})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.HurstMinPeriod, IndicatorDefaultConstants.HurstMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(NeweyWestLag, IndicatorDefaultConstants.HurstMinNeweyWestLag, IndicatorDefaultConstants.HurstMaxNeweyWestLag, nameof(NeweyWestLag));
        ThrowIfOutOfRange(VarianceRatioLag, IndicatorDefaultConstants.HurstMinVarianceRatioLag, IndicatorDefaultConstants.HurstMaxVarianceRatioLag, nameof(VarianceRatioLag));
    }
}
