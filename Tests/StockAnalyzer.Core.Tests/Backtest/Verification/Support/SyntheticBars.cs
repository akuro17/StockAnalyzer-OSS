using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.Backtest.Verification;

/// <summary>
/// Deterministic OHLCV builders. Every bar is a valid <see cref="CandleData"/> (BacktestInput rejects anything else):
/// UTC strictly increasing daily timestamps, Low &lt;= min(O,C) &lt;= max(O,C) &lt;= High, all prices &gt; 0.
/// All arithmetic is decimal; the only randomness is a seeded <see cref="Random"/> feeding integer "cent" steps.
/// </summary>
internal static class SyntheticBars
{
    public static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    public const long DefaultVolume = 1000;

    private const decimal PriceFloor = 5m;
    private const decimal CentsPerUnit = 100m;
    private const int MaxCloseStepCents = 300;
    private const int MaxWickCents = 200;
    private const int MaxGapCents = 100;

    /// <summary>Poison factors: valid but absurd (x1000 / x0.001) so OHLC ordering and positivity are preserved.</summary>
    private const decimal PoisonUpFactor = 1000m;
    private const decimal PoisonDownFactor = 0.001m;

    public static CandleData Bar(int dayOffset, decimal open, decimal high, decimal low, decimal close)
        => new(Start.AddDays(dayOffset), open, high, low, close, DefaultVolume);

    public static ImmutableArray<CandleData> Flat(int count, decimal price)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(count);
        for (int i = 0; i < count; i++) builder.Add(Bar(i, price, price, price, price));
        return builder.MoveToImmutable();
    }

    public static ImmutableArray<CandleData> FromTable(params (decimal O, decimal H, decimal L, decimal C)[] rows)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(rows.Length);
        for (int i = 0; i < rows.Length; i++) builder.Add(Bar(i, rows[i].O, rows[i].H, rows[i].L, rows[i].C));
        return builder.MoveToImmutable();
    }

    /// <summary>Flat bars (O=H=L=C) following the given close series.</summary>
    public static ImmutableArray<CandleData> FlatSeries(params decimal[] prices)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(prices.Length);
        for (int i = 0; i < prices.Length; i++) builder.Add(Bar(i, prices[i], prices[i], prices[i], prices[i]));
        return builder.MoveToImmutable();
    }

    /// <summary>Flat bars whose price rises by <paramref name="step"/> every bar (O=H=L=C = start + i*step).</summary>
    public static ImmutableArray<CandleData> Ramp(int count, decimal start, decimal step)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal p = start + (i * step);
            builder.Add(Bar(i, p, p, p, p));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>Seeded, symmetric (driftless apart from the price floor) random walk with small gaps between bars.</summary>
    public static ImmutableArray<CandleData> SeededRandomWalk(int seed, int count, decimal startPrice)
    {
        var rng = new Random(seed);
        var builder = ImmutableArray.CreateBuilder<CandleData>(count);
        decimal open = startPrice;
        for (int i = 0; i < count; i++)
        {
            decimal close = Math.Max(PriceFloor, open + (rng.Next(-MaxCloseStepCents, MaxCloseStepCents + 1) / CentsPerUnit));
            decimal high = Math.Max(open, close) + (rng.Next(0, MaxWickCents + 1) / CentsPerUnit);
            decimal low = Math.Min(open, close) - (rng.Next(0, MaxWickCents + 1) / CentsPerUnit);
            builder.Add(Bar(i, open, high, low, close));
            open = Math.Max(PriceFloor, close + (rng.Next(-MaxGapCents, MaxGapCents + 1) / CentsPerUnit));
        }
        return builder.MoveToImmutable();
    }

    public static ImmutableArray<CandleData> Scale(ImmutableArray<CandleData> bars, decimal factor)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(bars.Length);
        foreach (CandleData b in bars)
        {
            builder.Add(new CandleData(b.Timestamp, b.Open * factor, b.High * factor, b.Low * factor, b.Close * factor, b.Volume));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>Additive mirror p' = 2*pivot - p with High/Low swapped. Only valid while every mirrored price stays &gt; 0.</summary>
    public static ImmutableArray<CandleData> Mirror(ImmutableArray<CandleData> bars, decimal pivot)
    {
        decimal twice = 2m * pivot;
        var builder = ImmutableArray.CreateBuilder<CandleData>(bars.Length);
        foreach (CandleData b in bars)
        {
            builder.Add(new CandleData(b.Timestamp, twice - b.Open, twice - b.Low, twice - b.High, twice - b.Close, b.Volume));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>Replaces every bar with index &gt;= <paramref name="startIndex"/> by an absurd-but-valid bar (x1000 on even indices, x0.001 on odd).</summary>
    public static ImmutableArray<CandleData> Poison(ImmutableArray<CandleData> bars, int startIndex)
        => Poison(bars, startIndex, PoisonUpFactor, PoisonDownFactor);

    /// <summary>Same as the two-argument overload with caller-chosen factors (a mild pair avoids decimal overflow inside heavy indicators).</summary>
    public static ImmutableArray<CandleData> Poison(ImmutableArray<CandleData> bars, int startIndex, decimal upFactor, decimal downFactor)
    {
        var builder = ImmutableArray.CreateBuilder<CandleData>(bars.Length);
        for (int i = 0; i < bars.Length; i++)
        {
            CandleData b = bars[i];
            if (i < startIndex)
            {
                builder.Add(b);
                continue;
            }
            decimal f = i % 2 == 0 ? upFactor : downFactor;
            builder.Add(new CandleData(b.Timestamp, b.Open * f, b.High * f, b.Low * f, b.Close * f, b.Volume));
        }
        return builder.MoveToImmutable();
    }
}
