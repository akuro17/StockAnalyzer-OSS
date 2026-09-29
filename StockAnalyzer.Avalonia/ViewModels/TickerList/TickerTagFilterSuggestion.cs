using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

public enum TickerTagFilterSuggestionKind
{
    Ticker,
    Tag,
}

/// <summary>
/// One entry in the Ticker/Tag filter's suggestion list (Y:\Temp\sa_implementation_plan_TickerTagFilter_Redo.md
/// Phase 2, sa_improve follow-up). Mirrors the icon + name + kind-label row layout of
/// <see cref="StockAnalyzer.Avalonia.ViewModels.Notes.NoteScopeSuggestion"/> (Notes tab's unified
/// search box) so both suggestion popups look and read the same way; reuses that same icon/label SSoT
/// (<see cref="SharedIconGeometries"/>, the <c>Note_SearchSuggestion_TypeName_*</c> localization keys)
/// rather than duplicating it, since Ticker/Tag is exactly a subset of Notes' six suggestion kinds.
/// </summary>
public sealed class TickerTagFilterSuggestion : ISuggestionRow
{
    public TickerTagFilterSuggestionKind Kind { get; }
    public string Value { get; }

    /// <summary>Explicit <see cref="ISuggestionRow"/> implementation: the shared suggestion-row
    /// DataTemplate binds this, while callers using the concrete type keep binding <see cref="Value"/>
    /// (unchanged name, avoiding an unrelated rename of existing call sites).</summary>
    string ISuggestionRow.DisplayText => Value;

    private TickerTagFilterSuggestion(TickerTagFilterSuggestionKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public static TickerTagFilterSuggestion ForTicker(string ticker) => new(TickerTagFilterSuggestionKind.Ticker, ticker);

    public static TickerTagFilterSuggestion ForTag(string tag) => new(TickerTagFilterSuggestionKind.Tag, tag);

    public string IconGeometry => Kind switch
    {
        TickerTagFilterSuggestionKind.Ticker => SharedIconGeometries.Ticker,
        TickerTagFilterSuggestionKind.Tag => SharedIconGeometries.Tag,
        _ => string.Empty,
    };

    public string KindLabel => Kind switch
    {
        TickerTagFilterSuggestionKind.Ticker => LocalizationManager.Instance["Note_SearchSuggestion_TypeName_Ticker"],
        TickerTagFilterSuggestionKind.Tag => LocalizationManager.Instance["Note_SearchSuggestion_TypeName_Tag"],
        _ => string.Empty,
    };

    public override string ToString() => Value;
}
