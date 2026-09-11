using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSSASNRParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.SsaSnrDefaultWindowSize;

    [CoreParameterRange(4, 2048)]
    [Range(4, 2048)]
    [DisplayName("Window Size (W)")]
    [Description("Rolling window size (candles) for SSA analysis.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _embeddingDimension = IndicatorDefaultConstants.SsaSnrDefaultEmbeddingDimension;

    [CoreParameterRange(2, 512)]
    [Range(2, 512)]
    [DisplayName("Embedding Dimension (L)")]
    [Description("Lag window length L (2 <= L <= WindowSize / 2).")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    private int _numComponents = IndicatorDefaultConstants.SsaSnrDefaultNumComponents;

    [CoreParameterRange(1, 100)]
    [Range(1, 100)]
    [DisplayName("Number of Components (r)")]
    [Description("Number of principal components considered as Signal subspace.")]
    public int NumComponents
    {
        get => _numComponents;
        set => SetProperty(ref _numComponents, value);
    }

    private decimal _thresholdHigh = IndicatorDefaultConstants.SsaSnrDefaultThresholdHigh;

    [CoreParameterRange(-20.0, 40.0)]
    [Range(-20.0, 40.0)]
    [DisplayName("High SNR Threshold (dB)")]
    [Description("Upper reference line for high signal purity.")]
    public decimal ThresholdHigh
    {
        get => _thresholdHigh;
        set => SetProperty(ref _thresholdHigh, value);
    }

    private decimal _thresholdLow = IndicatorDefaultConstants.SsaSnrDefaultThresholdLow;

    [CoreParameterRange(-20.0, 40.0)]
    [Range(-20.0, 40.0)]
    [DisplayName("Low SNR Threshold (dB)")]
    [Description("Lower reference line for noise dominance.")]
    public decimal ThresholdLow
    {
        get => _thresholdLow;
        set => SetProperty(ref _thresholdLow, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {EmbeddingDimension}, {NumComponents})";

    public override void Validate()
    {
        if (WindowSize < 4)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be >= 4");
        if (EmbeddingDimension < 2 || EmbeddingDimension > WindowSize / 2)
            throw new ArgumentOutOfRangeException(nameof(EmbeddingDimension), "EmbeddingDimension must satisfy 2 <= L <= WindowSize / 2");
        int k = WindowSize - EmbeddingDimension + 1;
        int maxR = Math.Min(EmbeddingDimension, k);
        if (NumComponents < 1 || NumComponents > maxR)
            throw new ArgumentOutOfRangeException(nameof(NumComponents), $"NumComponents must satisfy 1 <= r <= {maxR}");
        if (ThresholdLow > ThresholdHigh)
            throw new ArgumentException("ThresholdLow cannot be greater than ThresholdHigh");
    }
}
