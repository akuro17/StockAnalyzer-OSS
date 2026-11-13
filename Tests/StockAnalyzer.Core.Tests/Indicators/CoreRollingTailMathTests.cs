using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>Plan 02 T7: the shared rolling window of VaR / CVaR (same output as before, far fewer allocations).</summary>
public class CoreRollingTailMathTests
{
    private static List<CoreCandleData> Candles(int count, params int[] zeroBars)
    {
        var list = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + 8m * (decimal)Math.Sin(i * 0.17) + (i * 7 % 13) * 0.31m + i * 0.01m;
            if (zeroBars.Contains(i)) close = 0m;
            list.Add(new CoreCandleData(new DateTime(2000, 1, 1).AddDays(i), close, close + 1m, close - 1m, close, 1000));
        }

        return list;
    }

    [Fact]
    public void TryLoadSortedWindow_SortsTheRangeAscending_AndRefusesARangeWithANull()
    {
        decimal?[] returns = { 0.03m, -0.02m, 0.01m, null, 0.05m, -0.04m };
        Span<decimal> buffer = stackalloc decimal[8];

        Assert.True(RollingTailMath.TryLoadSortedWindow(returns, 0, 3, buffer));
        Assert.Equal(new[] { -0.02m, 0.01m, 0.03m }, buffer.Slice(0, 3).ToArray());

        Assert.False(RollingTailMath.TryLoadSortedWindow(returns, 2, 3, buffer));      // contains the null
        Assert.True(RollingTailMath.TryLoadSortedWindow(returns, 4, 2, buffer));
        Assert.Equal(new[] { -0.04m, 0.05m }, buffer.Slice(0, 2).ToArray());
    }

    [Fact]
    public void ComputeReturns_MarksAnUndefinedReturnNull()
    {
        var prices = new List<decimal?> { 100m, 110m, null, 120m, 0m, 50m };
        decimal?[] returns = RollingTailMath.ComputeReturns(prices);

        Assert.Equal(5, returns.Length);
        Assert.Equal(0.1m, returns[0]);
        Assert.Null(returns[1]);   // current price missing
        Assert.Null(returns[2]);   // previous price missing
        Assert.Equal(-1m, returns[3]);
        Assert.Null(returns[4]);   // previous price 0
    }

    // Window longer than the stack limit (rented array) must give what the brute-force definition gives.
    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(300)]
    public void LongWindows_MatchTheDefinition_ForVarAndCVar(int period)
    {
        var candles = Candles(period + 40);
        double confidence = 0.95;
        var var = new CoreVarIndicator { Period = period, ConfidenceLevel = confidence }.Calculate(candles).MainValues;
        var cvar = new CoreCVarIndicator { Period = period, ConfidenceLevel = confidence }.Calculate(candles).MainValues;

        for (int b = 0; b < candles.Count; b++)
        {
            if (b < period)
            {
                Assert.Null(var[b]);
                Assert.Null(cvar[b]);
                continue;
            }

            var window = new List<decimal>();
            for (int k = b - period + 1; k <= b; k++) window.Add((candles[k].Close - candles[k - 1].Close) / candles[k - 1].Close);
            window.Sort();
            int index = Math.Clamp((int)Math.Floor((1 - confidence) * period), 0, period - 1);
            Assert.Equal(window[index], var[b]);
            Assert.Equal(window.Take(index + 1).Sum() / (index + 1), cvar[b]!.Value, 12);
        }
    }

    [Fact]
    public void ConfidenceZero_AveragesTheWholeWindow_AndAnInvalidPeriodStillFails()
    {
        var candles = Candles(60);
        var cvar = new CoreCVarIndicator { Period = 10, ConfidenceLevel = 0.0 }.Calculate(candles).MainValues;
        decimal sum = 0m;
        for (int k = 20 - 10 + 1; k <= 20; k++) sum += (candles[k].Close - candles[k - 1].Close) / candles[k - 1].Close;
        Assert.Equal(sum / 10m, cvar[20]!.Value, 12);

        IIndicatorResult failed = new CoreCVarIndicator { Period = 0 }.Calculate(candles);
        Assert.False(failed.IsSuccessful);
        Assert.Contains("Sequence contains no elements", failed.ErrorMessage);
    }

    // Before the change a Calculate of 5000 bars with Period 20 allocated about 2.1 MB (VaR) and 11.5 MB (CVaR); now about 0.24 MB each
    // (the price series, the returns and the result list). The bound leaves a wide margin and fails only if a per-bar allocation returns.
    [Fact]
    public void Calculate_AllocatesNoPerBarWindow()
    {
        var candles = Candles(5000);
        var var20 = new CoreVarIndicator { Period = 20 };
        var cvar20 = new CoreCVarIndicator { Period = 20 };
        var20.Calculate(candles);
        cvar20.Calculate(candles);

        long varBytes = AllocationProbe.InCollectionFreeWindow(() => var20.Calculate(candles));
        long cvarBytes = AllocationProbe.InCollectionFreeWindow(() => cvar20.Calculate(candles));
        if (varBytes < 0) varBytes = AllocationProbe.Minimum(() => var20.Calculate(candles));
        if (cvarBytes < 0) cvarBytes = AllocationProbe.Minimum(() => cvar20.Calculate(candles));

        const long bound = 700_000;
        Assert.True(varBytes < bound, $"VaR allocated {varBytes} bytes");
        Assert.True(cvarBytes < bound, $"CVaR allocated {cvarBytes} bytes");
    }
}
