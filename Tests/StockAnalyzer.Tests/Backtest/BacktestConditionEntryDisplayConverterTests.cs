using System.Globalization;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// SAで改善 bug fix (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): before this fix, a
/// "Price" condition (one catalog row per PriceType - Open/High/Low/...) always displayed as
/// "Price(Price) &gt; Price(Price)" regardless of which PriceType rows were actually selected, since
/// <see cref="BacktestConditionSide"/> had no field to carry the selection through and the converter never
/// looked at one. This proves the converter now shows the actual PriceType name (e.g. "High"/"Low")
/// instead, while leaving every non-Price condition's display unchanged.
/// </summary>
public class BacktestConditionEntryDisplayConverterTests
{
    private static string Convert(BacktestConditionEntry entry) =>
        (string)BacktestConditionEntryDisplayConverter.Instance.Convert(entry, typeof(string), null, CultureInfo.InvariantCulture)!;

    private static BacktestConditionSide PriceSide(PriceType priceSource, TimeFrame? frame = null, int offset = 0) => new()
    {
        IndicatorType = IndicatorType.Price,
        OutputName = priceSource.ToString(),
        PriceSource = priceSource,
        Frame = frame,
        Offset = offset,
    };

    [Fact]
    public void PriceCondition_DisplaysThePriceTypeName_NotTheGenericPriceLabel()
    {
        var entry = new BacktestConditionEntry
        {
            Left = PriceSide(PriceType.High),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            Right = PriceSide(PriceType.Low),
        };

        Assert.Equal("High > Low", Convert(entry));
    }

    [Fact]
    public void PriceCondition_WithFrameAndOffset_StillShowsThemInParentheses()
    {
        var entry = new BacktestConditionEntry
        {
            Left = PriceSide(PriceType.Close, frame: TimeFrame.W1, offset: 2),
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
        };

        Assert.Equal("Close(W1, -2) < 10", Convert(entry));
    }

    [Fact]
    public void NonPriceCondition_DisplayUnchanged_RegardlessOfPriceSourceFixExisting()
    {
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.SMA, OutputName = IndicatorResult.MainSeriesName },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 5m,
        };

        Assert.Equal($"{IndicatorType.SMA.GetFormalName()} > 5", Convert(entry));
    }

    /// <summary>
    /// SAで改善 (this round): the short/formal name (e.g. "SMA") is shown instead of the long localized
    /// Description (e.g. "Simple Moving Average"), and a single-series indicator's own fallback
    /// OutputName (ScreenerCatalogProvider.GetOutputSeriesNames returns the type's own name, e.g. "SMA",
    /// when there is no dedicated named output property) is no longer echoed back - it is replaced by the
    /// indicator's own parameter summary (CoreIndicatorParameterBase.GetDisplayName), e.g. "SMA(20)"
    /// instead of the previous "SMA(SMA)".
    /// </summary>
    [Fact]
    public void SingleOutputIndicator_ShowsShortNameWithPeriod_NotTheNameTwice()
    {
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new StockAnalyzer.Core.Models.Parameters.CoreSmaParameter { Period = 20 },
                OutputName = "SMA",
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 5m,
        };

        Assert.Equal("SMA (20) > 5", Convert(entry));
    }

    /// <summary>
    /// A genuinely distinct multi-output selection (e.g. MACD's "MACDLine"/"Signal"/"Histogram") is merged
    /// into the same trailing parenthetical as the period parameters, not repeated as a bare type name.
    /// </summary>
    [Fact]
    public void MultiOutputIndicator_MergesSelectedOutputIntoTheSameParenthetical()
    {
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide
            {
                IndicatorType = IndicatorType.MACD,
                Parameters = new StockAnalyzer.Core.Models.Parameters.CoreMacdParameter { ShortPeriod = 12, LongPeriod = 26, SignalPeriod = 9 },
                OutputName = "Histogram",
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
        };

        Assert.Equal("MACD (12, 26, 9, Histogram) > 0", Convert(entry));
    }

    [Fact]
    public void AuditAndExecutionExpression_ContainFrozenOperandsAndExplicitLeftGrouping()
    {
        var first = new BacktestConditionEntry
        {
            Left = PriceSide(PriceType.High, TimeFrame.D1, 1),
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            Right = PriceSide(PriceType.Low, TimeFrame.W1, 2),
            Role = BacktestConditionRole.EntryOnly,
            Position = TradeSide.Short,
            LogicalOperator = LogicalOperator.And,
        };
        var second = new BacktestConditionEntry
        {
            Left = PriceSide(PriceType.Close),
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 10m,
            Role = BacktestConditionRole.ExitOnly,
            Position = TradeSide.Long,
        };

        string expression = BacktestConditionFormatter.FormatExecutionExpression(new[] { first, second });
        string audit = BacktestConditionFormatter.FormatAudit(first, 7, expression);

        Assert.Contains("ShortEntry=(High(D1, -1) > Low(W1, -2))", expression);
        Assert.Contains("Exit=(Close < 10)", expression);
        Assert.DoesNotContain("AND (Close < 10)", expression);
        Assert.Contains("Index=7; Role=EntryOnly; Position=Short; ConnectorToNext=And", audit);
        Assert.Contains("IndicatorType=Price, OutputName=High, Frame=D1, PriceSource=High, Offset=1", audit);
        Assert.Contains("IndicatorType=Price, OutputName=Low, Frame=W1, PriceSource=Low, Offset=2", audit);
        Assert.Contains($"ExecutionPaths={expression}", audit);
    }

    [Fact]
    public void ExecutionExpression_UsesStrategyPartitionsAndFilteredConnectors()
    {
        BacktestConditionEntry Entry(decimal value, BacktestConditionRole role, TradeSide side, LogicalOperator connector) => new()
        {
            Left = PriceSide(PriceType.Close),
            RightNumericValue = value,
            Role = role,
            Position = side,
            LogicalOperator = connector,
        };

        string expression = BacktestConditionFormatter.FormatExecutionExpression(new[]
        {
            Entry(1m, BacktestConditionRole.EntryOnly, TradeSide.Long, LogicalOperator.And),
            Entry(2m, BacktestConditionRole.ExitOnly, TradeSide.Long, LogicalOperator.Or),
            Entry(3m, BacktestConditionRole.Both, TradeSide.Long, LogicalOperator.And),
            Entry(4m, BacktestConditionRole.Reversal, TradeSide.Short, LogicalOperator.And),
        });

        Assert.Contains("LongEntry=((Close > 1) AND (Close > 3))", expression);
        Assert.Contains("ShortEntry=(Close > 4)", expression);
        Assert.Contains("Exit=((Close > 2) OR (Close > 3))", expression);
        Assert.Contains("ReverseShort=(Close > 4)", expression);
        Assert.DoesNotContain("ReverseLong=", expression);
    }
}
