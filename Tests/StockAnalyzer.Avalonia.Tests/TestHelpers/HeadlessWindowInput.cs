using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>Helpers for headless tests that send pointer events to a window they have just shown.</summary>
public static class HeadlessWindowInput
{
    // Far above the few milliseconds the compositor normally needs; it only bounds a hang.
    private static readonly TimeSpan HittableCeiling = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The compositor publishes the visual tree of a shown window asynchronously: right after Show and one render tick the window can still answer a hit
    /// test with null, and a pointer event sent then reaches no control. The first pointer event must wait until the point can be hit.
    /// </summary>
    public static void WaitUntilHittable(Window window, Point point)
    {
        SpinWait.SpinUntil(() =>
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            return window.InputHitTest(point) != null;
        }, HittableCeiling);

        Assert.NotNull(window.InputHitTest(point));
    }
}
