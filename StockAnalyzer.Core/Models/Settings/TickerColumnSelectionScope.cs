namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Scope of the Tickers tab "Column Customization" dropdown selection (Settings &gt; Tickers).
/// The zero value is the pre-existing behavior, so an unset value keeps today's behavior.
/// Persisted in <c>user_tickers_settings.json</c> as its integer ordinal (no string enum converter): never reorder
/// or delete a member; add new members at the end.
/// </summary>
public enum TickerColumnSelectionScope
{
    /// <summary>Each ticker list remembers its own selection.</summary>
    PerList = 0,

    /// <summary>All ticker lists use one common selection.</summary>
    Shared = 1
}
