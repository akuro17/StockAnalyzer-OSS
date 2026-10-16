using System;

namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// Matches one tag against a tag list written as comma- or semicolon-separated tokens (e.g. "Growth, Tech;Value").
/// A tag matches when a whole token equals it, ignoring case and surrounding spaces; a part of a token does not match.
/// Same algorithm as the Watchlist filter's tag rule, available to Core (the Screener's text conditions).
/// </summary>
public static class TagMatcher
{
    /// <summary>True when <paramref name="targetTag"/> is one of the tokens of <paramref name="tags"/>; false when either is null, empty or only spaces.</summary>
    public static bool Contains(string? tags, string? targetTag)
    {
        if (string.IsNullOrEmpty(tags) || string.IsNullOrEmpty(targetTag)) return false;

        var targetSpan = targetTag.AsSpan().Trim();
        if (targetSpan.IsEmpty) return false;

        var span = tags.AsSpan();
        while (!span.IsEmpty)
        {
            // Find the nearest separator (comma or semicolon)
            int commaIndex = span.IndexOf(',');
            int semicolonIndex = span.IndexOf(';');
            int separatorIndex;
            if (commaIndex >= 0 && semicolonIndex >= 0)
                separatorIndex = Math.Min(commaIndex, semicolonIndex);
            else if (commaIndex >= 0)
                separatorIndex = commaIndex;
            else
                separatorIndex = semicolonIndex;

            ReadOnlySpan<char> tagSpan;
            if (separatorIndex >= 0)
            {
                tagSpan = span.Slice(0, separatorIndex).Trim();
                span = span.Slice(separatorIndex + 1);
            }
            else
            {
                tagSpan = span.Trim();
                span = ReadOnlySpan<char>.Empty;
            }

            if (tagSpan.Equals(targetSpan, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
