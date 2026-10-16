using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The startup validation phase of the Screener settings: operational values are compared with the system contracts
/// <see cref="ScreenerConditionOffsetLimits"/> and <see cref="ScreenerFilterTreeLimits"/>. A hard-limit violation is fatal (an exception that names the
/// key stops the startup); a value between the standard and the hard band runs and only produces a warning for the operator.
/// </summary>
public static class ScreenerSettingsValidator
{
    public const string MaxConditionOffsetKey = "Screener:MaxConditionOffset";
    public const string MaxFilterTreeNodesKey = "Screener:MaxFilterTreeNodes";
    public const string MaxFilterTreeDepthKey = "Screener:MaxFilterTreeDepth";

    /// <summary>
    /// Throws when <paramref name="maxConditionOffset"/> is outside the hard band. Returns the warnings of values that run but leave the standard band
    /// (empty when every value is standard); the caller with a logger reports them.
    /// </summary>
    public static IReadOnlyList<string> Validate(int maxConditionOffset)
    {
        var warnings = new List<string>();
        CheckBand(MaxConditionOffsetKey, nameof(maxConditionOffset), ScreenerConditionOffsetLimits.MaxConditionOffset, maxConditionOffset, "bars",
            "every symbol loads that many more bars per scan", warnings);
        return warnings;
    }

    /// <summary>
    /// Throws when a tree bound is outside its hard band, or when the depth exceeds the node count (a tree cannot be deeper than it has nodes, so the
    /// depth bound would be unreachable). Returns the warnings of values that run but leave the standard band.
    /// </summary>
    public static IReadOnlyList<string> ValidateFilterTree(int maxFilterTreeNodes, int maxFilterTreeDepth)
    {
        var warnings = new List<string>();
        CheckBand(MaxFilterTreeNodesKey, nameof(maxFilterTreeNodes), ScreenerFilterTreeLimits.MaxFilterTreeNodes, maxFilterTreeNodes, "nodes",
            "a larger tree is slower to edit and to evaluate", warnings);
        CheckBand(MaxFilterTreeDepthKey, nameof(maxFilterTreeDepth), ScreenerFilterTreeLimits.MaxFilterTreeDepth, maxFilterTreeDepth, "levels",
            "a deeper tree is harder to read and to edit", warnings);

        if (maxFilterTreeDepth > maxFilterTreeNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFilterTreeDepth), maxFilterTreeDepth,
                $"{MaxFilterTreeDepthKey} ({Format(maxFilterTreeDepth)}) must not exceed {MaxFilterTreeNodesKey} ({Format(maxFilterTreeNodes)}): a tree cannot be deeper than it has nodes.");
        }
        return warnings;
    }

    private static void CheckBand(string key, string parameterName, SettingBand band, int value, string unit, string consequence, List<string> warnings)
    {
        if (!band.IsWithinHardLimits(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{key} must be {Format(band.HardMin)} to {Format(band.HardMax)} {unit}; the value {Format(value)} is outside the system limits.");
        }

        if (!band.IsWithinStandard(value))
        {
            warnings.Add($"{key} = {Format(value)} {unit} is outside the standard range {Format(band.StandardMin)} to {Format(band.StandardMax)}; "
                + $"the application runs but {consequence}.");
        }
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
