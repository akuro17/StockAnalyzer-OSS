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

    public MetricValue CalmarFullPeriod { get; init; }
    public MetricValue SQN { get; init; }
    public MetricValue RecoveryFactor { get; init; }

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
