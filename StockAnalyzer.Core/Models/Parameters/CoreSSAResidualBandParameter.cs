using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Mode used to calculate residual volatility in SSA Residual Band.
/// </summary>
public enum SsaResidualBandSigmaMode
{
    /// <summary>
    /// Exact diagonal averaging reconstruction error standard deviation (O(r * W)).
    /// </summary>
    ExactDiagonalAverage = 0,

    /// <summary>
    /// Fast analytical eigenvalue energy sum residual approximation (O(L - r)).
    /// </summary>
    FastEigenEnergy = 1
}

public class CoreSSAResidualBandParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.SsaResidualBandDefaultWindowSize;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for SSA residual analysis.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _embeddingDimension = IndicatorDefaultConstants.SsaResidualBandDefaultEmbeddingDimension;

    [CoreParameterRange(2, 256)]
    [Range(2, 256)]
    [DisplayName("Embedding Dimension (L)")]
    [Description("Lag window length L (2 <= L <= WindowSize / 2).")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    private int _numComponents = IndicatorDefaultConstants.SsaResidualBandDefaultNumComponents;

    [CoreParameterRange(1, 100)]
    [Range(1, 100)]
    [DisplayName("Number of Components (r)")]
    [Description("Number of principal reconstructed components for signal extraction.")]
    public int NumComponents
    {
        get => _numComponents;
        set => SetProperty(ref _numComponents, value);
    }

    private decimal _multiplier = IndicatorDefaultConstants.SsaResidualBandDefaultMultiplier;

    [CoreParameterRange(0.1, 10.0)]
    [Range(0.1, 10.0)]
    [DisplayName("Multiplier (M)")]
    [Description("Residual standard deviation multiplier for volatility band width.")]
    public decimal Multiplier
    {
        get => _multiplier;
        set => SetProperty(ref _multiplier, value);
    }

    private SsaResidualBandSigmaMode _sigmaMode = SsaResidualBandSigmaMode.ExactDiagonalAverage;

    [DisplayName("Sigma Calculation Mode")]
    [Description("Method used to calculate residual volatility: Exact diagonal averaging vs Fast analytical eigenvalue energy.")]
    public SsaResidualBandSigmaMode SigmaMode
    {
        get => _sigmaMode;
        set => SetProperty(ref _sigmaMode, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {EmbeddingDimension}, {NumComponents}, {Multiplier:F1})";

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
        if (Multiplier <= 0m)
            throw new ArgumentOutOfRangeException(nameof(Multiplier), "Multiplier must be > 0");
    }
}
