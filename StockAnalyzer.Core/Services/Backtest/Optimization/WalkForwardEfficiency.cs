namespace StockAnalyzer.Core.Services.Backtest.Optimization;

/// <summary>The efficiency of a walk-forward run and how it was reached; <see cref="Value"/> is null exactly when <see cref="Reason"/> says why.</summary>
public sealed record WalkForwardEfficiencyResult(decimal? Value, string? Reason, int WindowCount, decimal? MeanOutOfSampleBarSharpe, decimal? MeanInSampleBarSharpe);

/// <summary>
/// The walk-forward efficiency of the bar Sharpe (owner decision D-04c = A, no threshold D-04f = A): the simple mean of the out-of-sample BarSharpe divided
/// by the simple mean of the in-sample BarSharpe, over the windows in which BOTH are Valid (a window with only one of them Valid is not used for either
/// mean, so the two means describe the same windows). It is null when no window qualifies or the in-sample mean is not greater than 0.
/// BarSharpe is the report's own per-bar metric; nothing is annualised here.
/// </summary>
public static class WalkForwardEfficiency
{
    public const string NoQualifyingWindowReason = "NoWindowWithValidInSampleAndOutOfSampleBarSharpe";
    public const string NonPositiveInSampleMeanReason = "InSampleBarSharpeMeanNotPositive";

    /// <param name="windows">Per window the in-sample and out-of-sample BarSharpe, each null unless its metric is Valid.</param>
    public static WalkForwardEfficiencyResult Compute(IEnumerable<(decimal? InSample, decimal? OutOfSample)> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var inSample = new List<decimal>();
        var outOfSample = new List<decimal>();
        foreach ((decimal? isValue, decimal? oosValue) in windows)
        {
            if (isValue is { } inValue && oosValue is { } outValue)
            {
                inSample.Add(inValue);
                outOfSample.Add(outValue);
            }
        }

        if (inSample.Count == 0)
        {
            return new WalkForwardEfficiencyResult(null, NoQualifyingWindowReason, 0, null, null);
        }

        decimal meanIn = inSample.Sum() / inSample.Count;
        decimal meanOut = outOfSample.Sum() / outOfSample.Count;
        return meanIn > 0m
            ? new WalkForwardEfficiencyResult(meanOut / meanIn, null, inSample.Count, meanOut, meanIn)
            : new WalkForwardEfficiencyResult(null, NonPositiveInSampleMeanReason, inSample.Count, meanOut, meanIn);
    }
}
