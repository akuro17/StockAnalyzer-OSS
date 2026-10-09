using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Target weights of one decision: one per asset slot plus the cash residual. Asset weights are in [0,1] and sum with cash to exactly 1.</summary>
public sealed record RlPortfolioWeightVector(ImmutableArray<decimal> Assets, decimal Cash);

/// <summary>
/// The only place where policy output (float logits) becomes money-side numbers. The softmax runs in double on the ML side; each weight is
/// floored to <see cref="RlPortfolioContract.WeightDecimals"/> places as decimal, capped, and cash absorbs the quantization and cap excess.
/// Deterministic and non-iterative; masked or padded slots always get weight zero.
/// </summary>
public static class RlPortfolioWeights
{
    private const decimal DecimalRadix = 10m;

    /// <summary>
    /// <paramref name="logits"/> has one entry per asset slot after a leading cash logit; <paramref name="tradable"/> has one flag per asset slot.
    /// Cash is always part of the softmax. Wrong length or a non-finite logit throws <see cref="ArgumentException"/>.
    /// </summary>
    public static RlPortfolioWeightVector FromLogits(ReadOnlySpan<float> logits, ReadOnlySpan<bool> tradable, int maxWeightPercent)
    {
        if (logits.Length != tradable.Length + 1)
            throw new ArgumentException("Logits must hold one cash logit plus one logit per asset slot.", nameof(logits));
        var band = RlPortfolioLimits.MaxWeightPercent;
        if (!band.IsWithinHardLimits(maxWeightPercent))
            throw new ArgumentOutOfRangeException(nameof(maxWeightPercent), $"The weight cap must be {band.HardMin} to {band.HardMax} percent.");
        foreach (float logit in logits)
            if (!float.IsFinite(logit)) throw new ArgumentException("Logits must be finite.", nameof(logits));

        double maximum = logits[0];
        for (int i = 0; i < tradable.Length; i++)
            if (tradable[i] && logits[i + 1] > maximum) maximum = logits[i + 1];
        double sum = Math.Exp(logits[0] - maximum);
        for (int i = 0; i < tradable.Length; i++)
            if (tradable[i]) sum += Math.Exp(logits[i + 1] - maximum);

        decimal scale = Scale();
        decimal cap = maxWeightPercent / RlPercent.DecimalDivisor;
        var weights = ImmutableArray.CreateBuilder<decimal>(tradable.Length);
        decimal assets = 0m;
        for (int i = 0; i < tradable.Length; i++)
        {
            decimal weight = 0m;
            if (tradable[i])
            {
                double probability = Math.Exp(logits[i + 1] - maximum) / sum;
                weight = Math.Min((decimal)Math.Floor(probability * (double)scale) / scale, cap);
            }
            weights.Add(weight);
            assets += weight;
        }
        return new RlPortfolioWeightVector(weights.MoveToImmutable(), 1m - assets);
    }

    /// <summary>
    /// Equal weights over the tradable slots for the equal-weight baselines: each is <c>1 / n</c> floored to the contract's decimals and capped
    /// like a policy weight; cash holds the quantization and cap residual. No tradable slot means all cash.
    /// </summary>
    public static RlPortfolioWeightVector EqualWeights(ReadOnlySpan<bool> tradable, int maxWeightPercent)
    {
        var band = RlPortfolioLimits.MaxWeightPercent;
        if (!band.IsWithinHardLimits(maxWeightPercent))
            throw new ArgumentOutOfRangeException(nameof(maxWeightPercent), $"The weight cap must be {band.HardMin} to {band.HardMax} percent.");
        int count = 0;
        foreach (bool flag in tradable) if (flag) count++;
        decimal scale = Scale();
        decimal each = count == 0 ? 0m : Math.Min(Math.Floor(scale / count) / scale, maxWeightPercent / RlPercent.DecimalDivisor);
        var weights = ImmutableArray.CreateBuilder<decimal>(tradable.Length);
        for (int i = 0; i < tradable.Length; i++) weights.Add(tradable[i] ? each : 0m);
        return new RlPortfolioWeightVector(weights.MoveToImmutable(), 1m - each * count);
    }

    private static decimal Scale()
    {
        decimal scale = 1m;
        for (int d = 0; d < RlPortfolioContract.WeightDecimals; d++) scale *= DecimalRadix;
        return scale;
    }
}
