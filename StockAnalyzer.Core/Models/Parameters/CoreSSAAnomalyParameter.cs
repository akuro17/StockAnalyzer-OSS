using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSSAAnomalyParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.SsaAnomalyDefaultWindowSize;

    [CoreParameterRange(4, 2048)]
    [Range(4, 2048)]
    [DisplayName("Window Size (W)")]
    [Description("Rolling causal window size (candles) for SSA anomaly detection.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _embeddingDimension = IndicatorDefaultConstants.SsaAnomalyDefaultEmbeddingDimension;

    [CoreParameterRange(2, 512)]
    [Range(2, 512)]
    [DisplayName("Embedding Dimension (L)")]
    [Description("Lag embedding dimension L (2 <= L <= WindowSize / 2).")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    private int _numComponents = IndicatorDefaultConstants.SsaAnomalyDefaultNumComponents;

    [CoreParameterRange(1, 100)]
    [Range(1, 100)]
    [DisplayName("Number of Components (r)")]
    [Description("Number of principal components for normal structural subspace.")]
    public int NumComponents
    {
        get => _numComponents;
        set => SetProperty(ref _numComponents, value);
    }

    private bool _autoRank = IndicatorDefaultConstants.SsaAnomalyDefaultAutoRank;

    [DisplayName("Auto Rank Selection")]
    [Description("Automatically estimate signal rank based on cumulative energy threshold.")]
    public bool AutoRank
    {
        get => _autoRank;
        set => SetProperty(ref _autoRank, value);
    }

    private SsaDetrendMode _detrendMethod = SsaDetrendMode.LeastSquaresLinear;

    [DisplayName("Detrend Method")]
    [Description("Trend removal method prior to trajectory embedding.")]
    public SsaDetrendMode DetrendMethod
    {
        get => _detrendMethod;
        set => SetProperty(ref _detrendMethod, value);
    }

    private PriceType _priceSource = PriceType.Close;

    [DisplayName("Price Type")]
    [Description("Price type used for analysis (Close, Median, etc.).")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    private decimal _enterThreshold = IndicatorDefaultConstants.SsaAnomalyDefaultEnterThreshold;

    [CoreParameterRange(0.1, 10.0)]
    [Range(0.1, 10.0)]
    [DisplayName("Enter Threshold (Z)")]
    [Description("Standard score magnitude required to enter anomalous regime.")]
    public decimal EnterThreshold
    {
        get => _enterThreshold;
        set => SetProperty(ref _enterThreshold, value);
    }

    private decimal _exitThreshold = IndicatorDefaultConstants.SsaAnomalyDefaultExitThreshold;

    [CoreParameterRange(0.1, 5.0)]
    [Range(0.1, 5.0)]
    [DisplayName("Exit Threshold (Z)")]
    [Description("Standard score magnitude required to exit anomalous regime.")]
    public decimal ExitThreshold
    {
        get => _exitThreshold;
        set => SetProperty(ref _exitThreshold, value);
    }

    private int _coolDownPeriod = IndicatorDefaultConstants.SsaAnomalyDefaultCoolDownPeriod;

    [CoreParameterRange(1, 20)]
    [Range(1, 20)]
    [DisplayName("Cooldown Confirmation Bars")]
    [Description("Consecutive bars below exit threshold required to confirm normalization.")]
    public int CoolDownPeriod
    {
        get => _coolDownPeriod;
        set => SetProperty(ref _coolDownPeriod, value);
    }

    private int _minDuration = IndicatorDefaultConstants.SsaAnomalyDefaultMinDuration;

    [CoreParameterRange(1, 20)]
    [Range(1, 20)]
    [DisplayName("Min Duration Bars")]
    [Description("Minimum duration in bars to qualify as structural anomaly.")]
    public int MinDuration
    {
        get => _minDuration;
        set => SetProperty(ref _minDuration, value);
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
        if (ExitThreshold > EnterThreshold)
            throw new ArgumentException("ExitThreshold cannot be greater than EnterThreshold");
    }
}
