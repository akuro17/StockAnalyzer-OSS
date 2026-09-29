namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Represents the validation state of the symbol input in the Add Ticker dialog.
/// </summary>
public enum ValidationState
{
    /// <summary>
    /// No input or input has been cleared.
    /// </summary>
    Idle,

    /// <summary>
    /// Search is currently in progress.
    /// </summary>
    Searching,

    /// <summary>
    /// Input exactly matches a registered symbol.
    /// </summary>
    Valid,

    /// <summary>
    /// Input does not match any registered symbol but is a valid new symbol.
    /// </summary>
    NewTicker,

    /// <summary>
    /// Input is invalid (e.g. empty or invalid characters).
    /// </summary>
    Invalid
}
