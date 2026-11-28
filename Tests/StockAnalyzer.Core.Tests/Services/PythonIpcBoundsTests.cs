using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class PythonIpcBoundsTests
{
    private sealed class DummySettings : PythonIpcTestSettingsBase
    {
        public override string PipeName => "ipcboundsunusedpipe";
    }

    [Fact]
    public async Task SendCandlesAsync_ExceedingMaxTransferRows_ThrowsArgumentOutOfRange()
    {
        var service = new PythonService(new DummySettings());
        var oversized = new List<CandleData>(PythonIpcLimits.MaxTransferRows + 1);
        var start = new DateTime(2000, 1, 1);
        for (int i = 0; i <= PythonIpcLimits.MaxTransferRows; i++)
        {
            oversized.Add(new CandleData(start.AddMinutes(i), 10m, 11m, 9m, 10m, 100));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SendCandlesAsync(oversized));
    }

    [Theory]
    [InlineData(PythonIpcLimits.MinWindowSize - 1, 1)]
    [InlineData(PythonIpcLimits.MaxWindowSize + 1, 1)]
    [InlineData(64, 0)]
    [InlineData(64, 34)]
    public async Task CalculateFftTrendFilterAsync_OutOfRangeArgs_ThrowsArgumentOutOfRange(int windowSize, int numHarmonics)
    {
        var service = new PythonService(new DummySettings());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CalculateFftTrendFilterAsync(windowSize, numHarmonics));
    }

    [Theory]
    [InlineData(-1, 5, 20, 0.3, 30, -1, -1)]
    [InlineData(PythonIpcLimits.MaxLookback + 1, 5, 20, 0.3, 30, -1, -1)]
    [InlineData(0, 0, 20, 0.3, 30, -1, -1)]
    [InlineData(0, PythonIpcLimits.MaxTopK + 1, 20, 0.3, 30, -1, -1)]
    [InlineData(0, 5, 0, 0.3, 30, -1, -1)]
    [InlineData(0, 5, PythonIpcLimits.MaxFutureSteps + 1, 0.3, 30, -1, -1)]
    [InlineData(0, 5, 20, -0.1, 30, -1, -1)]
    [InlineData(0, 5, 20, 1.1, 30, -1, -1)]
    [InlineData(0, 5, 20, double.NaN, 30, -1, -1)]
    [InlineData(0, 5, 20, double.PositiveInfinity, 30, -1, -1)]
    [InlineData(0, 5, 20, 0.3, 1, -1, -1)]
    [InlineData(0, 5, 20, 0.3, PythonIpcLimits.MaxQueryLength + 1, -1, -1)]
    [InlineData(0, 5, 20, 0.3, 30, -2, -1)]
    [InlineData(0, 5, 20, 0.3, 30, -1, -2)]
    [InlineData(0, 5, 20, 0.3, 30, -1, PythonIpcLimits.MaxWarpingRadius + 1)]
    public async Task SearchSimilarPatternsAsync_OutOfRangeArgs_ThrowsArgumentOutOfRange(
        int lookback,
        int topK,
        int futureSteps,
        double threshold,
        int queryLength,
        int queryStartIndex,
        int warpingRadius)
    {
        var service = new PythonService(new DummySettings());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchSimilarPatternsAsync(
                lookback: lookback,
                topK: topK,
                futureSteps: futureSteps,
                threshold: threshold,
                queryLength: queryLength,
                queryStartIndex: queryStartIndex,
                warpingRadius: warpingRadius));
    }
}
