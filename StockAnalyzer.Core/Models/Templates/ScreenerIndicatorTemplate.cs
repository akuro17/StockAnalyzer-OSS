using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable configuration template containing a collection of screener condition entries.
/// Stored independently under Data/Templates/Screener/.
/// </summary>
public class ScreenerIndicatorTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.Screener;

    private List<ScreenerIndicatorEntry> _entries = new();

    /// <summary>
    /// The collection of screener indicator entries in this template.
    /// </summary>
    public IReadOnlyList<ScreenerIndicatorEntry> Entries
    {
        get => _entries;
        init => _entries = value != null ? new List<ScreenerIndicatorEntry>(value) : new List<ScreenerIndicatorEntry>();
    }

    /// <summary>
    /// Replaces the entries in this template with a new collection.
    /// </summary>
    public void SetEntries(IEnumerable<ScreenerIndicatorEntry> entries)
    {
        _entries = entries != null ? new List<ScreenerIndicatorEntry>(entries) : new List<ScreenerIndicatorEntry>();
    }
}
