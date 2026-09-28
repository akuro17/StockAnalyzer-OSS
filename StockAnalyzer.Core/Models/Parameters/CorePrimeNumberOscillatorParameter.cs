using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CorePrimeNumberOscillatorParameter : CoreIndicatorParameterBase
{
    private decimal _scaleMultiplier = 10.0m;
    [CoreParameterRange(1.0, 1000.0)]
    [Range(1.0, 1000.0)]
    [DisplayName("Scale Multiplier")]
    [Description("Multiplier to scale prices into integer domain (e.g. 10.0 for 0.1 precision).")]
    public decimal ScaleMultiplier
    {
        get => _scaleMultiplier;
        set => SetProperty(ref _scaleMultiplier, value);
    }

    private int _consecutiveExtremaPeriods = 2;
    [CoreParameterRange(1, 10)]
    [Range(1, 10)]
    [DisplayName("Consecutive Extrema Periods")]
    [Description("Number of consecutive periods at extreme level required to trigger signal.")]
    public int ConsecutiveExtremaPeriods
    {
        get => _consecutiveExtremaPeriods;
        set => SetProperty(ref _consecutiveExtremaPeriods, value);
    }

    private int _lookbackPeriod = 5;
    [CoreParameterRange(2, 50)]
    [Range(2, 50)]
    [DisplayName("Lookback Period")]
    [Description("Period window for local high/low extrema detection.")]
    public int LookbackPeriod
    {
        get => _lookbackPeriod;
        set => SetProperty(ref _lookbackPeriod, value);
    }

    private decimal _tolerance = 0.0m;
    [CoreParameterRange(0.0, 10.0)]
    [Range(0.0, 10.0)]
    [DisplayName("Plateau Tolerance")]
    [Description("Maximum value deviation allowed for plateau stagnation.")]
    public decimal Tolerance
    {
        get => _tolerance;
        set => SetProperty(ref _tolerance, value);
    }

    public override string GetDisplayName(string type) => $"{type} (S:{ScaleMultiplier}, K:{ConsecutiveExtremaPeriods}, W:{LookbackPeriod})";

    public override void Validate()
    {
        if (ScaleMultiplier <= 0m || ScaleMultiplier > 1000.0m)
            throw new ArgumentOutOfRangeException(nameof(ScaleMultiplier), "Scale multiplier must be between 1.0 and 1000.0.");
        if (ConsecutiveExtremaPeriods < 1 || ConsecutiveExtremaPeriods > 10)
            throw new ArgumentOutOfRangeException(nameof(ConsecutiveExtremaPeriods), "Consecutive periods must be between 1 and 10.");
        if (LookbackPeriod < 2 || LookbackPeriod > 50)
            throw new ArgumentOutOfRangeException(nameof(LookbackPeriod), "Lookback period must be between 2 and 50.");
        if (Tolerance < 0m || Tolerance > 10.0m)
            throw new ArgumentOutOfRangeException(nameof(Tolerance), "Tolerance must be between 0.0 and 10.0.");
    }
}
