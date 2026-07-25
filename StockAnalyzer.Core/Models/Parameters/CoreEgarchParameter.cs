using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEgarchParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(1, 1000)]
    public int Period { get; set; } = 14;
    public double Omega { get; set; } = 0.1;
    public double Alpha { get; set; } = 0.2;
    public double Beta { get; set; } = 0.7;
    public double Gamma { get; set; } = 0.1;

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
    }
}
