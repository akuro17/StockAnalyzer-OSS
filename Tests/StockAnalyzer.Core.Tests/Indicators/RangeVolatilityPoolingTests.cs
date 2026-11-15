using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// WebAiPeerReview plan T9 (owner decision E-8 = a): Parkinson and Rogers-Satchell borrow their work arrays from ArrayPool and fill the result list directly.
/// The numbers must not change; the managed allocation of one calculation must drop by the work arrays and the intermediate list.
/// </summary>
public sealed class RangeVolatilityPoolingTests
{
    private const int Bars = 5000;

    // Measured before the change (2026-11-16, 5000 bars, after warm-up): 165,720 bytes for each indicator
    // (double[5000] 40,000 + bool[5000] 5,000 + the intermediate List of 5000 nullable decimals 120,000 + small objects).
    private const long BytesBeforePooling = 165_720;
    private const long RequiredReduction = 150_000;

    private readonly ITestOutputHelper _output;

    public RangeVolatilityPoolingTests(ITestOutputHelper output) => _output = output;

    private static List<CoreCandleData> Candles(int count, int invalidEvery = 0)
    {
        var random = new Random(12345);
        var candles = new List<CoreCandleData>(count);
        decimal price = 100m;
        for (int i = 0; i < count; i++)
        {
            decimal open = price;
            decimal close = Math.Max(1m, open + (decimal)((random.NextDouble() - 0.5) * 4.0));
            decimal high = Math.Max(open, close) + (decimal)(random.NextDouble() * 2.0);
            decimal low = Math.Max(0.5m, Math.Min(open, close) - (decimal)(random.NextDouble() * 0.5));
            if (invalidEvery > 0 && i % invalidEvery == invalidEvery - 1)
            {
                low = 0m;   // an unusable bar: H/L and the Rogers-Satchell logs are invalid
            }

            candles.Add(new CoreCandleData(new DateTime(2000, 1, 1).AddDays(i), open, high, low, close, 1000));
            price = close;
        }

        return candles;
    }

    /// <summary>The previous implementation of both indicators, written with fresh arrays, as the reference.</summary>
    private static List<decimal?> Reference(IReadOnlyList<CoreCandleData> candles, bool rogersSatchell, int period, bool annualize, int periodsPerYear)
    {
        int count = candles.Count;
        var contribution = new double[count];
        var valid = new bool[count];
        for (int k = 0; k < count; k++)
        {
            CoreCandleData c = candles[k];
            double v;
            if (rogersSatchell)
            {
                if (c.Open <= 0m || c.High <= 0m || c.Low <= 0m || c.Close <= 0m) continue;
                double open = (double)c.Open, high = (double)c.High, low = (double)c.Low, close = (double)c.Close;
                v = Math.Log(high / close) * Math.Log(high / open) + Math.Log(low / close) * Math.Log(low / open);
            }
            else
            {
                if (c.High <= 0m || c.Low <= 0m) continue;
                double logRange = Math.Log((double)(c.High / c.Low));
                v = logRange * logRange / (4.0 * Math.Log(2.0));
            }

            if (double.IsFinite(v))
            {
                contribution[k] = v;
                valid[k] = true;
            }
        }

        return RangeVolatilityMath.Rolling(contribution, valid, period, annualize, periodsPerYear);
    }

