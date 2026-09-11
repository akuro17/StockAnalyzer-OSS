using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKalmanFilterParameter : CoreIndicatorParameterBase
{
    private decimal _q = 0.01m;

    [DisplayName("Process Noise (Q)")]
    [Description("Process noise covariance hyperparameter.")]
    [CoreParameterRange(0.0001, 1.0)]
    public decimal Q
    {
        get => _q;
        set => SetProperty(ref _q, value);
    }

    private decimal _r = 0.1m;

    [DisplayName("Measurement Noise (R)")]
    [Description("Measurement noise covariance hyperparameter.")]
    [CoreParameterRange(0.0001, 1.0)]
    public decimal R
    {
        get => _r;
        set => SetProperty(ref _r, value);
    }

    public override string GetDisplayName(string type) => $"{type} (Q:{Q}, R:{R})";

    public override void Validate()
    {
        if (Q < 0) throw new ArgumentOutOfRangeException(nameof(Q));
        if (R < 0) throw new ArgumentOutOfRangeException(nameof(R));
    }
}
