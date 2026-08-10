using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKalmanFilterParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(0.0001, 1.0)]
    public decimal Q { get; set; } = 0.01m;

    [CoreParameterRange(0.0001, 1.0)]
    public decimal R { get; set; } = 0.1m;

    public override string GetDisplayName(string type) => $"{type} (Q:{Q}, R:{R})";

    public override void Validate()
    {
        if (Q < 0) throw new ArgumentOutOfRangeException(nameof(Q));
        if (R < 0) throw new ArgumentOutOfRangeException(nameof(R));
    }
}
