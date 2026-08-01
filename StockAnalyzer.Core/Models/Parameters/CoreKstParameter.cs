using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKstParameter : CoreIndicatorParameterBase
{
    // ROC Periods
    public int Roc1 { get; set; } = 10;
    public int Roc2 { get; set; } = 15;
    public int Roc3 { get; set; } = 20;
    public int Roc4 { get; set; } = 30;

    // SMA Periods
    public int Sma1 { get; set; } = 10;
    public int Sma2 { get; set; } = 10;
    public int Sma3 { get; set; } = 10;
    public int Sma4 { get; set; } = 15;

    public override string GetDisplayName(string type) => $"{type}";

    public override void Validate()
    {
        if (Roc1 <= 0 || Roc2 <= 0 || Roc3 <= 0 || Roc4 <= 0) throw new ArgumentOutOfRangeException("Roc Periods must be positive");
        if (Sma1 <= 0 || Sma2 <= 0 || Sma3 <= 0 || Sma4 <= 0) throw new ArgumentOutOfRangeException("Sma Periods must be positive");
    }
}
