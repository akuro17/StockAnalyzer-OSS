using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameter configuration for the Fractal Dimension Index (FDI) indicator.
/// </summary>
public class CoreFDIParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.FdiPeriod;
    private int _smoothingPeriod = IndicatorDefaultConstants.FdiSmoothingPeriod;

    [DisplayName("Period")]
    [Description("Lookback window for Fractal Dimension Index calculation.")]
    [CoreParameterRange(2, 10000)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Smoothing Period")]
    [Description("Exponential smoothing period applied to raw FDI values (0 disables smoothing).")]
    [CoreParameterRange(0, 1000)]
    public int SmoothingPeriod
    {
        get => _smoothingPeriod;
        set => SetProperty(ref _smoothingPeriod, value);
    }

    public override string GetDisplayName(string type) =>
        SmoothingPeriod > 0 ? $"{type} ({Period}, {SmoothingPeriod})" : $"{type} ({Period})";

    public override void Validate()
    {
        if (Period < 2 || Period > 10000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 2 and 10000.");
        if (SmoothingPeriod < 0 || SmoothingPeriod > 1000)
            throw new ArgumentOutOfRangeException(nameof(SmoothingPeriod), "Smoothing period must be between 0 and 1000.");
    }
}
