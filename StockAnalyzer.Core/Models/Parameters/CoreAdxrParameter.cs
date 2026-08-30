using System;
using System.ComponentModel;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAdxrParameter : CoreIndicatorParameterBase
{
    private int _period = 14;

    [DisplayName("Period")]
    [Description("Period for Average Directional Index (ADX).")]
    [CoreParameterRange(1, 1000)]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _adxrPeriod = 14;

    [DisplayName("ADXR Period")]
    [Description("Smoothing period for Average Directional Movement Index Rating (ADXR).")]
    [CoreParameterRange(1, 1000)]
    public int AdxrPeriod
    {
        get => _adxrPeriod;
        set => SetProperty(ref _adxrPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {AdxrPeriod})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (AdxrPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(AdxrPeriod));
    }
}
