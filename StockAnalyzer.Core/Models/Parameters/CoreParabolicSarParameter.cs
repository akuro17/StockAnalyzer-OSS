using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreParabolicSarParameter : CoreIndicatorParameterBase
{
    [CoreParameterRange(0.001, 1.0)]
    public decimal AccelerationStart { get; set; } = 0.02m;

    [CoreParameterRange(0.001, 1.0)]
    public decimal AccelerationStep { get; set; } = 0.02m;

    [CoreParameterRange(0.01, 1.0)]
    public decimal AccelerationMax { get; set; } = 0.2m;

    public override string GetDisplayName(string type) => $"{type} ({AccelerationStart}, {AccelerationStep}, {AccelerationMax})";

    public override void Validate()
    {
        if (AccelerationStart <= 0) throw new ArgumentOutOfRangeException(nameof(AccelerationStart));
        if (AccelerationStep <= 0) throw new ArgumentOutOfRangeException(nameof(AccelerationStep));
        if (AccelerationMax <= 0) throw new ArgumentOutOfRangeException(nameof(AccelerationMax));
    }
}
