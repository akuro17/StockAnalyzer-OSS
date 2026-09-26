using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Result of <see cref="TemplateEntryList.Merge{T}"/>.</summary>
/// <param name="Combined">The sentinel followed by the fetched templates, in the order given.</param>
/// <param name="Replacement">The entry the previous selection resolves to after the re-fetch (the sentinel if none).</param>
/// <param name="SelectedWasDeleted">True when a template was selected before the re-fetch but is no longer listed.</param>
public readonly record struct TemplateEntryMerge<T>(IReadOnlyList<T> Combined, T Replacement, bool SelectedWasDeleted) where T : class;

/// <summary>
/// SSoT for the "sentinel entry + re-fetched templates" list rule shared by the Tickers tab dropdowns
/// (Screener Templates filter, Column Customization). A template fetch returns new instances on every
/// call, so the selection is re-pointed by Id, and falls back to the sentinel (<c>Id == Guid.Empty</c>)
/// when the selected template no longer exists.
/// </summary>
public static class TemplateEntryList
{
    public static TemplateEntryMerge<T> Merge<T>(T sentinel, IEnumerable<T> templates, Guid previousSelectedId, Func<T, Guid> idOf)
        where T : class
    {
        var combined = new List<T> { sentinel };
        combined.AddRange(templates);

        var replacement = combined.FirstOrDefault(t => idOf(t) == previousSelectedId) ?? sentinel;
        var deleted = previousSelectedId != Guid.Empty && ReferenceEquals(replacement, sentinel);
        return new TemplateEntryMerge<T>(combined, replacement, deleted);
    }
}
