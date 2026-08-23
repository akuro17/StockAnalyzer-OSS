using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreParabolicSarParameter : CoreIndicatorParameterBase
{
    [DisplayName("Acceleration Start")]
    [Description("Starting acceleration factor for Parabolic SAR.")]
    [CoreParameterRange(0.001, 1.0)]
    public decimal AccelerationStart { get; set; } = 0.02m;

    [DisplayName("Acceleration Step")]
    [Description("Acceleration increment step per new extreme price.")]
    [CoreParameterRange(0.001, 1.0)]
    public decimal AccelerationStep { get; set; } = 0.02m;

    [DisplayName("Acceleration Max")]
    [Description("Maximum acceleration factor limit.")]
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
