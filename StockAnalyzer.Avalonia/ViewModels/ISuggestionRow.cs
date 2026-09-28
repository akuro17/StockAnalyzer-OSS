namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// Shared display contract for an icon + name + kind-label suggestion-popup row. Implemented by
/// <see cref="Notes.NoteScopeSuggestion"/> and <see cref="TickerList.TickerTagFilterSuggestion"/> so
/// both suggestion popups bind through one shared DataTemplate (Assets/Styles.axaml,
/// "SuggestionRowTemplate") instead of each defining the same StackPanel/PathIcon/TextBlock markup
/// (sa_improve, constraint-check follow-up: Y:\Temp\sa_constraint_check_TickerTagFilter_20260923.md).
/// </summary>
public interface ISuggestionRow
{
    string IconGeometry { get; }
    string DisplayText { get; }
    string KindLabel { get; }
}
