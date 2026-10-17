using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class ScreenerFaultLogTests
{
    [Fact]
    public void Record_IncrementsCount_AndCapturesFirstErrorWithTruncation()
    {
        var log = new ScreenerFaultLog();
        string longMessage = new string('x', 250);
        var ex1 = new InvalidOperationException(longMessage);
        var ex2 = new ArgumentException("Second error");

        log.Record(ScreenerFaultLog.SiteCriteria, ex1);
        log.Record(ScreenerFaultLog.SiteCriteria, ex2);

        var snapshot = log.TakeSnapshotAndReset();
        Assert.Single(snapshot);
        var fault = snapshot[0];
        Assert.Equal(ScreenerFaultLog.SiteCriteria, fault.Site);
        Assert.Equal(2, fault.Count);
        // Error format: "InvalidOperationException: {200 chars of message}"
        Assert.StartsWith("InvalidOperationException: ", fault.FirstError);
        string capturedMsg = fault.FirstError.Substring("InvalidOperationException: ".Length);
        Assert.Equal(200, capturedMsg.Length);

        // After reset, second snapshot is empty
        var secondSnapshot = log.TakeSnapshotAndReset();
        Assert.Empty(secondSnapshot);
    }

    [Fact]
    public void Record_ConcurrentCalls_ThreadSafelyIncrementsCount()
    {
        var log = new ScreenerFaultLog();
        int iterations = 1000;

        Parallel.For(0, iterations, i =>
        {
            log.Record(ScreenerFaultLog.SiteIndicatorCalculate, new Exception($"Error {i}"));
        });

        var snapshot = log.TakeSnapshotAndReset();
        Assert.Single(snapshot);
        Assert.Equal(ScreenerFaultLog.SiteIndicatorCalculate, snapshot[0].Site);
        Assert.Equal(iterations, snapshot[0].Count);
    }

    [Fact]
    public void ScreenerValueExtractor_InvalidIndicatorParameter_RecordsIndicatorConfigureFault()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = new List<CandleData>
        {
            new(DateTime.Today, 100m, 110m, 90m, 105m, 1000),
            new(DateTime.Today.AddDays(1), 105m, 115m, 95m, 110m, 1000),
        };

        var config = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Indicator,
            IndicatorType = IndicatorType.SMA,
            OutputName = "SMA",
            Parameters = new Dictionary<string, object>
            {
                ["Period"] = "abc" // Cannot convert string "abc" to int Period
            }
        };

        // Call extractor: should not throw, returns calculation with default or null
        extractor.ExtractValueNullable(config, candles);

        var faults = extractor.Faults.TakeSnapshotAndReset();
        Assert.Single(faults);
        Assert.Equal(ScreenerFaultLog.SiteIndicatorConfigure, faults[0].Site);
        Assert.Equal(1, faults[0].Count);
        Assert.Contains("FormatException", faults[0].FirstError);

        // A second call increments to 2
        extractor.ExtractValueNullable(config, candles);
        extractor.ExtractValueNullable(config, candles);
        var faults2 = extractor.Faults.TakeSnapshotAndReset();
        Assert.Single(faults2);
        Assert.Equal(2, faults2[0].Count);
    }
}