    [Theory]
    [InlineData(false, false, 20, 0)]
    [InlineData(false, true, 20, 0)]
    [InlineData(true, false, 20, 0)]
    [InlineData(false, false, 20, 7)]
    [InlineData(true, true, 5, 7)]
    [InlineData(false, true, 1, 3)]
    public void Indicators_GiveTheSameSeriesAsTheFreshArrayReference(bool rogersSatchell, bool annualize, int period, int invalidEvery)
    {
        var candles = Candles(600, invalidEvery);
        IReadOnlyList<decimal?> actual = rogersSatchell
            ? new CoreRogersSatchellVolatilityIndicator { Period = period, Annualize = annualize, PeriodsPerYear = 52 }.Calculate(candles).MainValues
            : new CoreParkinsonVolatilityIndicator { Period = period, Annualize = annualize, PeriodsPerYear = 52 }.Calculate(candles).MainValues;

        Assert.Equal(Reference(candles, rogersSatchell, period, annualize, 52), actual);
        if (invalidEvery > 0) Assert.Contains(actual, v => v is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirtyPool_AfterALongAllValidRun_DoesNotLeakIntoAShorterRunWithInvalidBars(bool rogersSatchell)
    {
        // The pool hands back arrays that still hold the previous run: all valid = true and non-zero contributions.
        // A run with unusable bars must still give null windows exactly where the reference does.
        var all = Candles(900);
        var withHoles = Candles(300, invalidEvery: 4);

        for (int round = 0; round < 3; round++)
        {
            IReadOnlyList<decimal?> first = rogersSatchell
                ? new CoreRogersSatchellVolatilityIndicator().Calculate(all).MainValues
                : new CoreParkinsonVolatilityIndicator().Calculate(all).MainValues;
            IReadOnlyList<decimal?> second = rogersSatchell
                ? new CoreRogersSatchellVolatilityIndicator().Calculate(withHoles).MainValues
                : new CoreParkinsonVolatilityIndicator().Calculate(withHoles).MainValues;

            Assert.Equal(Reference(all, rogersSatchell, 20, false, 252), first);
            Assert.Equal(Reference(withHoles, rogersSatchell, 20, false, 252), second);
        }
    }

    [Fact]
    public void SpanOverload_WithLongerBackingArrays_GivesTheSameSeries_AndClearsTheOutput()
    {
        var v = new double[] { 0.0004, 0.0009, 0.0001, 0.0016, 0.0025 };
        var valid = new[] { true, true, true, false, true };
        var vBig = new double[64];
        var validBig = new bool[64];
        Array.Fill(vBig, 123.0);
        Array.Fill(validBig, true);
        v.CopyTo(vBig, 0);
        valid.CopyTo(validBig, 0);

        var output = new List<decimal?> { 1m, 2m, 3m };   // stale content must be dropped
        RangeVolatilityMath.Rolling(vBig.AsSpan(0, 5), validBig.AsSpan(0, 5), 2, true, 52, output);

        Assert.Equal(RangeVolatilityMath.Rolling(v, valid, 2, true, 52), output);
        Assert.Equal(5, output.Count);
    }

    [Fact]
    public void SpanOverload_ValidatesItsArguments()
    {
        var v = new double[3];
        var valid = new bool[3];

        Assert.Throws<ArgumentNullException>(() => RangeVolatilityMath.Rolling(v, valid, 2, false, 252, null!));
        Assert.Throws<ArgumentException>(() => RangeVolatilityMath.Rolling(v, new bool[2], 2, false, 252, new List<decimal?>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RangeVolatilityMath.Rolling(v, valid, 0, false, 252, new List<decimal?>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RangeVolatilityMath.Rolling(v, valid, 2, true, 0, new List<decimal?>()));
        RangeVolatilityMath.Rolling(v, valid, 2, false, 0, new List<decimal?>());   // periodsPerYear is not read while annualize is off
    }

    [Fact]
    public void EmptyInput_GivesAnEmptySeries_AndRentingZeroBarsIsHarmless()
    {
        var output = new List<decimal?> { 1m };
        RangeVolatilityMath.Rolling(ReadOnlySpan<double>.Empty, ReadOnlySpan<bool>.Empty, 3, false, 252, output);

        Assert.Empty(output);
    }

    [Fact]
    public void OneCalculationOf5000Bars_AllocatesAtLeast150KilobytesLessThanBefore()
    {
        var candles = Candles(Bars);
        var parkinson = new CoreParkinsonVolatilityIndicator();
        var rogers = new CoreRogersSatchellVolatilityIndicator();
        parkinson.Calculate(candles);   // warm-up
        rogers.Calculate(candles);

        long parkinsonBytes = AllocationProbe.Minimum(() => parkinson.Calculate(candles));
        long rogersBytes = AllocationProbe.Minimum(() => rogers.Calculate(candles));

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Bars} bars: Parkinson {parkinsonBytes} bytes, Rogers-Satchell {rogersBytes} bytes (before: {BytesBeforePooling})"));
        Assert.True(parkinsonBytes <= BytesBeforePooling - RequiredReduction, $"Parkinson allocated {parkinsonBytes} bytes");
        Assert.True(rogersBytes <= BytesBeforePooling - RequiredReduction, $"Rogers-Satchell allocated {rogersBytes} bytes");
    }
}
