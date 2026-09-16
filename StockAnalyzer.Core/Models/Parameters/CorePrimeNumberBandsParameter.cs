using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CorePrimeNumberBandsParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.PrimeNumberBandsPeriod;
    [CoreParameterRange(1, 1000)]
    [Range(1, 1000)]
    [DisplayName("Period")]
    [Description("Lookback period for highest high and lowest low detection.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private decimal _scaleMultiplier = IndicatorDefaultConstants.PrimeNumberBandsScaleMultiplier;
    [CoreParameterRange(1.0, 1000.0)]
    [Range(1.0, 1000.0)]
    [DisplayName("Scale Multiplier")]
    [Description("Multiplier to scale prices into integer domain for prime lookup.")]
    public decimal ScaleMultiplier
    {
        get => _scaleMultiplier;
        set => SetProperty(ref _scaleMultiplier, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {ScaleMultiplier})";

    public override void Validate()
    {
        if (Period < 1 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 1 and 1000.");
        if (ScaleMultiplier < 1.0m || ScaleMultiplier > 1000.0m)
            throw new ArgumentOutOfRangeException(nameof(ScaleMultiplier), "Scale multiplier must be between 1.0 and 1000.0.");
    }
}
