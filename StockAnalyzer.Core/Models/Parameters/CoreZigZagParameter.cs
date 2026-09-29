using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreZigZagParameter : CoreIndicatorParameterBase
{
    private decimal _threshold = 5.0m;

    [DisplayName("Threshold (%)")]
    [Description("Minimum price change percentage required to form a new ZigZag leg.")]
    [CoreParameterRange(0.1, 100.0)]
    public decimal Threshold
    {
        get => _threshold;
        set => SetProperty(ref _threshold, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Threshold}%)";

    public override void Validate()
    {
        if (Threshold <= 0) throw new ArgumentOutOfRangeException(nameof(Threshold));
    }
}
