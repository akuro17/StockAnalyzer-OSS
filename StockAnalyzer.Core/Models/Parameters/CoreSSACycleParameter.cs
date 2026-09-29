using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSSACycleParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.SsaCycleDefaultWindowSize;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for SSA cycle analysis.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _embeddingDimension = IndicatorDefaultConstants.SsaCycleDefaultEmbeddingDimension;

    [CoreParameterRange(2, 256)]
    [Range(2, 256)]
    [DisplayName("Embedding Dimension (L)")]
    [Description("Lag window length L (2 <= L <= WindowSize / 2).")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    private double _deltaPair = IndicatorDefaultConstants.SsaCycleDefaultDeltaPair;

    [CoreParameterRange(0.01, 0.50)]
    [Range(0.01, 0.50)]
    [DisplayName("Pair Degeneracy Ratio Delta")]
    [Description("Tolerance delta for eigenvalue degeneracy check (lambda_{m+1} / lambda_m >= 1 - delta).")]
    public double DeltaPair
    {
        get => _deltaPair;
        set => SetProperty(ref _deltaPair, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({WindowSize}, {EmbeddingDimension}, {DeltaPair:F2})";

    public override void Validate()
    {
        if (WindowSize < 4)
            throw new ArgumentOutOfRangeException(nameof(WindowSize), "WindowSize must be >= 4");
        if (EmbeddingDimension < 2 || EmbeddingDimension > WindowSize / 2)
            throw new ArgumentOutOfRangeException(nameof(EmbeddingDimension), "EmbeddingDimension must satisfy 2 <= L <= WindowSize / 2");
        if (DeltaPair <= 0.0 || DeltaPair >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(DeltaPair), "DeltaPair must satisfy 0.0 < DeltaPair < 1.0");
    }
}
