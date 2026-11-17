using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEntropyParameter : CoreIndicatorParameterBase
{
    private int _period = IndicatorDefaultConstants.EntropyDefaultPeriod;
    private int _binCount = IndicatorDefaultConstants.EntropyDefaultBinCount;
    private int _embeddingDimension = IndicatorDefaultConstants.EntropyDefaultEmbeddingDimension;
    private int _sampleEntropyDimension = IndicatorDefaultConstants.EntropyDefaultSampleDimension;
    private double _toleranceMultiplier = IndicatorDefaultConstants.EntropyDefaultToleranceMultiplier;

    [CoreParameterRange(IndicatorDefaultConstants.EntropyMinPeriod, IndicatorDefaultConstants.EntropyMaxPeriod)]
    [DisplayName("Period")]
    [Description("Number of log returns in the window (bars). A window containing an unusable price gives no value.")]
    [Category("Periods")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.EntropyMinBinCount, IndicatorDefaultConstants.EntropyMaxBinCount)]
    [DisplayName("Bin Count")]
    [Description("Number of equal-width bins between the smallest and the largest return of the window. The result is divided by ln(Bin Count), so it is 0 to 1 whatever the bin count.")]
    [Category("Distribution")]
    public int BinCount
    {
        get => _binCount;
        set => SetProperty(ref _binCount, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.EntropyMinEmbeddingDimension, IndicatorDefaultConstants.EntropyMaxEmbeddingDimension)]
    [DisplayName("Permutation Dimension")]
    [Description("Embedding dimension d in [3, 5] for normalized Permutation Entropy (requires Period >= 27 for d=4, or Period >= 124 for d=5; shorter windows output null).")]
    [Category("Complexity")]
    public int EmbeddingDimension
    {
        get => _embeddingDimension;
        set => SetProperty(ref _embeddingDimension, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.EntropyMinSampleDimension, IndicatorDefaultConstants.EntropyMaxSampleDimension)]
    [DisplayName("Sample Entropy Dimension")]
    [Description("Template length m in [1, 3] for Richman-Moorman Sample Entropy (applies finite-sample continuity floor ln(2 * countB) when countB > 0 and countA == 0, and outputs null when countB == 0).")]
    [Category("Complexity")]
    public int SampleEntropyDimension
    {
        get => _sampleEntropyDimension;
        set => SetProperty(ref _sampleEntropyDimension, value);
    }

    [CoreParameterRange(IndicatorDefaultConstants.EntropyMinToleranceMultiplier, IndicatorDefaultConstants.EntropyMaxToleranceMultiplier)]
    [DisplayName("Tolerance Multiplier")]
    [Description("Tolerance multiplier r relative to sample standard deviation (tolerance = multiplier * stddev) for Sample Entropy.")]
    [Category("Complexity")]
    public double ToleranceMultiplier
    {
        get => _toleranceMultiplier;
        set => SetProperty(ref _toleranceMultiplier, value);
    }

    public override string GetDisplayName(string type) =>
        EmbeddingDimension == IndicatorDefaultConstants.EntropyDefaultEmbeddingDimension &&
        SampleEntropyDimension == IndicatorDefaultConstants.EntropyDefaultSampleDimension &&
        ToleranceMultiplier == IndicatorDefaultConstants.EntropyDefaultToleranceMultiplier
            ? $"{type} ({Period}, {BinCount})"
            : $"{type} ({Period}, {BinCount}, d:{EmbeddingDimension}, m:{SampleEntropyDimension})";

    // The first value is at bar index Period (Period returns need Period + 1 prices).
    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.EntropyMinPeriod, IndicatorDefaultConstants.EntropyMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(BinCount, IndicatorDefaultConstants.EntropyMinBinCount, IndicatorDefaultConstants.EntropyMaxBinCount, nameof(BinCount));
        ThrowIfOutOfRange(EmbeddingDimension, IndicatorDefaultConstants.EntropyMinEmbeddingDimension, IndicatorDefaultConstants.EntropyMaxEmbeddingDimension, nameof(EmbeddingDimension));
        ThrowIfOutOfRange(SampleEntropyDimension, IndicatorDefaultConstants.EntropyMinSampleDimension, IndicatorDefaultConstants.EntropyMaxSampleDimension, nameof(SampleEntropyDimension));
        if (!double.IsFinite(ToleranceMultiplier) ||
            ToleranceMultiplier < IndicatorDefaultConstants.EntropyMinToleranceMultiplier ||
            ToleranceMultiplier > IndicatorDefaultConstants.EntropyMaxToleranceMultiplier)
        {
            throw new ArgumentOutOfRangeException(nameof(ToleranceMultiplier));
        }
    }
}
