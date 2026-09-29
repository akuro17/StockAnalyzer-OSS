using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSSAEntropyParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.SsaEntropyDefaultWindowSize;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for SSA entropy calculation.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _embeddingDimension = IndicatorDefaultConstants.SsaEntropyDefaultEmbeddingDimension;

    [CoreParameterRange(2, 256)]
    [Range(2, 256)]
    [DisplayName("Embedding Dimension (L)")]
    [Description("Lag window length L (2 <= L <= WindowSize / 2).")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    private SsaDetrendMode _detrendMode = SsaDetrendMode.LeastSquaresLinear;

    [DisplayName("Detrend Mode")]
    [Description("Detrending algorithm applied prior to SSA covariance matrix embedding.")]
    public SsaDetrendMode DetrendMode
    {
        get => _detrendMode;
        set => SetProperty(ref _detrendMode, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {EmbeddingDimension})";

    public override void Validate()
    {
        if (WindowSize < 4)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be >= 4");
        if (EmbeddingDimension < 2 || EmbeddingDimension > WindowSize / 2)
            throw new ArgumentOutOfRangeException(nameof(EmbeddingDimension), "EmbeddingDimension must satisfy 2 <= L <= WindowSize / 2");
    }
}
