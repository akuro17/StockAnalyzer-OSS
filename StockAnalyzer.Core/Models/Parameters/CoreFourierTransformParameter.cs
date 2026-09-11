using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFourierTransformParameter : CoreIndicatorParameterBase
{
    private int _targetPeriod = IndicatorDefaultConstants.FourierTransformDefaultTargetPeriod;

    [CoreParameterRange(2, 500)]
    [Range(2, 500)]
    [DisplayName("Target Period")]
    [Description("The specific cycle period (candles) whose signal strength is extracted via the Goertzel algorithm.")]
    public int TargetPeriod
    {
        get => _targetPeriod;
        set => SetProperty(ref _targetPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({TargetPeriod})";

    public override void Validate()
    {
        if (TargetPeriod < 2 || TargetPeriod > 500)
            throw new ArgumentOutOfRangeException(nameof(TargetPeriod), "TargetPeriod must be between 2 and 500");
    }
}
