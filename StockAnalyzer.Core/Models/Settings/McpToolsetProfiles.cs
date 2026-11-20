using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Single source of truth for StockAnalyzer.McpServer <c>--toolset</c> profile names and parsing rules.
/// Shared across Core settings validation, Avalonia UI, and StockAnalyzer.McpServer CLI/filtering.
/// </summary>
public static class McpToolsetProfiles
{
    public const string EnableDiscussionToken = "--enable-discussion";
    public const string ToolsetToken = "--toolset";
    public const string ResponseFormatToken = "--response-format";
    public const string DualResponseFormat = "dual";
    public const string TextOnlyResponseFormat = "text-only";
    public const string CompactDefaultsToken = "--compact-defaults";
    public const string ProgressiveDiscoveryToken = "--progressive-discovery";

    public const string All = "all";
    public const string Core = "core";
    public const string Technical = "technical";
    public const string TechnicalExtended = "technical-extended";
    public const string Screener = "screener";
    public const string Backtest = "backtest";
    public const string Portfolio = "portfolio";
    public const string Ml = "ml";

    /// <summary>Recommended lightweight AI client profile combination (~10K tools/list tokens).</summary>
    public const string RecommendedPresetToolset = $"{Core},{Technical}";

    /// <summary>All profile tokens accepted by <c>--toolset</c>, case-sensitive lower-case ASCII.</summary>
    public static IReadOnlyList<string> AllowedProfiles { get; } = new[]
    {
        All,
        Core,
        Technical,
        TechnicalExtended,
        Screener,
        Backtest,
        Portfolio,
        Ml,
    };

    /// <summary>Domain-specific selectable profiles excluding <see cref="All"/>.</summary>
    public static IReadOnlyList<string> SelectableProfiles { get; } = new[]
    {
        Core,
        Technical,
        TechnicalExtended,
        Screener,
        Backtest,
        Portfolio,
        Ml,
    };

    /// <summary>
    /// Parses a comma-separated list of profile names (case-sensitive lower-case ASCII, no whitespace or empty items).
    /// Duplicate profile names are folded idempotently in order of first appearance.
    /// </summary>
    public static bool TryParse(string? raw, out IReadOnlyList<string> profiles)
    {
        if (string.IsNullOrEmpty(raw))
        {
            profiles = Array.Empty<string>();
            return false;
        }

        string[] parts = raw.Split(',');
        var unique = new List<string>(parts.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in parts)
        {
            if (part.Length == 0 || !AllowedProfiles.Contains(part, StringComparer.Ordinal))
            {
                profiles = Array.Empty<string>();
                return false;
            }

            if (seen.Add(part))
            {
                unique.Add(part);
            }
        }

        profiles = unique;
        return true;
    }

    /// <summary>Trims whitespace; returns <see cref="string.Empty"/> for null/whitespace input.</summary>
    public static string NormalizeText(string? raw) => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();
}
