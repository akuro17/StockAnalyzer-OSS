using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreIfftBandPassFilterParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.IfftBandPassFilterDefaultWindowSize;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for the IFFT band-pass reconstruction.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _bandWidthBins = IndicatorDefaultConstants.IfftBandPassFilterDefaultBandWidthBins;

    [CoreParameterRange(0, 128)]
    [Range(0, 128)]
    [DisplayName("Band Width (bins)")]
    [Description("Bins kept on each side of the auto-detected dominant frequency bin before the inverse transform. 0 keeps only the single dominant bin.")]
    public int BandWidthBins
    {
        get => _bandWidthBins;
        set => SetProperty(ref _bandWidthBins, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {BandWidthBins})";

    public override void Validate()
    {
        if (WindowSize <= 0 || WindowSize > 512)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be between 1 and 512");
        if (BandWidthBins < 0 || BandWidthBins > 128)
            throw new ArgumentOutOfRangeException(nameof(BandWidthBins), "BandWidthBins must be between 0 and 128");
    }
}
