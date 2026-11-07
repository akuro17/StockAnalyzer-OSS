#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Tests.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Opt-in measurement (MCP Part 2 external review, T9): what one <see cref="CoreAtrIndicator"/> calculation over 10,000 bars allocates on the calling thread, before and after the list of
/// true ranges was replaced by a running sum. One warm-up run (discarded), then five measured runs; the report holds the median and the minimum of
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Runs only when <see cref="MeasureVariable"/> is set; run it alone (<c>--filter</c>). No threshold is asserted.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class CoreAtrAllocationMeasurementTests
{
    private const string MeasureVariable = "SA_ATR_MEASURE";
    private const int Bars = 10_000;
    private const int Period = 14;
    private const int WarmUpRuns = 1;
    private const int MeasuredRuns = 5;
    private const int Seed = 11;
    private const decimal StartPrice = 100m;
    private const decimal MinPrice = 1m;
    private const decimal TickSize = 0.01m;
    private const decimal StepRange = 4m;       // a close moves by at most half of this either way
    private const decimal WickLength = 1m;
    private const long BarVolume = 1000;
    private const int PriceDecimals = 2;

    private readonly ITestOutputHelper _output;

    public CoreAtrAllocationMeasurementTests(ITestOutputHelper output) => _output = output;

    [EnvironmentFact(MeasureVariable)]
    public void T9_AllocationsOfOneAtrCalculation()
    {
        var random = new Random(Seed);
        decimal close = StartPrice;
        var candles = new List<CoreCandleData>(Bars);
        DateTime start = new(2000, 1, 1);
        for (int i = 0; i < Bars; i++)
        {
            decimal open = close;
            close = Math.Max(MinPrice, Math.Round(open + ((decimal)(random.NextDouble() - 0.5) * StepRange), PriceDecimals));
            candles.Add(new CoreCandleData(start.AddDays(i), open, Math.Max(open, close) + WickLength, Math.Max(TickSize, Math.Min(open, close) - WickLength), close, BarVolume));
        }

        var bytes = new List<long>();
        int values = 0;
        for (int run = 0; run < WarmUpRuns + MeasuredRuns; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var indicator = new CoreAtrIndicator { Period = Period };
            long before = GC.GetAllocatedBytesForCurrentThread();

            indicator.Calculate(candles);

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            values = indicator.Values.Count;
            if (run >= WarmUpRuns)
            {
                bytes.Add(allocated);
            }
        }

        long[] ordered = bytes.OrderBy(value => value).ToArray();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"T9 {Bars} bars, ATR({Period}), {WarmUpRuns} warm-up + {MeasuredRuns} runs: values {values}, allocated bytes (current thread) median {ordered[ordered.Length / 2]:N0}, min {ordered[0]:N0}"));
        Assert.Equal(Bars, values);
    }
}
