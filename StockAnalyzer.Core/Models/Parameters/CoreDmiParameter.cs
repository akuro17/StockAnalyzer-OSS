using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreDmiParameter : CoreIndicatorParameterBase
{
    private int _period = 14;

    [DisplayName("Period")]
    [Description("Period for Directional Movement Index (DMI).")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
