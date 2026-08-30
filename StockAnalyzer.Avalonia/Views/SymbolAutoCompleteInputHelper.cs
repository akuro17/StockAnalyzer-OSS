using System;

namespace StockAnalyzer.Avalonia.Views;

/// <summary>
/// Shared symbol-normalization logic for the Ticker <c>AutoCompleteBox</c> pattern used by
/// <see cref="MainWindow"/>, <see cref="Chart.ChartView"/>, and <see cref="Dialogs.EditTickerNotesDialog"/>.
/// Keeps the Enter-key/selection confirmation rules (trim, uppercase, no-op on unchanged value) in one place.
/// </summary>
public static class SymbolAutoCompleteInputHelper
{
    public static void ApplyTypedSymbol(string? rawText, Action<string> applySymbol)
    {
        string text = rawText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            applySymbol(text.Trim().ToUpperInvariant());
        }
    }

    public static void ApplySelectedSymbol(string selectedSymbol, string? currentSymbol, Action<string> applySymbol)
    {
        if (string.IsNullOrWhiteSpace(selectedSymbol)) return;
        if (string.Equals(currentSymbol, selectedSymbol, StringComparison.OrdinalIgnoreCase)) return;
        applySymbol(selectedSymbol.Trim().ToUpperInvariant());
    }
}
