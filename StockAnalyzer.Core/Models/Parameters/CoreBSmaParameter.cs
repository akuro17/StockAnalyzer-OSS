namespace StockAnalyzer.Core.Models.Parameters;

using System;
using System.ComponentModel;

public class CoreBSmaParameter : CoreIndicatorParameterBase
{
    private int _period = 14;
    [CoreParameterRange(2, 500)]
    [DisplayName("Period")]
    [Description("Number of periods for the B-Spline Moving Average.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    private int _degree = 3;
    [CoreParameterRange(1, 5)]
    [DisplayName("Degree")]
    [Description("Polynomial degree of the B-spline basis (1 to 5).")]
    public int Degree
    {
        get => _degree;
        set => SetProperty(ref _degree, value);
    }

    private double _offset = 0.85;
    [CoreParameterRange(0.0, 1.0)]
    [DisplayName("Offset")]
    [Description("Centroid offset of the spline window (0.0 to 1.0). Higher values reduce lag.")]
    public double Offset
    {
        get => _offset;
        set => SetProperty(ref _offset, value);
    }

    private double _sigma = 6.0;
    [CoreParameterRange(0.5, 20.0)]
    [DisplayName("Sigma")]
    [Description("B-spline coordinate scale factor (0.5 to 20.0).")]
    public double Sigma
    {
        get => _sigma;
        set => SetProperty(ref _sigma, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {Degree}, {Offset:F2}, {Sigma:F1})";

    public override void Validate()
    {
        if (Period < 2 || Period > 500)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 2 and 500.");
        if (Degree < 1 || Degree > 5)
            throw new ArgumentOutOfRangeException(nameof(Degree), "Degree must be between 1 and 5.");
        if (!double.IsFinite(Offset) || Offset < 0.0 || Offset > 1.0)
            throw new ArgumentOutOfRangeException(nameof(Offset), "Offset must be a finite number between 0.0 and 1.0.");
        if (!double.IsFinite(Sigma) || Sigma < 0.5 || Sigma > 20.0)
            throw new ArgumentOutOfRangeException(nameof(Sigma), "Sigma must be a finite number between 0.5 and 20.0.");
    }
}
