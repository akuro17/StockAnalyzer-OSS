using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>
/// Helpers for headless Avalonia tests that drive real pointer input. Avalonia's own
/// <c>HeadlessWindowExtensions.MouseDown</c> / <c>MouseUp</c> give up ("Dispatcher job loop detected")
/// when more than 10 run-jobs/render rounds are still pending afterwards. A pointer press makes the
/// control run wall-clock-bound visual transitions (measured: 10-32 ms of real time, re-posting a job
/// every round), so on a warm process - where a round takes microseconds - that limit is exhausted
/// before the time elapses and the test fails intermittently (never when run alone, where JIT makes
/// rounds slow). These helpers deliver the same raw pointer event but wait for the dispatcher to go
/// idle by wall-clock time instead of by round count.
/// </summary>
public static class HeadlessDispatcher
{
    private const string HeadlessWindowInterfaceName = "Avalonia.Headless.IHeadlessWindow";

    /// <summary>Environment variable that overrides the settle timeout, in seconds (for slow CI hosts).</summary>
    public const string SettleTimeoutEnvironmentVariable = "SA_TEST_HEADLESS_SETTLE_TIMEOUT_SECONDS";

    /// <summary>Timeout used when <see cref="SettleTimeoutEnvironmentVariable"/> is unset or invalid.</summary>
    private const double FallbackSettleTimeoutSeconds = 2;

    /// <summary>Upper bound on wall-clock time <see cref="Settle"/> waits for pending work to drain.</summary>
    public static readonly TimeSpan DefaultSettleTimeout =
        ParseSettleTimeout(Environment.GetEnvironmentVariable(SettleTimeoutEnvironmentVariable));

    /// <summary>Parses a seconds value; null, non-numeric, non-finite or non-positive input yields the fallback.</summary>
    public static TimeSpan ParseSettleTimeout(string? seconds) =>
        double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value > 0
            ? TimeSpan.FromSeconds(value)
            : TimeSpan.FromSeconds(FallbackSettleTimeoutSeconds);

    /// <summary>
    /// Runs dispatcher jobs and render ticks until no active-priority job is pending, or
    /// <paramref name="timeout"/> elapses. Call after <c>Window.Show()</c> and before the first
    /// simulated input so the input helper starts from an idle dispatcher.
    /// </summary>
    /// <returns><c>true</c> if the dispatcher became idle within the timeout.</returns>
    public static bool Settle(TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? DefaultSettleTimeout;
        while (Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.Background))
        {
            if (deadline.Elapsed > limit)
            {
                return false;
            }

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        return true;
    }

    /// <summary>
    /// Same as <c>HeadlessWindowExtensions.MouseDown</c>, but settles by time rather than by a
    /// 10-round limit. Uses the headless platform window's own pointer entry point, so the real
    /// gesture-recognition pipeline (taps, double-taps) is exercised.
    /// </summary>
    public static void MouseDownSettled(this TopLevel topLevel, Point point, MouseButton button) =>
        SendSettled(topLevel, "MouseDown", point, button);

    /// <summary>Same as <c>HeadlessWindowExtensions.MouseUp</c>; see <see cref="MouseDownSettled"/>.</summary>
    public static void MouseUpSettled(this TopLevel topLevel, Point point, MouseButton button) =>
        SendSettled(topLevel, "MouseUp", point, button);

    private static void SendSettled(TopLevel topLevel, string methodName, Point point, MouseButton button)
    {
        // The headless window interface (IHeadlessWindow) is internal to Avalonia.Headless and its
        // implementation declares the members explicitly, so they are reached through the interface
        // type by reflection.
        var impl = topLevel.PlatformImpl
            ?? throw new InvalidOperationException("The top level has no platform implementation.");
        var headlessWindowType = typeof(HeadlessWindowExtensions).Assembly.GetType(HeadlessWindowInterfaceName)
            ?? throw new InvalidOperationException($"{HeadlessWindowInterfaceName} was not found in Avalonia.Headless.");
        var method = headlessWindowType.GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.Public, new[] { typeof(Point), typeof(MouseButton), typeof(RawInputModifiers) })
            ?? throw new InvalidOperationException($"{HeadlessWindowInterfaceName} does not expose {methodName}(Point, MouseButton, RawInputModifiers).");

        RequireSettled();
        method.Invoke(impl, new object[] { point, button, RawInputModifiers.None });
        RequireSettled();
    }

    private static void RequireSettled()
    {
        if (!Settle())
        {
            throw new InvalidOperationException(
                $"The dispatcher did not become idle within {DefaultSettleTimeout.TotalSeconds:0.#} s.");
        }
    }
}
