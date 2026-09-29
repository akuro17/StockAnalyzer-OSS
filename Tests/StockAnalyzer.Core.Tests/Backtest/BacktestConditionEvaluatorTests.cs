using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class BacktestConditionEvaluatorTests
{
    private static IndicatorSeriesSet BuildSeries(params (string Key, decimal?[] Values)[] series)
    {
        var dict = new Dictionary<string, ImmutableArray<decimal?>>();
        foreach (var (key, values) in series)
        {
            dict[key] = values.ToImmutableArray();
        }
        return new IndicatorSeriesSet(dict);
    }

    private static BacktestConditionSide Side(int offset = 0) => new()
    {
        IndicatorType = IndicatorType.SMA,
        Offset = offset,
    };

    [Theory]
    [InlineData(ComparisonOperator.GreaterThan, 5, 3, true)]
    [InlineData(ComparisonOperator.GreaterThan, 3, 5, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 5, 5, true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 4, 5, false)]
    [InlineData(ComparisonOperator.LessThan, 3, 5, true)]
    [InlineData(ComparisonOperator.LessThan, 5, 3, false)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 5, 5, true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 6, 5, false)]
    [InlineData(ComparisonOperator.Equal, 5, 5, true)]
    [InlineData(ComparisonOperator.Equal, 5, 6, false)]
    [InlineData(ComparisonOperator.NotEqual, 5, 6, true)]
    [InlineData(ComparisonOperator.NotEqual, 5, 5, false)]
    public void Evaluate_NumericComparison_AllOperators(ComparisonOperator op, decimal leftValue, decimal rightValue, bool expected)
    {
        var left = Side();
        var entry = new BacktestConditionEntry
        {
            Left = left,
            Operator = op,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = rightValue,
        };
        var indicators = BuildSeries(("Left", new decimal?[] { leftValue }));
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "Left" };

        bool actual = BacktestConditionEvaluator.Evaluate(new[] { entry }, map, indicators, barIndex: 0);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Evaluate_IndicatorVsIndicator_ComparesBothSides()
    {
        var left = Side();
        var right = Side();
        var entry = new BacktestConditionEntry
        {
            Left = left,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            Right = right,
        };
        var indicators = BuildSeries(
            ("Left", new decimal?[] { 10m }),
            ("Right", new decimal?[] { 8m }));
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "Left", [right] = "Right" };

        Assert.True(BacktestConditionEvaluator.Evaluate(new[] { entry }, map, indicators, barIndex: 0));
    }

    [Fact]
    public void Evaluate_OffsetArithmetic_ReadsBarsBeforeCurrentIndex()
    {
        var left = Side(offset: 1);
        var entry = new BacktestConditionEntry
        {
            Left = left,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 5m,
        };
        // bar 0 = 100 (would satisfy > 5), bar 1 = 1 (would not satisfy > 5).
        // At barIndex=1 with Offset=1, the evaluator must read bar 0 (100), not bar 1 (1).
        var indicators = BuildSeries(("Left", new decimal?[] { 100m, 1m }));
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "Left" };

        Assert.True(BacktestConditionEvaluator.Evaluate(new[] { entry }, map, indicators, barIndex: 1));
    }

    [Fact]
    public void Evaluate_OutOfRangeBarIndex_ReturnsFalse_DoesNotThrow()
    {
        var left = Side(offset: 5);
        var entry = new BacktestConditionEntry
        {
            Left = left,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
        };
        var indicators = BuildSeries(("Left", new decimal?[] { 100m }));
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "Left" };

        // barIndex - offset = 0 - 5 = -5, out of range -> ValueAt returns null -> entry is false.
        bool actual = BacktestConditionEvaluator.Evaluate(new[] { entry }, map, indicators, barIndex: 0);

        Assert.False(actual);
    }

    [Fact]
    public void Evaluate_AndChaining_BothMustBeTrue()
    {
        var leftA = Side();
        var leftB = Side();
        var entryA = new BacktestConditionEntry
        {
            Left = leftA,
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 0m,
            LogicalOperator = LogicalOperator.And,
        };
        var entryB = new BacktestConditionEntry
        {
            Left = leftB,
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 100m,
        };
        var indicators = BuildSeries(("A", new decimal?[] { 5m }), ("B", new decimal?[] { 5m }));
        var map = new Dictionary<BacktestConditionSide, string> { [leftA] = "A", [leftB] = "B" };

        // A: 5 > 0 = true, B: 5 > 100 = false, AND => false.
        Assert.False(BacktestConditionEvaluator.Evaluate(new[] { entryA, entryB }, map, indicators, barIndex: 0));
    }

    [Fact]
    public void Evaluate_OrChaining_EitherTrueSucceeds()
    {
        var leftA = Side();
        var leftB = Side();
        var entryA = new BacktestConditionEntry
        {
            Left = leftA,
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 100m,
            LogicalOperator = LogicalOperator.Or,
        };
        var entryB = new BacktestConditionEntry
        {
            Left = leftB,
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 0m,
        };
        var indicators = BuildSeries(("A", new decimal?[] { 5m }), ("B", new decimal?[] { 5m }));
        var map = new Dictionary<BacktestConditionSide, string> { [leftA] = "A", [leftB] = "B" };

        // A: 5 > 100 = false, B: 5 > 0 = true, OR => true.
        Assert.True(BacktestConditionEvaluator.Evaluate(new[] { entryA, entryB }, map, indicators, barIndex: 0));
    }

    [Fact]
    public void Evaluate_EmptyEntryList_ReturnsFalse()
    {
        var indicators = BuildSeries();
        var map = new Dictionary<BacktestConditionSide, string>();

        Assert.False(BacktestConditionEvaluator.Evaluate(Array.Empty<BacktestConditionEntry>(), map, indicators, barIndex: 0));
    }

    [Fact]
    public void Evaluate_StringValueTargetMode_ThrowsNotSupported()
    {
        var left = Side();
        var entry = new BacktestConditionEntry
        {
            Left = left,
            TargetMode = RightHandTargetMode.StringValue,
        };
        var indicators = BuildSeries(("Left", new decimal?[] { 5m }));
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "Left" };

        Assert.Throws<NotSupportedException>(() =>
            BacktestConditionEvaluator.Evaluate(new[] { entry }, map, indicators, barIndex: 0));
    }

    [Fact]
    public void Evaluate_ContainsOperator_ThrowsNotSupported()
    {
        var left = Side();
        var entry = new BacktestConditionEntry
        {
            Left = left,
            Operator = ComparisonOperator.Contains,
            RightNumericValue = 0m,
        };
        var indicators = BuildSeries(("Left", new decimal?[] { 5m }));
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "Left" };

        Assert.Throws<NotSupportedException>(() =>
            BacktestConditionEvaluator.Evaluate(new[] { entry }, map, indicators, barIndex: 0));
    }

    [Fact]
    public void Evaluate_IndicatorTargetMode_MissingRight_Throws()
    {
        var left = Side();
        var entry = new BacktestConditionEntry
        {
            Left = left,
            TargetMode = RightHandTargetMode.Indicator,
            Right = null,
        };
        var indicators = BuildSeries(("Left", new decimal?[] { 5m }));
        var map = new Dictionary<BacktestConditionSide, string> { [left] = "Left" };

        Assert.Throws<InvalidOperationException>(() =>
            BacktestConditionEvaluator.Evaluate(new[] { entry }, map, indicators, barIndex: 0));
    }
}
