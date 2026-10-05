using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public sealed record RlEpisodeEvaluation(string BlockId, string Partition, string TerminalReturn,
    string MaximumDrawdown, int CompletedDecisions, string TerminalReason, string ResidualQuantity,
    string RealizedPnl, string UnrealizedPnl, string Costs, int Rejections, int NoOps)
{
    public JsonElement? Constraints { get; init; }
}

/// <summary>All report money, returns, and aggregation stay in the decimal Core accountant.</summary>
public static class RlEvaluation
{
    public static string DecimalText(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    public static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    public static RlEpisodeEvaluation Finish(RlEpisodeBlock block, RlSessionSnapshot snapshot, BacktestResult result,
        int rejections, int noOps)
    {
        if (!snapshot.Terminated) throw new InvalidOperationException("Evaluation requires natural termination.");
        return new(block.Id, block.Partition, DecimalText((snapshot.EquityPoint.Equity - result.Configuration.InitialCapital) / result.Configuration.InitialCapital),
            DecimalText(snapshot.MaximumDrawdown), snapshot.CompletedDecisions, snapshot.TerminalReason.ToString(),
            DecimalText(snapshot.Quantity), DecimalText(result.Trades.Sum(t => t.ClosedNet)),
            DecimalText(snapshot.UnrealizedPnL), DecimalText(result.Fills.Sum(f => f.Commission + f.SlippageAmount)), rejections, noOps);
    }

    public static Dictionary<string, object> Aggregate(IReadOnlyList<RlEpisodeEvaluation> rows, bool expanded = false)
    {
        if (rows.Count == 0) throw new InvalidOperationException("Empty evaluation cannot be aggregated.");
        var result = new Dictionary<string, object>()
        {
            ["episodes"] = rows,
            ["mean_return"] = DecimalText(rows.Sum(e => Parse(e.TerminalReturn)) / rows.Count),
            ["mean_maximum_drawdown"] = DecimalText(rows.Sum(e => Parse(e.MaximumDrawdown)) / rows.Count),
            ["worst_maximum_drawdown"] = DecimalText(rows.Max(e => Parse(e.MaximumDrawdown))),
        };
        if (expanded) result["return_distribution"] = RlConstraintEvaluation.ToWire(RlAllBlockEvaluation.Distribution(rows.Select(e => Parse(e.TerminalReturn))));
        return result;
    }
}
