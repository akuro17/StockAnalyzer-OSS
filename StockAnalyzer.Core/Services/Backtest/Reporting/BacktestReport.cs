using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Per spec §5.5, verbatim field set, plus two user-approved supplemental additions
/// (<see cref="AnnualizedSharpeAutocorrelationAdjusted"/> and <see cref="AnnualizedSortinoAutocorrelationAdjusted"/>,
/// each confirmed via AskUserQuestion — see Y:\Temp\sa_implementation_plan_BacktestReportAutocorrelationAdjustedSharpe.md
/// and Y:\Temp\sa_implementation_plan_SortinoAutocorrelationAdjustedBootstrap.md). Populated only by
/// <see cref="BacktestReportGenerator"/>.
/// </summary>
public sealed class BacktestReport
{
    public const string YFinanceApproximateDisclosure =
        "Approximate fixed-OHLC backtest using user-declared, unverified yfinance-origin daily data; not evidence of executable fills.";

    // Currency
    public MetricValue TotalPnL { get; init; }
    public MetricValue MaxDrawdownAmount { get; init; }
    public MetricValue ExpectedPayoff { get; init; }

    // Ratios / statistics
    public MetricValue TotalReturn { get; init; }
    public MetricValue CAGR { get; init; }
    public MetricValue WinRate { get; init; }
    public MetricValue ProfitFactor { get; init; }
    public MetricValue MaxDrawdown { get; init; }
    public MetricValue UlcerIndex { get; init; }
    public MetricValue BarSharpe { get; init; }
    public MetricValue AnnualizedSharpe { get; init; }

    /// <summary>
    /// Supplemental addition (not in spec §5.5): Newey-West HAC-adjusted generalization of
    /// <see cref="AnnualizedSharpe"/>, correcting for serial correlation in the per-bar excess-return
    /// sample. Sharpe-only via this closed-form method; see <see cref="AnnualizedSortinoAutocorrelationAdjusted"/>
    /// for Sortino's own (differently-derived) equivalent.
    /// </summary>
    public MetricValue AnnualizedSharpeAutocorrelationAdjusted { get; init; }

    public MetricValue BarSortino { get; init; }
    public MetricValue AnnualizedSortino { get; init; }

    /// <summary>
    /// Supplemental addition (not in spec §5.5): bias-corrected annualized Sortino via a Politis-White
    /// automatic-block-length circular block bootstrap (Candidate B in the comparison doc), since Sortino's
    /// nonlinear min(y,0)^2 downside truncation has no closed-form HAC correction the way Sharpe's does. See
    /// Y:\Temp\sa_implementation_plan_SortinoAutocorrelationAdjustedBootstrap.md.
    /// </summary>
    public MetricValue AnnualizedSortinoAutocorrelationAdjusted { get; init; }

    /// <summary>95% percentile bootstrap confidence interval paired with <see cref="AnnualizedSortinoAutocorrelationAdjusted"/>; null iff that field's Status != Valid.</summary>
    public ConfidenceInterval? AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval { get; init; }

    /// <summary>
    /// CAGR / MaxDrawdown over the whole evaluation period (spec §5.4). Because it is measured since the start of the evaluation rather than
    /// over a fixed 36-month window, it is numerically the same quantity as the "MAR Ratio" (CAGR since inception / maximum drawdown), so no
    /// separate MAR Ratio field exists; it is not the classic 36-month Calmar.
    /// </summary>
    public MetricValue CalmarFullPeriod { get; init; }
    public MetricValue SQN { get; init; }
    public MetricValue RecoveryFactor { get; init; }

    // Supplemental extended trade metrics (not in spec §5.5). Nullable so reports persisted before they existed still load and validate;
    // a report produced by BacktestReportGenerator always carries them. Trade-PnL values use ClosedNet; loss-side values are non-negative
    // magnitudes. See Y:\Temp\sa_implementation_plan_BacktestReportExtendedMetrics.md.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? GrossProfit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? GrossLoss { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? AverageWin { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? AverageLoss { get; init; }

