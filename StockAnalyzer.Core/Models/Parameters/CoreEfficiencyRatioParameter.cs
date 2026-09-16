using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEfficiencyRatioParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.EfficiencyRatioPeriod;

    [DisplayName("Period")]
    [Description("Lookback period for Kaufman Efficiency Ratio.")]
    [CoreParameterRange(2, 10000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period < 2 || Period > 10000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 2 and 10000.");
    }
}
