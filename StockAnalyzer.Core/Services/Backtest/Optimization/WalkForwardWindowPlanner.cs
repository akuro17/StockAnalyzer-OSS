namespace StockAnalyzer.Core.Services.Backtest.Optimization;

/// <summary>How the in-sample part of the windows is placed: its length stays fixed and it slides, or its start stays at the first bar and it grows.</summary>
public enum WalkForwardMode
{
    Rolling,
    Anchored,
}

/// <summary>One window as inclusive bar indices into the planned series; the out-of-sample part follows the in-sample part directly.</summary>
public readonly record struct WalkForwardWindow(int Index, int InSampleStart, int InSampleEnd, int OutOfSampleStart, int OutOfSampleEnd);

/// <summary>
/// Plans walk-forward windows over a series of bars (pure function, no I/O). Lengths are in bars of the series, never in calendar time.
/// Window k (from 0) is, for <see cref="WalkForwardMode.Rolling"/>, in-sample [k*step, k*step + inSample - 1]; for <see cref="WalkForwardMode.Anchored"/>,
/// in-sample [0, inSample - 1 + k*step]; in both cases the out-of-sample part is the next <c>outOfSample</c> bars. A window whose out-of-sample part would
/// run past the last bar is not made, so every window has a full-length out-of-sample part and the out-of-sample part is always after the in-sample part.
/// </summary>
public static class WalkForwardWindowPlanner
{
    /// <exception cref="ArgumentOutOfRangeException">A length or the step is below 1, or <paramref name="barCount"/> is negative.</exception>
    public static IReadOnlyList<WalkForwardWindow> Plan(int barCount, WalkForwardMode mode, int inSampleBars, int outOfSampleBars, int stepBars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(barCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(inSampleBars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outOfSampleBars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepBars, 1);

        var windows = new List<WalkForwardWindow>();
        for (long k = 0; ; k++)
        {
            long inSampleStart = mode == WalkForwardMode.Rolling ? k * stepBars : 0;
            long inSampleEnd = (mode == WalkForwardMode.Rolling ? inSampleStart + inSampleBars : inSampleBars + (k * stepBars)) - 1;
            long outOfSampleStart = inSampleEnd + 1;
            long outOfSampleEnd = outOfSampleStart + outOfSampleBars - 1;
            if (outOfSampleEnd > barCount - 1L)
            {
                return windows;
            }

            windows.Add(new WalkForwardWindow((int)k, (int)inSampleStart, (int)inSampleEnd, (int)outOfSampleStart, (int)outOfSampleEnd));
        }
    }
}
