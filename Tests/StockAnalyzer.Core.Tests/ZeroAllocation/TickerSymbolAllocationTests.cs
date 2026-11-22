using System;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.ZeroAllocation;

/// <summary>
/// <see cref="TickerSymbol.TryCanonicalizeAscii"/> serves the bulk import's allocation-free path, so it must not allocate on the accepting path and also not on any refusing path
/// (a file full of long text or non-ASCII descriptions must not cost memory per token). Measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> after a warm-up.
/// The measured loop contains no assertion (the assertion library itself allocates); each action returns the written length (or -1 for a refusal) and the totals are asserted afterwards.
/// </summary>
public class TickerSymbolAllocationTests
{
    private const int Iterations = 2000;

    // Non-ASCII text from code units (no non-ASCII character in this source file).
    private static readonly string NonAscii = "AB" + (char)0x0130 + "CD";

    private static (long Allocated, long Total) Measure(Func<int> action)
    {
        long total = 0;
        total += action(); // warm-up: JIT and static initialisation
        total += action();
        total = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++) total += action();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (allocated, total);
    }

    [Fact]
    public void TheAcceptingPath_AllocatesNothing()
    {
        var (allocated, total) = Measure(() =>
        {
            Span<char> buffer = stackalloc char[32];
            return TickerSymbol.TryCanonicalizeAscii("brk.b", buffer, out int written) ? written : -1;
        });

        Assert.Equal(0, allocated);
        Assert.Equal(5L * Iterations, total);
    }

    [Fact]
    public void TheNonAsciiRefusal_AllocatesNothing()
    {
        var (allocated, total) = Measure(() =>
        {
            Span<char> buffer = stackalloc char[32];
            return TickerSymbol.TryCanonicalizeAscii(NonAscii, buffer, out int written) ? written : -1;
        });

        Assert.Equal(0, allocated);
        Assert.Equal(-1L * Iterations, total);
    }

    [Fact]
    public void TheShortBufferRefusal_AllocatesNothing()
    {
        var (allocated, total) = Measure(() =>
        {
            Span<char> buffer = stackalloc char[3];
            return TickerSymbol.TryCanonicalizeAscii("brk.b", buffer, out int written) ? written : -1;
        });

        Assert.Equal(0, allocated);
        Assert.Equal(-1L * Iterations, total);
    }

    [Fact]
    public void TheEmptyRefusal_AllocatesNothing()
    {
        var (allocated, total) = Measure(() =>
        {
            Span<char> buffer = stackalloc char[8];
            return TickerSymbol.TryCanonicalizeAscii(ReadOnlySpan<char>.Empty, buffer, out int written) ? written : -1;
        });

        Assert.Equal(0, allocated);
        Assert.Equal(-1L * Iterations, total);
    }

    [Fact]
    public void TheInPlacePath_AllocatesNothing()
    {
        char[] chars = "brk.b".ToCharArray();

        var (allocated, total) = Measure(() => TickerSymbol.TryCanonicalizeAscii(chars, chars, out int written) ? written : -1);

        Assert.Equal(0, allocated);
        Assert.Equal(5L * Iterations, total);
    }
}
