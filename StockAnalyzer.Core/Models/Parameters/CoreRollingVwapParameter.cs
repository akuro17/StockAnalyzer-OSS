using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreRollingVwapParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.RollingVwapPeriod;

    [CoreParameterRange(1, 10000)]
    [Range(1, 10000)]
    [DisplayName("Period")]
    [Description("Lookback period for the Rolling VWAP calculation.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0 || Period > 10000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 1 and 10000");
    }
}
