using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSSASqueezeParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.SsaSqueezeDefaultWindowSize;

    [CoreParameterRange(4, 2048)]
    [Range(4, 2048)]
    [DisplayName("Window Size (W)")]
    [Description("Rolling window size (candles) for SSA analysis.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _embeddingDimension = IndicatorDefaultConstants.SsaSqueezeDefaultEmbeddingDimension;

    [CoreParameterRange(2, 512)]
    [Range(2, 512)]
    [DisplayName("Embedding Dimension (L)")]
    [Description("Lag window length L (2 <= L <= WindowSize / 2).")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    private int _numComponents = IndicatorDefaultConstants.SsaSqueezeDefaultNumComponents;

    [CoreParameterRange(1, 100)]
    [Range(1, 100)]
    [DisplayName("Number of Components (r)")]
    [Description("Number of principal components used for SSA reconstruction.")]
    public int NumComponents
    {
        get => _numComponents;
        set => SetProperty(ref _numComponents, value);
    }

    private decimal _ssaMultiplier = IndicatorDefaultConstants.SsaSqueezeDefaultSsaMultiplier;

    [CoreParameterRange(0.1, 10.0)]
    [Range(0.1, 10.0)]
    [DisplayName("SSA Multiplier (M_SSA)")]
    [Description("Multiplier for SSA residual volatility band.")]
    public decimal SsaMultiplier
    {
        get => _ssaMultiplier;
        set => SetProperty(ref _ssaMultiplier, value);
    }

    private int _atrPeriod = IndicatorDefaultConstants.SsaSqueezeDefaultAtrPeriod;

    [CoreParameterRange(2, 100)]
    [Range(2, 100)]
    [DisplayName("ATR Period (K_ATR)")]
    [Description("Averaging period for True Range.")]
    public int AtrPeriod
    {
        get => _atrPeriod;
        set => SetProperty(ref _atrPeriod, value);
    }

    private decimal _atrMultiplier = IndicatorDefaultConstants.SsaSqueezeDefaultAtrMultiplier;

    [CoreParameterRange(0.1, 10.0)]
    [Range(0.1, 10.0)]
    [DisplayName("ATR Multiplier (M_ATR)")]
    [Description("Multiplier for ATR channel.")]
    public decimal AtrMultiplier
    {
        get => _atrMultiplier;
        set => SetProperty(ref _atrMultiplier, value);
    }

    private int _momentumPeriod = IndicatorDefaultConstants.SsaSqueezeDefaultMomentumPeriod;

    [CoreParameterRange(2, 50)]
    [Range(2, 50)]
    [DisplayName("Momentum Period (K_mom)")]
    [Description("Lookback bars for causal linear regression momentum.")]
    public int MomentumPeriod
    {
        get => _momentumPeriod;
        set => SetProperty(ref _momentumPeriod, value);
    }

    private decimal _squeezeThreshold = IndicatorDefaultConstants.SsaSqueezeDefaultSqueezeThreshold;

    [CoreParameterRange(0.1, 5.0)]
    [Range(0.1, 5.0)]
    [DisplayName("Squeeze Threshold")]
    [Description("Threshold ratio for Squeeze ON/OFF state (default 1.0).")]
    public decimal SqueezeThreshold
    {
        get => _squeezeThreshold;
        set => SetProperty(ref _squeezeThreshold, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {EmbeddingDimension}, {NumComponents}, {MomentumPeriod})";

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
        if (SsaMultiplier <= 0m)
            throw new ArgumentOutOfRangeException(nameof(SsaMultiplier), "SsaMultiplier must be > 0");
        if (AtrPeriod < 2)
            throw new ArgumentOutOfRangeException(nameof(AtrPeriod), "AtrPeriod must be >= 2");
        if (AtrMultiplier <= 0m)
            throw new ArgumentOutOfRangeException(nameof(AtrMultiplier), "AtrMultiplier must be > 0");
        if (MomentumPeriod < 2)
            throw new ArgumentOutOfRangeException(nameof(MomentumPeriod), "MomentumPeriod must be >= 2");
        if (SqueezeThreshold <= 0m)
            throw new ArgumentOutOfRangeException(nameof(SqueezeThreshold), "SqueezeThreshold must be > 0");
    }
}
