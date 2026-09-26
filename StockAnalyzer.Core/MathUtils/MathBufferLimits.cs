namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Shared buffer-size limit for the zero-allocation math engines. Static math code has no access to
/// configuration, so this is a deliberate compiled constant; new engines must reference it instead of
/// declaring their own copy.
/// </summary>
public static class MathBufferLimits
{
    /// <summary>Largest element count of a <c>double</c> scratch buffer taken with <c>stackalloc</c> (4 KiB); larger buffers come from <see cref="System.Buffers.ArrayPool{T}"/>.</summary>
    public const int StackAllocThreshold = 512;
}
