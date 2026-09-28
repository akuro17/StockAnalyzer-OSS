using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreFourierTransformParameter : CoreIndicatorParameterBase
{
    private int _targetPeriod = IndicatorDefaultConstants.FourierTransformDefaultTargetPeriod;

    [CoreParameterRange(IndicatorDefaultConstants.FourierTransformMinTargetPeriod, IndicatorDefaultConstants.FourierTransformMaxTargetPeriod)]
    [Range(IndicatorDefaultConstants.FourierTransformMinTargetPeriod, IndicatorDefaultConstants.FourierTransformMaxTargetPeriod)]
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
        if (TargetPeriod < IndicatorDefaultConstants.FourierTransformMinTargetPeriod || TargetPeriod > IndicatorDefaultConstants.FourierTransformMaxTargetPeriod)
            throw new ArgumentOutOfRangeException(nameof(TargetPeriod), $"TargetPeriod must be between {IndicatorDefaultConstants.FourierTransformMinTargetPeriod} and {IndicatorDefaultConstants.FourierTransformMaxTargetPeriod}");
    }
}
