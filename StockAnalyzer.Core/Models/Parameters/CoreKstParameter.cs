using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKstParameter : CoreIndicatorParameterBase
{
    // ROC Periods
    private int _roc1 = 10;

    [DisplayName("ROC 1")]
    [Description("Rate of Change period 1.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc1
    {
        get => _roc1;
        set => SetProperty(ref _roc1, value);
    }

    private int _roc2 = 15;

    [DisplayName("ROC 2")]
    [Description("Rate of Change period 2.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc2
    {
        get => _roc2;
        set => SetProperty(ref _roc2, value);
    }

    private int _roc3 = 20;

    [DisplayName("ROC 3")]
    [Description("Rate of Change period 3.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc3
    {
        get => _roc3;
        set => SetProperty(ref _roc3, value);
    }

    private int _roc4 = 30;

    [DisplayName("ROC 4")]
    [Description("Rate of Change period 4.")]
    [Category("ROC Periods")]
    [CoreParameterRange(1, 1000)]
    public int Roc4
    {
        get => _roc4;
        set => SetProperty(ref _roc4, value);
    }

    // SMA Periods
    private int _sma1 = 10;

    [DisplayName("SMA 1")]
    [Description("Smoothing SMA period 1.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma1
    {
        get => _sma1;
        set => SetProperty(ref _sma1, value);
    }

    private int _sma2 = 10;

    [DisplayName("SMA 2")]
    [Description("Smoothing SMA period 2.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma2
    {
        get => _sma2;
        set => SetProperty(ref _sma2, value);
    }

    private int _sma3 = 10;

    [DisplayName("SMA 3")]
    [Description("Smoothing SMA period 3.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma3
    {
        get => _sma3;
        set => SetProperty(ref _sma3, value);
    }

    private int _sma4 = 15;

    [DisplayName("SMA 4")]
    [Description("Smoothing SMA period 4.")]
    [Category("SMA Periods")]
    [CoreParameterRange(1, 1000)]
    public int Sma4
    {
        get => _sma4;
        set => SetProperty(ref _sma4, value);
    }

    public override string GetDisplayName(string type) => $"{type}";

    public override void Validate()
    {
        if (Roc1 <= 0 || Roc2 <= 0 || Roc3 <= 0 || Roc4 <= 0) throw new ArgumentOutOfRangeException("Roc Periods must be positive");
        if (Sma1 <= 0 || Sma2 <= 0 || Sma3 <= 0 || Sma4 <= 0) throw new ArgumentOutOfRangeException("Sma Periods must be positive");
    }
}
