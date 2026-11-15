using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Single source of truth for identifying Screener sides/conditions referencing RS Rating and building
/// on-demand cross-sectional <see cref="IScreenerEvaluationContext"/> instances across a symbol universe.
/// </summary>
public static class ScreenerRsRatingInspector
{
    public const string ColumnMemberName = "RsRating";
    public const string ColumnDisplayName = "RS Rating";

    /// <summary>
    /// Returns <c>true</c> when <paramref name="side"/> names the RS Rating column.
    /// </summary>
    public static bool IsRsRatingColumn(ScreenerIndicatorSideConfig? side)
    {
        if (side is not { CategoryType: ScreenerItemCategoryType.Column })
        {
            return false;
        }

        return MatchesRsRatingName(side.CustomDisplayName)
            || MatchesRsRatingName(side.OutputName)
            || MatchesRsRatingName(side.DisplayName);
    }

    /// <summary>
    /// Returns <c>true</c> when any active comparison side in <paramref name="condition"/> references RS Rating.
    /// </summary>
    public static bool UsesRsRating(IScreeningCondition? condition)
    {
        return condition switch
        {
            ScreenerIndicatorEntry entry => UsesRsRating(entry),
            ScreenerConditionTreeCondition tree => UsesRsRating(tree.Root),
            _ => false
        };
    }

    /// <summary>
    /// Returns <c>true</c> when any enabled entry of <paramref name="template"/> references RS Rating.
    /// </summary>
    public static bool UsesRsRating(ScreenerIndicatorTemplate? template)
    {
        if (template?.Entries == null)
        {
            return false;
        }

        for (int i = 0; i < template.Entries.Count; i++)
        {
            var entry = template.Entries[i];
            if (entry.IsEnabled && UsesRsRating(entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when any active comparison side in <paramref name="entries"/> references RS Rating.
    /// </summary>
    public static bool UsesRsRating(IEnumerable<ScreenerIndicatorEntry>? entries)
    {
        if (entries == null)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.IsEnabled && UsesRsRating(entry))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsesRsRating(ScreenerIndicatorEntry? entry)
    {
        if (entry == null)
        {
            return false;
        }

        if (IsRsRatingColumn(entry.LeftHand))
        {
            return true;
        }

        return entry.TargetMode == RightHandTargetMode.Indicator && IsRsRatingColumn(entry.RightHand);
    }

    private static bool UsesRsRating(IScreenerConditionNode? node)
    {
        switch (node)
        {
            case ScreenerConditionLeaf leaf:
                return leaf.Entry.IsEnabled && UsesRsRating(leaf.Entry);
            case ScreenerConditionGroup group:
                for (int i = 0; i < group.Children.Length; i++)
                {
                    if (UsesRsRating(group.Children[i]))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static bool MatchesRsRatingName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = name.Trim();
        return string.Equals(trimmed, ColumnMemberName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, ColumnDisplayName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes RS Rating across <paramref name="universeSymbols"/> once using <paramref name="provider"/>.
    /// Returns <see cref="ScreenerEvaluationContext.Empty"/> without querying when <paramref name="asOfDate"/> is specified
    /// (to prevent future data leakage from the latest 253-close snapshot), or when <paramref name="provider"/> is null
    /// or <paramref name="universeSymbols"/> is empty.
    /// </summary>
    public static async Task<IScreenerEvaluationContext> ComputeContextAsync(
        IRsRatingClosesProvider? provider,
        IReadOnlyList<string> universeSymbols,
        DateOnly? asOfDate = null,
        CancellationToken cancellationToken = default)
    {
        if (asOfDate.HasValue || provider == null || universeSymbols == null || universeSymbols.Count == 0)
        {
            return ScreenerEvaluationContext.Empty;
        }

        var distinct = universeSymbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0)
        {
            return ScreenerEvaluationContext.Empty;
        }

        IReadOnlyDictionary<string, RsRatingCloses> closes =
            await provider.GetRsRatingClosesAsync(distinct, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, int> ratings = RsRatingCalculator.Calculate(closes);
        return new ScreenerEvaluationContext(ratings);
    }
}
