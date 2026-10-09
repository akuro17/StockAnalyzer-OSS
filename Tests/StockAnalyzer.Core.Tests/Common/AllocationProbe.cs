using System;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// Measures managed allocation on the calling thread for "allocates nothing" probes. The counter of
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> also moves when the runtime itself allocates on this thread (a tier-up, a stub, an
/// allocation-context refill, which shows as roughly 8 KiB), so one sample can be non-zero although the code under test never allocates.
/// An allocation made by the code under test appears in every sample; runtime noise does not. The probe therefore takes the smallest of a
/// few samples, and stops at the first clean one.
/// </summary>
internal static class AllocationProbe
{
    /// <summary>Samples taken before a probe concludes that the code allocates (noise would have to hit every one).</summary>
    public const int Samples = 5;

    /// <param name="body">The measured work; it must not allocate its own closure or delegate while it runs.</param>
    /// <param name="prepare">Runs before each sample, outside the measurement (for example a forced collection).</param>
    public static long Minimum(Action body, Action? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        long minimum = long.MaxValue;
        for (int sample = 0; sample < Samples && minimum != 0; sample++)
        {
            prepare?.Invoke();
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return minimum;
    }
}
