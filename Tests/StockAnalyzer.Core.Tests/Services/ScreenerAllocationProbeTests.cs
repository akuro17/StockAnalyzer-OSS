using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Services;

public class ScreenerAllocationProbeTests
{
    private readonly ITestOutputHelper _output;

    public ScreenerAllocationProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void MeasureAllocation_CriteriaEvaluation_1000Calls500Bars()
    {
        var bars = SyntheticBars.SeededRandomWalk(1, 500, 100m);
        string[] targetCriteria =
        {
            "Bullish Engulfing",
            "Granville's Law Buy Signal",
            "Elliott Wave Impulse 1-5"
        };

        var extractor = ScreenerValueExtractor.Default;
        const int warmupCount = 50;
        const int iterations = 1000;

        foreach (var name in targetCriteria)
        {
            var config = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Criteria,
                CustomDisplayName = name,
                OutputName = name,
                Offset = 0
            };

            // Warmup
            for (int i = 0; i < warmupCount; i++)
            {
                _ = extractor.ExtractValueNullable(config, bars);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                _ = extractor.ExtractValueNullable(config, bars);
            }
            long afterBytes = GC.GetAllocatedBytesForCurrentThread();

            long totalAllocated = afterBytes - beforeBytes;
            double bytesPerCall = (double)totalAllocated / iterations;

            _output.WriteLine($"CRITERIA_ALLOCATION: [{name}] Total: {totalAllocated:N0} bytes ({totalAllocated / (1024.0 * 1024.0):F2} MB), PerCall: {bytesPerCall:N1} bytes/call");

            Assert.True(totalAllocated > 0, $"Expected allocations to be measured for {name}");
        }
    }
}
