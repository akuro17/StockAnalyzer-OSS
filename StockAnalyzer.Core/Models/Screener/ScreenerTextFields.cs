using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Models.Screener;

/// <summary>
/// The text fields a Screener condition can compare against a typed text (<see cref="RightHandTargetMode.StringValue"/>), where each one is read from
/// and how a comparison is decided. Single definition for the Core evaluation and the registration dialog's text-column list.
/// </summary>
public static class ScreenerTextFields
{
    public const string Sector = "Sector";
    public const string Industry = "Industry";
    public const string Region = "Region";
    /// <summary>The company's full name (<see cref="TickerMetadata.LongName"/>).</summary>
    public const string Name = "Name";
    public const string Tag = "Tag";

    /// <summary>Column names that are text fields. The ticker symbol is not one of them: it is the key of the row, not a property of it.</summary>
    public static IReadOnlyList<string> Names { get; } = new[] { Sector, Industry, Region, Name, Tag };

    /// <summary>
    /// The text field a Column side names. The registration dialog stores the short name in <see cref="ScreenerIndicatorSideConfig.OutputName"/> and the
    /// catalog description in <see cref="ScreenerIndicatorSideConfig.CustomDisplayName"/>, while tests and saved templates may carry the name in either,
    /// so the names are tried in the order CustomDisplayName, OutputName, DisplayName. Null when none of them is a text field.
    /// </summary>
    public static string? ResolveFieldName(ScreenerIndicatorSideConfig? side)
        => side == null ? null : Resolve(side.CustomDisplayName) ?? Resolve(side.OutputName) ?? Resolve(side.DisplayName);

    /// <summary>The value of <paramref name="fieldName"/> in <paramref name="metadata"/>; null when it has none or the name is not a text field.</summary>
    public static string? GetValue(TickerMetadata metadata, string? fieldName)
        => Resolve(fieldName) switch
        {
            Sector => metadata.Sector,
            Industry => metadata.Industry,
            Region => metadata.Region,
            Name => metadata.LongName,
            Tag => metadata.Tag,
            _ => null
        };

    /// <summary>
    /// Decides a text comparison. A field value that is null or only spaces, an empty target text, and an operator other than
    /// Contains / DoesNotContain / Equal / NotEqual never match (not even the negative operators). Tag lists are compared token by token
    /// (<see cref="TagMatcher"/>) for Contains / DoesNotContain; every other comparison ignores case and surrounding spaces.
    /// </summary>
    public static bool IsMet(ComparisonOperator op, string? fieldName, string? fieldValue, string? targetText)
    {
        if (string.IsNullOrWhiteSpace(fieldValue)) return false;
        string target = targetText?.Trim() ?? string.Empty;
        if (target.Length == 0) return false;

        bool isTag = Resolve(fieldName) == Tag;
        return op switch
        {
            ComparisonOperator.Contains => ContainsText(fieldValue, target, isTag),
            ComparisonOperator.DoesNotContain => !ContainsText(fieldValue, target, isTag),
            ComparisonOperator.Equal => EqualsText(fieldValue, target),
            ComparisonOperator.NotEqual => !EqualsText(fieldValue, target),
            _ => false
        };
    }

    private static bool ContainsText(string value, string target, bool isTagList)
        => isTagList ? TagMatcher.Contains(value, target) : value.Contains(target, StringComparison.OrdinalIgnoreCase);

    private static bool EqualsText(string value, string target)
        => string.Equals(value.Trim(), target, StringComparison.OrdinalIgnoreCase);

    private static string? Resolve(string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return null;
        foreach (string name in Names)
        {
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }
}
