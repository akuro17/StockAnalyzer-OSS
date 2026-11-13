using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreVwapParameter : CoreIndicatorParameterBase
{
    private VwapResetPeriod _resetPeriod = VwapResetPeriod.Cumulative;

    [DisplayName("Reset Period")]
    [Description("When the cumulative sums restart: Cumulative (never), Weekly (ISO week), Monthly, Quarterly or Yearly (calendar periods of the bar timestamp).")]
    public VwapResetPeriod ResetPeriod
    {
        get => _resetPeriod;
        set => SetProperty(ref _resetPeriod, value);
    }

    private bool _showBands;

    [DisplayName("Show Bands")]
    [Description("Calculate and draw the UpperBand and LowerBand: VWAP plus or minus the multiplier times the volume-weighted standard deviation within the period. When off, only the VWAP line is calculated.")]
    public bool ShowBands
    {
        get => _showBands;
        set => SetProperty(ref _showBands, value);
    }

    private decimal _stdDevMultiplier = IndicatorDefaultConstants.VwapStdDevMultiplier;

    [DisplayName("StdDev Multiplier")]
    [Description("Band width in volume-weighted standard deviations. Used only when Show Bands is on.")]
    [CoreParameterRange(IndicatorDefaultConstants.VwapMinStdDevMultiplier, IndicatorDefaultConstants.VwapMaxStdDevMultiplier)]
    public decimal StdDevMultiplier
    {
        get => _stdDevMultiplier;
        set => SetProperty(ref _stdDevMultiplier, value);
    }

    public override string GetDisplayName(string type) => ResetPeriod == VwapResetPeriod.Cumulative ? type : $"{type} ({ResetPeriod})";

    public override void Validate()
    {
        if (!Enum.IsDefined(ResetPeriod)) throw new ArgumentOutOfRangeException(nameof(ResetPeriod));
        if (StdDevMultiplier < (decimal)IndicatorDefaultConstants.VwapMinStdDevMultiplier || StdDevMultiplier > (decimal)IndicatorDefaultConstants.VwapMaxStdDevMultiplier)
            throw new ArgumentOutOfRangeException(nameof(StdDevMultiplier));
    }
}
