using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKstParameter : CoreIndicatorParameterBase
{
    // ROC Periods
    [DisplayName("ROC 1")]
    [Description("Rate of Change period 1.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc1 { get; set; } = 10;

    [DisplayName("ROC 2")]
    [Description("Rate of Change period 2.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc2 { get; set; } = 15;

    [DisplayName("ROC 3")]
    [Description("Rate of Change period 3.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc3 { get; set; } = 20;

    [DisplayName("ROC 4")]
    [Description("Rate of Change period 4.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc4 { get; set; } = 30;

    // SMA Periods
    [DisplayName("SMA 1")]
    [Description("Smoothing SMA period 1.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma1 { get; set; } = 10;

    [DisplayName("SMA 2")]
    [Description("Smoothing SMA period 2.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma2 { get; set; } = 10;

    [DisplayName("SMA 3")]
    [Description("Smoothing SMA period 3.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma3 { get; set; } = 10;

    [DisplayName("SMA 4")]
    [Description("Smoothing SMA period 4.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma4 { get; set; } = 15;

    public override string GetDisplayName(string type) => $"{type}";

    public override void Validate()
    {
        if (Roc1 <= 0 || Roc2 <= 0 || Roc3 <= 0 || Roc4 <= 0) throw new ArgumentOutOfRangeException("Roc Periods must be positive");
        if (Sma1 <= 0 || Sma2 <= 0 || Sma3 <= 0 || Sma4 <= 0) throw new ArgumentOutOfRangeException("Sma Periods must be positive");
    }
}
