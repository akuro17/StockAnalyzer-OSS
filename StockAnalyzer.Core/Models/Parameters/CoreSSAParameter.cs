using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreSSAParameter : CoreIndicatorParameterBase
{
    private int _windowSize = IndicatorDefaultConstants.SsaDefaultWindowSize;

    [CoreParameterRange(4, 512)]
    [Range(4, 512)]
    [DisplayName("Window Size")]
    [Description("Rolling window size (candles) for SSA analysis.")]
    public int WindowSize
    {
        get => _windowSize;
        set => SetProperty(ref _windowSize, value);
    }

    private int _embeddingDimension = IndicatorDefaultConstants.SsaDefaultEmbeddingDimension;

    [CoreParameterRange(2, 256)]
    [Range(2, 256)]
    [DisplayName("Embedding Dimension (L)")]
    [Description("Lag window length L (2 <= L <= WindowSize / 2).")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    private int _numComponents = IndicatorDefaultConstants.SsaDefaultNumComponents;

    [CoreParameterRange(1, 100)]
    [Range(1, 100)]
    [DisplayName("Number of Components (r)")]
    [Description("Number of principal reconstructed components.")]
    public int NumComponents
    {
        get => _numComponents;
        set => SetProperty(ref _numComponents, value);
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
    }
}
