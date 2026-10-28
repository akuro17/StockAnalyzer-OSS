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

    /// <summary>Attempts <see cref="InCollectionFreeWindow"/> makes before it concludes that no sample was free of a collection.</summary>
    public const int MaxAttempts = 500;

    /// <summary>
    /// Measures like <see cref="Minimum"/>, but accepts only a sample during which the collection count did not change. A collection that another
    /// test runs on another thread adds bytes (a multiple of 1056 seen) to this thread's counter although the code under test allocated nothing, and
    /// beside allocation-heavy tests it can touch every one of a few samples. The count moves when a collection starts, so one that was already
    /// running when the sample began and ended inside it is not seen (measured once in about ten Screener runs); the result is therefore the smallest
    /// of up to <see cref="Samples"/> accepted samples, stopping at the first clean one. Returns -1 when every one of <see cref="MaxAttempts"/> samples
    /// overlapped a collection (the measurement is then inconclusive, not zero).
    /// </summary>
    /// <param name="body">The measured work; keep it short so that a collection-free sample is likely; it must not allocate its own closure or delegate while it runs.</param>
    /// <param name="prepare">Runs before each sample, outside the measurement.</param>
    public static long InCollectionFreeWindow(Action body, Action? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        long minimum = -1;
        int accepted = 0;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            prepare?.Invoke();
            int collectionsBefore = GC.CollectionCount(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (GC.CollectionCount(0) != collectionsBefore) continue;

            minimum = minimum < 0 ? allocated : Math.Min(minimum, allocated);
            if (minimum == 0 || ++accepted >= Samples) return minimum;
        }
        return minimum;
    }
}
