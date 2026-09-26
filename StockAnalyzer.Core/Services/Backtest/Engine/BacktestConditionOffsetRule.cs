namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Single source of truth for the backtest condition Offset bounds. Offset means "bars BEFORE the current bar" (a lag): a negative value would read
/// <c>series[barIndex + n]</c>, a future bar.
/// Lower bound: <see cref="MinOffset"/>, a causality invariant (a constant), consumed by <c>ConditionBasedBacktestStrategy</c> (construction), <c>BacktestConfigurationManager</c>
/// (saved configuration) and the Offset input boxes (through the ViewModel).
/// Upper bound: a usability limit that comes from configuration (<c>BacktestSettings.MaxConditionOffset</c>), consumed by the saved-configuration validation and the input boxes;
/// the engine does not need it (an Offset beyond the data length just yields null values).
/// </summary>
public static class BacktestConditionOffsetRule
{
    public const int MinOffset = 0;

    public static bool IsValid(int offset) => offset >= MinOffset;

    /// <summary>True when <paramref name="offset"/> does not exceed the configured limit.</summary>
    public static bool IsWithinLimit(int offset, int maxOffset) => offset <= maxOffset;

    /// <summary>English diagnostic for an Offset above the configured limit.</summary>
    public static string DescribeAboveLimit(int entryIndex, string sideName, int offset, int maxOffset)
        => $"ConditionEntries[{entryIndex}].{sideName}.Offset must be <= {maxOffset} (the configured Backtest:MaxConditionOffset), but was {offset}.";

    /// <summary>English diagnostic naming the offending condition side (shared by the exception and the configuration-load error).</summary>
    public static string Describe(int entryIndex, string sideName, int offset)
        => $"ConditionEntries[{entryIndex}].{sideName}.Offset must be >= {MinOffset} (bars before the current bar), but was {offset}; a negative Offset would reference a future bar.";
}
