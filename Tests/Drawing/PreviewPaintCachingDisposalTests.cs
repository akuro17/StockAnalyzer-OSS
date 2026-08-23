using System;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Regression tests for the ZeroAllocation fix (SAで制約確認 follow-up): RegressionTrendObject
/// and FixedRangeVolumeProfileObject's preview-fallback Render() branches used to allocate a
/// `new SKPaint` every call (SA_RENDERING_PERFORMANCE.md "Prohibit new SKPaint... inside
/// Render()"). Both now reuse a class-level cached SKPaint field instead, so both classes
/// gained an IDisposable implementation to release it. These tests only guard the disposal
/// contract itself (safe to call, idempotent enough not to throw); the actual rendered pixel
/// output is unchanged and already covered by the existing RegressionTrendObjectTests /
/// FixedRangeVolumeProfileHandleDragTests suites, which all continued to pass unmodified
/// after this change.
/// </summary>
public class PreviewPaintCachingDisposalTests
{
    [Fact]
    public void RegressionTrendObject_Dispose_DoesNotThrow()
    {
        var obj = new RegressionTrendObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 5), 120m));

        var ex = Record.Exception(() => obj.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public void FixedRangeVolumeProfileObject_Dispose_DoesNotThrow()
    {
        var obj = new FixedRangeVolumeProfileObject(
            new ChartPoint(new DateTime(2025, 1, 1), 100m),
            new ChartPoint(new DateTime(2025, 1, 5), 120m));

        var ex = Record.Exception(() => obj.Dispose());

        Assert.Null(ex);
    }
}
