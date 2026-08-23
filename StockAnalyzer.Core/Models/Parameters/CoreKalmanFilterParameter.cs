using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreKalmanFilterParameter : CoreIndicatorParameterBase
{
    [DisplayName("Process Noise (Q)")]
    [Description("Process noise covariance hyperparameter.")]
    [CoreParameterRange(0.0001, 1.0)]
    public decimal Q { get; set; } = 0.01m;

    [DisplayName("Measurement Noise (R)")]
    [Description("Measurement noise covariance hyperparameter.")]
    [CoreParameterRange(0.0001, 1.0)]
    public decimal R { get; set; } = 0.1m;

    public override string GetDisplayName(string type) => $"{type} (Q:{Q}, R:{R})";

    public override void Validate()
    {
        if (Q < 0) throw new ArgumentOutOfRangeException(nameof(Q));
        if (R < 0) throw new ArgumentOutOfRangeException(nameof(R));
    }
}
