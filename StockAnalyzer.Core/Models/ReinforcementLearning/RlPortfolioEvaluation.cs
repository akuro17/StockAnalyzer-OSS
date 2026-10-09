using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// Constants of the portfolio evaluation protocol (cells, baselines, report). Kept apart from <see cref="RlPortfolioContract"/> so the
/// pinned hash of the training contract stays valid; the report carries both hashes. Every public constant feeds <see cref="ContractHash"/>.
/// </summary>
public static class RlPortfolioEvaluationContract
{
    public const int SchemaVersion = 1;
    /// <summary>Report file inside a run folder.</summary>
    public const string ReportFile = "evaluation.json";
    /// <summary>Largest report the reader accepts (it holds one return per block and arm, so it is small).</summary>
    public const long MaximumReportBytes = 8L * 1024 * 1024;

    public const string CashBaseline = "cash";
    public const string EqualBuyHoldBaseline = "equal_buy_hold";
    public const string EqualDailyRebalancedBaseline = "equal_daily_rebalanced";

    public const string Statement = "Descriptive evidence; pooled symbols are correlated; not an efficacy claim.";
    public const string ShortHistoryNote = "Short-history evaluation: descriptive; not statistically independent evidence.";
    public const string TimeHoldoutOnlyNote = "Time holdout only: no symbol holdout was used.";
    /// <summary>Baselines run under the same per-asset weight cap as the policy, in the same session and cost model.</summary>
    public const string SameCapNote = "Baselines use the same per-asset weight cap as the policy.";

    /// <summary>Baseline ids in report order.</summary>
    public static ImmutableArray<string> Baselines { get; } = ImmutableArray.Create(CashBaseline, EqualBuyHoldBaseline, EqualDailyRebalancedBaseline);

    public static IReadOnlyDictionary<string, object> ToWireDefaults()
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(RlPortfolioEvaluationContract).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.IsLiteral) values.Add(field.Name, field.GetValue(null)!);
        values.Add(nameof(Baselines), Baselines.ToArray());
        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    public static string ContractHash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToWireDefaults()))));
}

/// <summary>
/// Terminal returns of one complete block: the policy and every baseline on the same block with the same costs. A terminal return is
/// <c>(E_final - InitialCapital) / InitialCapital</c> in decimal; a paired delta is the policy return minus a baseline return.
/// </summary>
public sealed record RlPortfolioBlockResult(string BlockId, ImmutableArray<string> TradableSymbols, decimal PolicyReturn,
    decimal CashReturn, decimal EqualBuyHoldReturn, decimal EqualDailyRebalancedReturn);

/// <summary>Distribution of one baseline's returns and of the paired policy-minus-baseline deltas over the cell's blocks.</summary>
public sealed record RlPortfolioBaselineSummary(string Id, RlReturnDistribution Returns, RlReturnDistribution PairedDelta);

/// <summary>
/// One evaluation cell (partition x symbol group). A cell below its minimum block count is stated as not evaluable, with no rows and no
/// statistics, and is never dropped from the report.
/// </summary>
public sealed record RlPortfolioCellReport(string Partition, RlPanelSymbolGroup Group, ImmutableArray<string> Symbols, int BlockCount,
    int RequiredBlocks, bool Evaluable, ImmutableArray<RlPortfolioBlockResult> Blocks, RlReturnDistribution? Policy,
    ImmutableArray<RlPortfolioBaselineSummary> Baselines);

public sealed record RlPortfolioEvaluationReport(
    string SpecificationId, int SchemaVersion, string ContractHash, string PortfolioContractHash, string EvaluationSpecificationId,
    string UniverseSha256, string CalendarSha256, string ModelSha256,
    ImmutableArray<string> Symbols, ImmutableArray<string> TrainSymbols, ImmutableArray<string> HoldoutSymbols, bool SymbolHoldout,
    BacktestConfiguration Costs, int MaxWeightPercent, bool SmokeOnly,
    ImmutableArray<RlPortfolioCellReport> Cells, string PricePit, string InvestmentEfficacy, string Statement, ImmutableArray<string> Notes);
