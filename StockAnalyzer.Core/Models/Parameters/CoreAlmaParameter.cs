using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAlmaParameter : CoreIndicatorParameterBase
{
    private int _period = 9;
    [CoreParameterRange(1, 10000)]
    [DisplayName("Period")]
    [Description("Number of periods for the Arnaud Legoux Moving Average.")]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    private double _offset = 0.85;
    [CoreParameterRange(0.01, 1.0)]
    [DisplayName("Offset")]
    [Description("Gaussian offset (0 to 1).")]
    public double Offset 
    { 
        get => _offset; 
        set => SetProperty(ref _offset, value); 
    }

    private double _sigma = 6.0;
    [CoreParameterRange(1.0, 100.0)]
    [DisplayName("Sigma")]
    [Description("Gaussian sigma.")]
    public double Sigma 
    { 
        get => _sigma; 
        set => SetProperty(ref _sigma, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Offset}, {Sigma})";

    public override void Validate()
    {
        if (Period <= 0) throw new ArgumentOutOfRangeException(nameof(Period));
        if (Offset <= 0 || Offset > 1) throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Sigma <= 0) throw new ArgumentOutOfRangeException(nameof(Sigma));
    }
}
