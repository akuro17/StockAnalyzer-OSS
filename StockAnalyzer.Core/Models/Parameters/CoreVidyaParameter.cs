using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreVidyaParameter : CoreIndicatorParameterBase
{
    private int _smoothPeriod = IndicatorDefaultConstants.VidyaSmoothPeriod;

    [DisplayName("Smooth Period")]
    [Description("Base EMA smoothing period for VIDYA (N).")]
    [CoreParameterRange(1, 10000)]
    public int SmoothPeriod
    {
        get => _smoothPeriod;
        set => SetProperty(ref _smoothPeriod, value);
    }

    private int _cmoPeriod = IndicatorDefaultConstants.VidyaCmoPeriod;

    [DisplayName("CMO Period")]
    [Description("Chande Momentum Oscillator lookback period for VIDYA (M).")]
    [CoreParameterRange(1, 10000)]
    public int CmoPeriod
    {
        get => _cmoPeriod;
        set => SetProperty(ref _cmoPeriod, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({SmoothPeriod}, {CmoPeriod})";

    public override void Validate()
    {
        if (SmoothPeriod < 1 || SmoothPeriod > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(SmoothPeriod), "Smooth period must be between 1 and 10000.");
        }

        if (CmoPeriod < 1 || CmoPeriod > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(CmoPeriod), "CMO period must be between 1 and 10000.");
        }
    }
}
