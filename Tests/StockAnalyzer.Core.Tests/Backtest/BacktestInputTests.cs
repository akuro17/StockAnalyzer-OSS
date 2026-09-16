using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class BacktestInputTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CandleData MakeBar(DateTime timestamp, decimal open, decimal high, decimal low, decimal close, long volume = 1000)
        => new(timestamp, open, high, low, close, volume);

    private static ImmutableArray<CandleData> ThreeValidBars() => ImmutableArray.Create(
        MakeBar(Bar0, 100m, 105m, 95m, 102m),
        MakeBar(Bar0.AddDays(1), 102m, 108m, 100m, 106m),
        MakeBar(Bar0.AddDays(2), 106m, 110m, 104m, 108m));

    private static BacktestInput Build(
        ImmutableArray<CandleData> bars,
        string symbol = "AAPL",
        TimeFrame frame = TimeFrame.D1,
        int dataVersion = BacktestInput.CurrentDataVersion,
        int historyStartIndex = 0,
        int tradingStartIndex = 0)
        => new(bars, symbol, frame, dataVersion, Bar0, Bar0.AddDays(30), historyStartIndex, tradingStartIndex);

    [Fact]
    public void ValidInput_Constructs()
    {
        var input = Build(ThreeValidBars(), tradingStartIndex: 0);
        Assert.Equal(3, input.Bars.Length);
        Assert.Equal("AAPL", input.Symbol);
    }

    [Fact]
    public void NullSymbol_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Build(ThreeValidBars(), symbol: null!));
    }

    [Fact]
    public void DefaultImmutableArray_Throws()
    {
        Assert.Throws<ArgumentException>(() => Build(default));
    }

    [Fact]
    public void BlankSymbol_Throws()
    {
        Assert.Throws<ArgumentException>(() => Build(ThreeValidBars(), symbol: "   "));
    }

    [Fact]
    public void UndefinedFrame_Throws()
    {
        Assert.Throws<ArgumentException>(() => Build(ThreeValidBars(), frame: (TimeFrame)999));
    }

    [Fact]
    public void UnsupportedDataVersion_Throws()
    {
        Assert.Throws<UnsupportedDataVersionException>(() => Build(ThreeValidBars(), dataVersion: 999));
    }

    [Fact]
    public void NonUtcBarTimestamp_Throws()
    {
        var bars = ImmutableArray.Create(MakeBar(DateTime.SpecifyKind(Bar0, DateTimeKind.Local), 100m, 105m, 95m, 102m));
        Assert.Throws<ArgumentException>(() => Build(bars));
    }

    [Fact]
    public void ChronologicalViolation_DuplicateTimestamp_Throws()
    {
        var bars = ImmutableArray.Create(
            MakeBar(Bar0, 100m, 105m, 95m, 102m),
            MakeBar(Bar0, 102m, 108m, 100m, 106m));
        Assert.Throws<ArgumentException>(() => Build(bars));
    }

    [Fact]
    public void ChronologicalViolation_OutOfOrder_Throws()
    {
        var bars = ImmutableArray.Create(
            MakeBar(Bar0.AddDays(1), 100m, 105m, 95m, 102m),
            MakeBar(Bar0, 102m, 108m, 100m, 106m));
        Assert.Throws<ArgumentException>(() => Build(bars));
    }

    [Fact]
    public void InvalidOHLC_HighBelowLow_Throws()
    {
        var bars = ImmutableArray.Create(MakeBar(Bar0, 100m, 90m, 95m, 92m));
        Assert.Throws<ArgumentException>(() => Build(bars));
    }

    [Theory]
    [InlineData(0.0, 105.0, 95.0, 102.0)]
    [InlineData(100.0, 0.0, 95.0, 102.0)]
    [InlineData(100.0, 105.0, 0.0, 102.0)]
    [InlineData(100.0, 105.0, 95.0, 0.0)]
    public void NonPositiveOhlc_Throws(double open, double high, double low, double close)
    {
        var bars = ImmutableArray.Create(MakeBar(Bar0, (decimal)open, (decimal)high, (decimal)low, (decimal)close));
        Assert.Throws<ArgumentException>(() => Build(bars));
    }

    [Fact]
    public void HistoryStartIndex_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(ThreeValidBars(), historyStartIndex: 4));
    }

    [Fact]
    public void HistoryStartIndex_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(ThreeValidBars(), historyStartIndex: -1));
    }

    [Fact]
    public void TradingStartIndex_BelowHistoryStartIndex_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(ThreeValidBars(), historyStartIndex: 2, tradingStartIndex: 1));
    }

    [Fact]
    public void TradingStartIndex_AboveCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(ThreeValidBars(), tradingStartIndex: 4));
    }

    [Fact]
    public void EmptyBars_ConstructsSuccessfully()
    {
        var input = Build(ImmutableArray<CandleData>.Empty);
        Assert.Equal(0, input.Bars.Length);
    }

    [Fact]
    public void SingleBar_ConstructsSuccessfully()
    {
        var bars = ImmutableArray.Create(MakeBar(Bar0, 100m, 105m, 95m, 102m));
        var input = Build(bars, tradingStartIndex: 0);
        Assert.Equal(1, input.Bars.Length);
    }

    [Fact]
    public void Symbol_IsNfcNormalized()
    {
        // Half-width katakana would also have a distinct NFC-normalized form, but an ASCII-safe
        // composed-form check keeps this test free of non-English characters: a combining acute accent
        // should normalize to its precomposed form.
        string decomposedEAcute = "é"; // "e" + combining acute accent
        var input = Build(ThreeValidBars(), symbol: decomposedEAcute);
        Assert.Equal("é", input.Symbol); // precomposed "é"
    }
}
