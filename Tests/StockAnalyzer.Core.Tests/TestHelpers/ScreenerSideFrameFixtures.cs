using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// Real bars and real condition entries for the tests of the Screener in which every side reads its own time frame. The daily bars are one per calendar day from
/// Monday 2024-01-01 and close at 100 + day index, so every number is worked out from that: the weekly bars of the first 28 days close at 106, 113, 120 and 127
/// (the Sundays 01-07, 01-14, 01-21 and 01-28); the 29th and 30th days (Monday 01-29 and Tuesday 01-30, closes 128 and 129) are a partial week.
/// </summary>
internal static class ScreenerSideFrameFixtures
{
    public static readonly DateTime FirstDay = new(2024, 1, 1);

    public static List<CandleData> Daily(int days)
        => Enumerable.Range(0, days)
            .Select(day => new CandleData(FirstDay.AddDays(day), 100m + day, 101m + day, 99m + day, 100m + day, 1000))
            .ToList();

    /// <summary>The weekly bars of the first 28 days of <paramref name="daily"/> (the complete weeks).</summary>
    public static IReadOnlyList<CandleData> CompleteWeeks(IReadOnlyList<CandleData> daily)
        => TimeFrameAggregator.Aggregate(daily.Take(28).ToList(), TimeFrame.W1);

    /// <summary>The bar set of the 30 daily bars: D1 as is, W1 = the four complete weeks.</summary>
    public static ScreenerBarSet BarSet(int days = 30)
    {
        List<CandleData> daily = Daily(days);
        return new ScreenerBarSet(new Dictionary<TimeFrame, IReadOnlyList<CandleData>>
        {
            [TimeFrame.D1] = daily,
            [TimeFrame.W1] = CompleteWeeks(daily),
        });
    }

    public static ScreenerIndicatorSideConfig Close(TimeFrame frame = TimeFrame.D1, int offset = 0) => new()
    {
        IndicatorType = IndicatorType.Price,
        OutputName = nameof(PriceType.Close),
        TimeFrame = frame,
        Offset = offset,
    };

    public static ScreenerIndicatorSideConfig Sma(int period, TimeFrame frame = TimeFrame.D1, int offset = 0) => new()
    {
        IndicatorType = IndicatorType.SMA,
        OutputName = "Main",
        Parameters = new Dictionary<string, object> { ["Period"] = period },
        TimeFrame = frame,
        Offset = offset,
    };

    /// <summary>A fundamental Column (it has no time frame); the value comes from the ticker metadata, which the tests leave empty.</summary>
    public static ScreenerIndicatorSideConfig Fundamental(TimeFrame frame) => new()
    {
        CategoryType = ScreenerItemCategoryType.Column,
        OutputName = "MarketCap",
        CustomDisplayName = "Market Cap",
        TimeFrame = frame,
    };

    /// <summary>A Column that names a bar value (Close): it has a time frame.</summary>
    public static ScreenerIndicatorSideConfig CloseColumn(TimeFrame frame) => new()
    {
        CategoryType = ScreenerItemCategoryType.Column,
        OutputName = "Close",
        CustomDisplayName = "Close",
        TimeFrame = frame,
    };

    public static ScreenerIndicatorEntry Versus(ScreenerIndicatorSideConfig left, ComparisonOperator op, decimal right, bool enabled = true) => new()
    {
        LeftHand = left,
        Operator = op,
        TargetMode = RightHandTargetMode.NumericValue,
        RightNumericValue = right,
        IsEnabled = enabled,
    };

    public static ScreenerIndicatorEntry Versus(ScreenerIndicatorSideConfig left, ComparisonOperator op, ScreenerIndicatorSideConfig right, bool enabled = true) => new()
    {
        LeftHand = left,
        Operator = op,
        TargetMode = RightHandTargetMode.Indicator,
        RightHand = right,
        IsEnabled = enabled,
    };

    /// <summary>Serves the daily bars it was given and records every request (a weekly/monthly request would show that stored weekly files are read).</summary>
    public sealed class RecordingDataService : IDataService
    {
        private readonly IReadOnlyDictionary<string, List<CandleData>> _bySymbol;

        public RecordingDataService(IReadOnlyDictionary<string, List<CandleData>> bySymbol) => _bySymbol = bySymbol;

        public System.Collections.Concurrent.ConcurrentBag<(string Symbol, TimeFrame TimeFrame, int Count)> Requests { get; } = new();

        public System.Threading.Tasks.Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            Requests.Add((symbol, timeFrame, count));
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<CandleData>>(_bySymbol.TryGetValue(symbol, out List<CandleData>? bars) ? bars : new List<CandleData>());
        }
    }

    /// <summary>A real <see cref="ScreenerService"/> over <see cref="RecordingDataService"/> with the given symbols, each with that many daily bars (see <see cref="Daily"/>).</summary>
    public static (ScreenerService Service, RecordingDataService Data) CreateService(params (string Symbol, int Days)[] symbols)
    {
        var data = new RecordingDataService(symbols.ToDictionary(item => item.Symbol, item => Daily(item.Days), StringComparer.Ordinal));
        return (new ScreenerService(data, new PythonIpcTestSettingsBase(), Array.Empty<IMarketDataProvider>()), data);
    }

    public static ScreenerConditionLeaf Leaf(ScreenerIndicatorEntry entry) => new(entry);

    public static ScreenerConditionGroup Group(LogicalOperator op, params IScreenerConditionNode[] children)
        => new(op, children.ToImmutableArray());
}

/// <summary>
/// A screener that implements only the two plain scan members (both throw), so every other member keeps the interface default and refuses with <see cref="NotSupportedException"/>.
/// Shared by the tests that pin those defaults.
/// </summary>
internal sealed class RefusingScreener : IScreenerService
{
    public System.Threading.Tasks.Task<List<string>> ScreenAsync(ScreeningCriteria criteria, IProgress<int> progress, System.Threading.CancellationToken ct)
        => throw new InvalidOperationException();

    public System.Threading.Tasks.Task<List<string>> ScreenAsync(List<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, IProgress<int> progress, System.Threading.CancellationToken ct)
        => throw new InvalidOperationException();
}
