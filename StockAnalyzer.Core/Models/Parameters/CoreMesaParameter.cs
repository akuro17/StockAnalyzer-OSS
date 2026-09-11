using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreMesaParameter : CoreIndicatorParameterBase
{
    private decimal _fastLimit = 0.5m;

    [DisplayName("Fast Limit")]
    [Description("Fast limit coefficient for MESA adaptive filter.")]
    [CoreParameterRange(0.01, 1.0)]
    public decimal FastLimit
    {
        get => _fastLimit;
        set => SetProperty(ref _fastLimit, value);
    }

    private decimal _slowLimit = 0.05m;

    [DisplayName("Slow Limit")]
    [Description("Slow limit coefficient for MESA adaptive filter.")]
    [CoreParameterRange(0.001, 1.0)]
    public decimal SlowLimit
    {
        get => _slowLimit;
        set => SetProperty(ref _slowLimit, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({FastLimit}, {SlowLimit})";

    public override void Validate()
    {
        if (FastLimit <= 0) throw new ArgumentOutOfRangeException(nameof(FastLimit));
        if (SlowLimit <= 0) throw new ArgumentOutOfRangeException(nameof(SlowLimit));
    }
}
