using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Builders shared by the condition-tree test classes (single definition; only helpers that were byte-identical in several classes live here).</summary>
internal static class ConditionTreeTestKit
{
    public static BacktestConditionGroup Group(LogicalOperator op, params IBacktestConditionNode[] children)
        => new(op, children.ToImmutableArray());

    public static BacktestConditionSide Side() => new() { IndicatorType = IndicatorType.SMA };

    /// <summary>A comparison "series > 0": true when the series value at the bar is 1, false when 0, false when null.</summary>
    public static BacktestConditionEntry Comparison(BacktestConditionSide side) => new()
    {
        Left = side,
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = 0m,
    };

    public static BacktestConditionLeaf Leaf(BacktestConditionSide side) => new(Comparison(side));

    public static BacktestConditionGroup And(params IBacktestConditionNode[] children) => new(LogicalOperator.And, children.ToImmutableArray());

    public static BacktestConditionGroup Or(params IBacktestConditionNode[] children) => new(LogicalOperator.Or, children.ToImmutableArray());

    /// <summary>Series where bar m carries bit k of m for leaf k, so every truth combination of the given leaves is one bar.</summary>
    public static (IndicatorSeriesSet Series, Dictionary<BacktestConditionSide, string> Map) TruthTable(params BacktestConditionSide[] sides)
    {
        int bars = 1 << sides.Length;
        var dict = new Dictionary<string, ImmutableArray<decimal?>>();
        var map = new Dictionary<BacktestConditionSide, string>();
        for (int k = 0; k < sides.Length; k++)
        {
            var values = new decimal?[bars];
            for (int m = 0; m < bars; m++) values[m] = (m >> k) & 1;
            dict["k" + k] = values.ToImmutableArray();
            map[sides[k]] = "k" + k;
        }
        return (new IndicatorSeriesSet(dict), map);
    }

    public static ImmutableArray<CandleData> WavyBars(int count = 60)
        => Enumerable.Range(0, count)
            .Select(i =>
            {
                decimal open = 100m + i % 7;
                decimal close = 100m + (i * 5 % 9) + (i % 3);
                return SyntheticBars.Bar(i, open, Math.Max(open, close) + 3m, Math.Min(open, close) - 3m, close);
            })
            .ToImmutableArray();

    public static BacktestConditionSide Close() => new() { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close };

    public static BacktestConditionSide Sma(int period) => new() { IndicatorType = IndicatorType.SMA, Parameters = new CoreSmaParameter { Period = period } };

    public static BacktestConditionEntry CloseVsSma(ComparisonOperator op, int period, BacktestConditionRole role, TradeSide position, LogicalOperator connector = LogicalOperator.And) => new()
    {
        Left = Close(),
        Operator = op,
        TargetMode = RightHandTargetMode.Indicator,
        Right = Sma(period),
        Role = role,
        Position = position,
        LogicalOperator = connector,
    };

    public static BacktestConditionLeaf CloseLeaf(ComparisonOperator op, decimal value) => new(new BacktestConditionEntry
    {
        Left = Close(),
        Operator = op,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = value,
    });

    public static BacktestConditionTree Tree(
        BacktestConditionGroup? entryLong = null, BacktestConditionGroup? entryShort = null,
        BacktestConditionGroup? exitLong = null, BacktestConditionGroup? exitShort = null,
        BacktestConditionGroup? reverseLong = null, BacktestConditionGroup? reverseShort = null) => new(
        entryLong ?? BacktestConditionGroup.EmptyAnd, entryShort ?? BacktestConditionGroup.EmptyAnd,
        exitLong ?? BacktestConditionGroup.EmptyAnd, exitShort ?? BacktestConditionGroup.EmptyAnd,
        reverseLong ?? BacktestConditionGroup.EmptyAnd, reverseShort ?? BacktestConditionGroup.EmptyAnd);

    public static BacktestResult Run(IBacktestStrategy strategy, ImmutableArray<CandleData>? bars = null)
        => VerificationHarness.CreateEngine().Run(
            VerificationHarness.MakeInput(bars ?? WavyBars()),
            VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 3m),
            strategy);

    public static string FingerprintHex(IBacktestStrategy strategy, ImmutableArray<CandleData> bars)
    {
        BacktestInput input = VerificationHarness.MakeInput(bars);
        BacktestConfiguration config = VerificationHarness.MakeConfig(initialCapital: 1000m, sizingParameter: 3m);
        BacktestRunFingerprintBuilder frozen = BacktestRunFingerprintBuilder.Freeze(input, config, strategy);
        BacktestResult result = VerificationHarness.CreateEngine().Run(input, config, strategy);
        return frozen.Seal(result).ToHexString();
    }
}
