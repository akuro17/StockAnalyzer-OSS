using System;
using System.Collections;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class DrawingCalculationContextTests
{
    private class MockIndicatorResult : List<decimal?>, IIndicatorResult
    {
        public bool IsSuccessful => true;
        public string? ErrorMessage => null;
        public IReadOnlyList<decimal?> MainValues => this;
        public IEnumerable<string> SeriesNames => Array.Empty<string>();
        public IReadOnlyList<string> SeriesNamesList => Array.Empty<string>();
        public IReadOnlyDictionary<string, string> SeriesLabels => new Dictionary<string, string>();
        public object? CustomData => null;
        public IConfluenceSignalProvider? SignalProvider => null;

        public bool HasSeries(string name) => false;
        public IReadOnlyList<decimal?> GetSeries(string name) => Array.Empty<decimal?>();
    }

    [Fact]
    public void DrawingCalculationContext_LookupHelpers_ReturnExpectedResults()
    {
        var candles = new List<CoreCandleData>
        {
            new(new DateTime(2026, 1, 1), 100m, 105m, 95m, 102m, 1000)
        };

        var mockAtr = new MockIndicatorResult { 2.5m };

        var results = new Dictionary<string, IIndicatorResult>
        {
            ["atr_1"] = mockAtr
        };

        var settings = new List<CoreIndicatorSettings>
        {
            new() { Id = "atr_1", TypeEnum = IndicatorType.ATR, IsEnabled = true }
        };

        var context = new DrawingCalculationContext(candles, results, settings, "TEST");

        Assert.True(context.HasCandles);
        Assert.True(context.HasIndicators);
        Assert.Equal("TEST", context.Symbol);

        // Direct lookup by ID
        Assert.True(context.TryGetIndicatorResult("atr_1", out var res));
        Assert.Same(mockAtr, res);

        // Lookup by type
        Assert.True(context.TryGetFirstIndicatorResultByType("ATR", out var resType, out var settingType));
        Assert.Same(mockAtr, resType);
        Assert.NotNull(settingType);
        Assert.Equal("atr_1", settingType.Id);
    }

    [Fact]
    public void LongShortPositionObject_WithAtrMultiplier_DynamicallyAdjustsLevels()
    {
        var dt = new DateTime(2026, 1, 1);
        var candles = new List<CoreCandleData>
        {
            new(dt, 100m, 105m, 95m, 100m, 1000)
        };

        var mockAtr = new MockIndicatorResult { 2.0m };

        var results = new Dictionary<string, IIndicatorResult>
        {
            ["atr_1"] = mockAtr
        };

        var settings = new List<CoreIndicatorSettings>
        {
            new() { Id = "atr_1", TypeEnum = IndicatorType.ATR, IsEnabled = true }
        };

        var context = new DrawingCalculationContext(candles, results, settings, "TEST");

        // Long position with entry 100, initial stop 95, initial target 110 (Risk = 5, Reward = 10, RR = 2.0)
        var entry = new ChartPoint(dt, 100m);
        var stop = new ChartPoint(dt, 95m);
        var target = new ChartPoint(dt, 110m);
        var obj = new LongShortPositionObject(entry, stop, target, isLong: true)
        {
            AtrMultiplier = 1.5m // ATR distance = 2.0 * 1.5 = 3.0
        };

        obj.Recalculate(context);

        // New stop should be 100 - 3.0 = 97.0
        Assert.Equal(97.0m, obj.Points[1].Price);
        // New target should maintain 1:2 RR ratio -> 100 + (3.0 * 2.0) = 106.0
        Assert.Equal(106.0m, obj.Points[2].Price);

        var values = obj.GetCalculatedValues(dt);
        Assert.Contains(values, v => v.Key == "ATR_Entry" && v.NumericValue == 2.0m);
    }

    [Fact]
    public void DeferredComputationRecalculator_DispatchesContextCorrectly()
    {
        var dt = new DateTime(2026, 1, 1);
        var candles = new List<CoreCandleData>
        {
            new(dt, 100m, 105m, 95m, 100m, 1000)
        };

        var mockAtr = new MockIndicatorResult { 4.0m };

        var context = new DrawingCalculationContext(
            candles,
            new Dictionary<string, IIndicatorResult> { ["atr_main"] = mockAtr },
            new List<CoreIndicatorSettings> { new() { Id = "atr_main", TypeEnum = IndicatorType.ATR, IsEnabled = true } },
            "TEST"
        );

        var longShort = new LongShortPositionObject(new ChartPoint(dt, 100m), new ChartPoint(dt, 90m), new ChartPoint(dt, 120m), isLong: true)
        {
            BoundIndicatorId = "atr_main",
            AtrMultiplier = 1.0m // distance = 4.0 * 1.0 = 4.0
        };

        bool recalculated = DeferredComputationRecalculator.TryRecalculate(longShort, context);

        Assert.True(recalculated);
        Assert.Equal(96.0m, longShort.Points[1].Price); // 100 - 4.0 = 96.0
        Assert.Equal(108.0m, longShort.Points[2].Price); // 100 + (4.0 * 2.0) = 108.0
    }
}
