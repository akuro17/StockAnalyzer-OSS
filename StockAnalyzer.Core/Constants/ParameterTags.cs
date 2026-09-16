namespace StockAnalyzer.Core.Constants;

/// <summary>
/// Centralized vocabulary of ParameterTagAttribute values.
/// Single Source of Truth for tag literals shared between parameter classes
/// (which declare the tag) and UI-building code (which filters on it).
/// </summary>
public static class ParameterTags
{
    /// <summary>
    /// Applied to a parameter property whose value is superseded by a Dynamic Period Driver
    /// when one is selected on the owning indicator (e.g. Period).
    /// </summary>
    public const string DynamicPeriodSensitive = "DynamicPeriodSensitive";
}
