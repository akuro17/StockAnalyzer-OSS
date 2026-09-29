using System;
using System.ComponentModel;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Parameters;

public abstract class CoreHoughParameterBase : CoreIndicatorParameterBase
{
    private int _lookback = 100;

    [DisplayName("Lookback Period")]
    [Description("Number of historical bars to analyze with Hough Transform.")]
    [CoreParameterRange(20, 500)]
    public int Lookback
    {
        get => _lookback;
        set => SetProperty(ref _lookback, Math.Clamp(value, 20, 500));
    }

    private int _pivotWindow = 3;

    [DisplayName("Pivot Window")]
    [Description("Window size for local extrema (fractal pivot) extraction.")]
    [CoreParameterRange(1, 15)]
    public int PivotWindow
    {
        get => _pivotWindow;
        set => SetProperty(ref _pivotWindow, Math.Clamp(value, 1, 15));
    }

    private int _voteThreshold = 3;

    [DisplayName("Vote Threshold")]
    [Description("Minimum accumulator votes required to accept a line candidate.")]
    [CoreParameterRange(2, 20)]
    public int VoteThreshold
    {
        get => _voteThreshold;
        set => SetProperty(ref _voteThreshold, Math.Clamp(value, 2, 20));
    }

    private int _maxLines = 5;

    [DisplayName("Max Lines")]
    [Description("Maximum number of dominant lines to extract.")]
    [CoreParameterRange(1, 10)]
    public int MaxLines
    {
        get => _maxLines;
        set => SetProperty(ref _maxLines, Math.Clamp(value, 1, 10));
    }

    private HoughNormalizationMode _normalization = HoughNormalizationMode.MinMax;

    [DisplayName("Normalization")]
    [Description("Coordinate normalization mode applied before Hough voting.")]
    public HoughNormalizationMode Normalization
    {
        get => _normalization;
        set => SetProperty(ref _normalization, value);
    }

    public override void Validate()
    {
        if (Lookback < 20 || Lookback > 500)
            throw new ArgumentOutOfRangeException(nameof(Lookback), "Lookback must be between 20 and 500.");
        if (PivotWindow < 1 || PivotWindow > 15)
            throw new ArgumentOutOfRangeException(nameof(PivotWindow), "PivotWindow must be between 1 and 15.");
        if (VoteThreshold < 2 || VoteThreshold > 20)
            throw new ArgumentOutOfRangeException(nameof(VoteThreshold), "VoteThreshold must be between 2 and 20.");
        if (MaxLines < 1 || MaxLines > 10)
            throw new ArgumentOutOfRangeException(nameof(MaxLines), "MaxLines must be between 1 and 10.");
    }
}

public class CoreHoughTrendStrengthParameter : CoreHoughParameterBase
{
    public override string GetDisplayName(string indicatorType)
    {
        return $"HoughTrendStrength({Lookback},{PivotWindow},{VoteThreshold})";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not CoreHoughTrendStrengthParameter p) return false;
        return p.Lookback == Lookback &&
               p.PivotWindow == PivotWindow &&
               p.VoteThreshold == VoteThreshold &&
               p.MaxLines == MaxLines &&
               p.Normalization == Normalization;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Lookback, PivotWindow, VoteThreshold, MaxLines, Normalization);
    }
}

public class CoreHoughTrendAngleParameter : CoreHoughParameterBase
{
    public override string GetDisplayName(string indicatorType)
    {
        return $"HoughTrendAngle({Lookback},{PivotWindow},{VoteThreshold})";
    }

    public override bool Equals(object? obj)
    {
        if (obj is not CoreHoughTrendAngleParameter p) return false;
        return p.Lookback == Lookback &&
               p.PivotWindow == PivotWindow &&
               p.VoteThreshold == VoteThreshold &&
               p.MaxLines == MaxLines &&
               p.Normalization == Normalization;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Lookback, PivotWindow, VoteThreshold, MaxLines, Normalization);
    }
}
