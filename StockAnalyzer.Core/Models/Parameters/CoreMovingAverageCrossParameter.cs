using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameters for the Moving Average Cross indicator: the fast and slow moving-average lengths.
/// Named per the CoreXxxIndicator -> CoreXxxParameter convention so it is auto-discovered by
/// <see cref="Indicators.CoreIndicatorBase.GetDefaultSettings"/>.
/// </summary>
public class CoreMovingAverageCrossParameter : CoreIndicatorParameterBase
{
    private int _shortPeriod = 10;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Short Period")]
    [Description("Fast moving-average length for the cross.")]
    [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
    public int ShortPeriod
    {
        get => _shortPeriod;
        set => SetProperty(ref _shortPeriod, value);
    }

    private int _longPeriod = 20;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Long Period")]
    [Description("Slow moving-average length for the cross.")]
    public int LongPeriod
    {
        get => _longPeriod;
        set => SetProperty(ref _longPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({ShortPeriod}, {LongPeriod})";

    public override void Validate()
    {
        if (ShortPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(ShortPeriod), "ShortPeriod must be positive");
        if (LongPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(LongPeriod), "LongPeriod must be positive");
    }
}