    /// <summary>
    /// AverageWin / AverageLoss. With losses but no wins it is Undefined(NoWinningTrades), not 0: the average win is a mean over an empty set,
    /// which is undefined by definition. (ProfitFactor, a ratio of sums, is Valid(0) in the same case; the two are different quantities.)
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? PayoffRatio { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? LargestWin { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? LargestLoss { get; init; }

    /// <summary>
    /// Mean HoldingBars (raw <c>ExitBar - EntryBar</c>) over all closed trades, in bars. A trade opened and closed on the same bar (e.g. a
    /// forced liquidation on its entry bar) has HoldingBars 0 and is averaged as 0, whereas <see cref="TimeInMarket"/> counts that bar as one
    /// in-market bar; the two are intentionally not the same population.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? AverageHoldingPeriod { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? MaxConsecutiveWins { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? MaxConsecutiveLosses { get; init; }

    /// <summary>
    /// Underwater bars (index difference on the equity path E[0..m]) of the maximum-depth drawdown episode, where depth is the ratio
    /// (H-E)/H behind <see cref="MaxDrawdown"/>; an amount-basis deepest episode can be a different one and no amount-basis duration exists.
    /// An episode still underwater at the last sample bar is counted to that bar and is only a lower bound: see
    /// <see cref="MaxDepthDrawdownDurationRightCensored"/> (the evaluation artifact's <c>DrawdownEpisode.RecoveryIndex</c> tells the same for its episode).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? MaxDepthDrawdownDuration { get; init; }

    /// <summary>
    /// Longest underwater span, in bars, over all drawdown episodes (not necessarily the deepest one). An unrecovered episode is counted to the
    /// last sample bar; see <see cref="LongestDrawdownDurationRightCensored"/>. Always at least <see cref="MaxDepthDrawdownDuration"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? LongestDrawdownDuration { get; init; }

    /// <summary>
    /// True iff the maximum-depth episode never regained its peak by the last sample bar, so <see cref="MaxDepthDrawdownDuration"/> is a lower
    /// bound; false when it recovered or there was no drawdown. Present iff <see cref="MaxDepthDrawdownDuration"/> is Valid (never Valid with a Reason).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MaxDepthDrawdownDurationRightCensored { get; init; }

    /// <summary>
    /// True iff an unrecovered episode exists and its length equals <see cref="LongestDrawdownDuration"/>, so the value is only a lower bound
    /// (conservatively also true when a completed episode ties with it). Present iff <see cref="LongestDrawdownDuration"/> is Valid.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LongestDrawdownDurationRightCensored { get; init; }

    /// <summary>
    /// Number of bars, from <c>max(HistoryStartIndex, TradingStartIndex)</c> on, in which a position existed: an open position in the Close
    /// snapshot of the equity curve, or a trade opened and closed on that same bar (each bar counted once). In bars.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? TimeInMarket { get; init; }

    /// <summary>TimeInMarket / the number of bars from <c>max(HistoryStartIndex, TradingStartIndex)</c> on, as a raw 0..1 ratio (the x100 percentage is a UI concern).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? Exposure { get; init; }

    /// <summary>
    /// CAGR / Exposure: the annualized return per unit of time in the market, a raw return ratio. It is a linear scaling of CAGR; it is NOT a
    /// compounded "always invested" CAGR (that would be a different formula). Non-Valid CAGR or Exposure propagates; Exposure 0 is Undefined.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricValue? ExposureAdjustedCAGR { get; init; }

    /// <summary>
    /// The present extended metrics in their fixed canonical order (also the evaluation identity encoding order). Adding a metric appends
    /// to this list; never reorder.
    /// </summary>
    public IEnumerable<(string Name, MetricValue Metric, MetricUnit ExpectedUnit)> EnumerateExtendedMetrics()
    {
        if (GrossProfit is { } grossProfit) yield return (nameof(GrossProfit), grossProfit, MetricUnit.Currency);
        if (GrossLoss is { } grossLoss) yield return (nameof(GrossLoss), grossLoss, MetricUnit.Currency);
        if (AverageWin is { } averageWin) yield return (nameof(AverageWin), averageWin, MetricUnit.Currency);
        if (AverageLoss is { } averageLoss) yield return (nameof(AverageLoss), averageLoss, MetricUnit.Currency);
        if (PayoffRatio is { } payoffRatio) yield return (nameof(PayoffRatio), payoffRatio, MetricUnit.Dimensionless);
        if (LargestWin is { } largestWin) yield return (nameof(LargestWin), largestWin, MetricUnit.Currency);
        if (LargestLoss is { } largestLoss) yield return (nameof(LargestLoss), largestLoss, MetricUnit.Currency);
        if (AverageHoldingPeriod is { } holding) yield return (nameof(AverageHoldingPeriod), holding, MetricUnit.Bars);
        if (MaxConsecutiveWins is { } maxWins) yield return (nameof(MaxConsecutiveWins), maxWins, MetricUnit.Count);
        if (MaxConsecutiveLosses is { } maxLosses) yield return (nameof(MaxConsecutiveLosses), maxLosses, MetricUnit.Count);
        if (MaxDepthDrawdownDuration is { } maxDepthDrawdownDuration) yield return (nameof(MaxDepthDrawdownDuration), maxDepthDrawdownDuration, MetricUnit.Bars);
        if (LongestDrawdownDuration is { } longestDrawdownDuration) yield return (nameof(LongestDrawdownDuration), longestDrawdownDuration, MetricUnit.Bars);
        if (TimeInMarket is { } timeInMarket) yield return (nameof(TimeInMarket), timeInMarket, MetricUnit.Bars);
        if (Exposure is { } exposure) yield return (nameof(Exposure), exposure, MetricUnit.ExposureRatio);
        if (ExposureAdjustedCAGR is { } exposureAdjustedCagr) yield return (nameof(ExposureAdjustedCAGR), exposureAdjustedCagr, MetricUnit.ReturnRatio);
    }

    // Counts
    public int TotalTrades { get; init; }
    public int WinTrades { get; init; }
    public int LossTrades { get; init; }
    public int BreakevenTrades { get; init; }

    /// <summary>Display-recommendation flag only (spec §5.4's SQN warning rule); never affects SQN's own Status.</summary>
    public bool SqnWarning { get; init; }

    // Metadata (from the BacktestReportOptions this report was generated with)
    /// <summary>Single owner of the report formula version emitted by the generator and mirrored into evaluation metadata.</summary>
    public const int CurrentFormulaVersion = 1;

    public int FormulaVersion { get; init; }
    public int AnnualPeriods { get; init; }
    public decimal AnnualRiskFreeRate { get; init; }
    public decimal AnnualMAR { get; init; }
    public TimeFrame Frame { get; init; }

    /// <summary>The explicit non-evidence mode for new yfinance-approximate exports; absent for legacy reports.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionModel? ExecutionModel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionDisclosure { get; init; }

    /// <summary>
    /// Identity of the run this report describes (owner decision G5): the SHA-256 run fingerprint, or the reason none exists (a strategy without a stable
    /// manifest). Null only when the caller supplied no frozen fingerprint builder in <see cref="BacktestReportOptions.RunFingerprintBuilder"/>.
    /// </summary>
    public BacktestReportRunFingerprint? RunFingerprint { get; init; }
}
