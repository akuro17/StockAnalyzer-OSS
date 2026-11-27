using System;

namespace StockAnalyzer.Core.Optimization;

/// <summary>
/// Resolves effective CPU parallelism from configuration and host core count while keeping hardware headroom reserved for UI rendering.
/// </summary>
public static class HardwareConcurrencyPolicy
{
    public const int DefaultReservedCores = 2;
    public const int DefaultScreenerAutoMaxParallelism = Models.Settings.InfrastructureSettings.DefaultMaxAutoScreenerParallelism;

    /// <summary>
    /// Resolves effective parallelism bounded by [<paramref name="min"/>, <paramref name="max"/>].
    /// When <paramref name="autoScale"/> is <see langword="false"/>, preserves <paramref name="configured"/> clamped to bounds.
    /// When <paramref name="autoScale"/> is <see langword="true"/>, clamps <c>cores - reservedCores</c> to [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    public static int ResolveEffectiveParallelism(
        int configured,
        bool autoScale,
        int min,
        int max,
        int reservedCores = DefaultReservedCores,
        int? processorCountOverride = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(min, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);
        ArgumentOutOfRangeException.ThrowIfLessThan(reservedCores, 0);

        if (!autoScale)
        {
            return Math.Clamp(configured, min, max);
        }

        int cores = Math.Max(1, processorCountOverride ?? Environment.ProcessorCount);
        return Math.Clamp(cores - reservedCores, min, max);
    }
}
