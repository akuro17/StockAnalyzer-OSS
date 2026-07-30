using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAnchoredVwapParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(0, 1000000)]
    public int AnchorIndex { get; set; } = 0;

    public override string GetDisplayName(string type) => $"{type} (Idx: {AnchorIndex})";

    public override void Validate()
    {
        if (AnchorIndex < 0) throw new ArgumentOutOfRangeException(nameof(AnchorIndex));
    }
}
