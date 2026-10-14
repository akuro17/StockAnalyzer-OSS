using System;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// System contract of the stored-period proposal (numeric classification, see SA_ARCHITECTURE_RULES): code, never configuration. The cap is the number of
/// symbols whose stored dates one proposal reads one file after another over one shared connection; above it nothing is read and no period is proposed.
/// It bounds the work, not the responsiveness: the proposal runs in the background and any input change cancels it. The time depends on the disk and the CPU
/// of the machine, so the cap is a bound on the work and not a promise of a time.
/// </summary>
public static class StoredPeriodLimits
{
    /// <summary>
    /// The cap in force. Owner decision 2026-10-14: every symbol of a large ticker table must fit, so 10,000 (the table holds 7,086 symbols). Measured with the
    /// owner's real Data/Daily files copied under distinct names (median of three, warm cache): one connection per file about 8 to 9 ms per file (7,086 files
    /// 63.6 s); one shared connection about 1.3 ms per file (200 files 0.27 s, 1,000 files 1.48 s, 3,000 files 4.50 s, 7,086 files 9.24 s). A symbol without a
    /// stored file costs a file-name probe only, so the time follows the files that exist.
    /// </summary>
    public const int MaximumSymbols = 10000;

    /// <summary>Hard band [1, 20000]; the standard value is <see cref="MaximumSymbols"/>.</summary>
    public static readonly SettingBand MaximumSymbolsBand = new(1, 20000, MaximumSymbols, MaximumSymbols);

    /// <summary>Startup validation: a value outside the hard band throws.</summary>
    public static int Validate(int maximumSymbols)
    {
        if (!MaximumSymbolsBand.IsWithinHardLimits(maximumSymbols))
            throw new ArgumentOutOfRangeException(nameof(maximumSymbols), maximumSymbols,
                $"The stored-period symbol cap must lie in [{MaximumSymbolsBand.HardMin}, {MaximumSymbolsBand.HardMax}].");
        return maximumSymbols;
    }
}

/// <summary>Timing token of the date pre-fill (named, not a literal at the call sites).</summary>
public static class StoredPeriodTiming
{
    /// <summary>Quiet time after the last relevant input change before the stored dates are read again.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
}
