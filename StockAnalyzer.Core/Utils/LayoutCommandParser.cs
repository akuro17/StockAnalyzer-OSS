using System;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Core.Utils;

/// <summary>
/// Provides zero-allocation parsing of layout control command strings.
/// All logic operates strictly on ReadOnlySpan to avoid heap allocations.
/// </summary>
public static class LayoutCommandParser
{
    /// <summary>
    /// Parses a command in the format "Region:TargetId" with zero heap allocation.
    /// Matches the region name case-insensitively using span comparisons to prevent numeric enum exploits.
    /// </summary>
    /// <param name="input">The command span to parse.</param>
    /// <param name="region">Output parameter for the matched PanelRegion. Unknown if parsing fails.</param>
    /// <param name="targetId">Output parameter for the target ID slice. Default if parsing fails.</param>
    /// <returns>True if parsing succeeds; otherwise, false.</returns>
    public static bool TryParseCommand(
        ReadOnlySpan<char> input,
        out PanelRegion region,
        out ReadOnlySpan<char> targetId)
    {
        region = PanelRegion.Unknown;
        targetId = default;

        if (input.IsEmpty) return false;

        int colonIndex = input.IndexOf(':');
        // No colon or starts with a colon
        if (colonIndex <= 0) return false;
        // Ends with a colon (empty targetId)
        if (colonIndex == input.Length - 1) return false;

        // Strictly reject commands with multiple colons
        if (input.Slice(colonIndex + 1).IndexOf(':') != -1) return false;

        ReadOnlySpan<char> regionSpan = input.Slice(0, colonIndex);
        ReadOnlySpan<char> idSpan = input.Slice(colonIndex + 1);

        // Exploit-proof and allocation-free enum resolution
        PanelRegion parsed;
        if (MemoryExtensions.Equals(regionSpan, "Left".AsSpan(), StringComparison.OrdinalIgnoreCase))
            parsed = PanelRegion.Left;
        else if (MemoryExtensions.Equals(regionSpan, "Right".AsSpan(), StringComparison.OrdinalIgnoreCase))
            parsed = PanelRegion.Right;
        else if (MemoryExtensions.Equals(regionSpan, "Top".AsSpan(), StringComparison.OrdinalIgnoreCase))
            parsed = PanelRegion.Top;
        else if (MemoryExtensions.Equals(regionSpan, "Bottom".AsSpan(), StringComparison.OrdinalIgnoreCase))
            parsed = PanelRegion.Bottom;
        else
            return false;

        // Reject whitespace-only, leading, or trailing whitespace in TargetId
        if (char.IsWhiteSpace(idSpan[0]) || char.IsWhiteSpace(idSpan[idSpan.Length - 1])) return false;

        region = parsed;
        targetId = idSpan;
        return true;
    }
}
