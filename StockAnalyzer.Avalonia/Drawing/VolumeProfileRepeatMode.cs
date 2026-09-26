namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Specifies the display and repetition mode for Fixed Range Volume Profile drawing objects.
/// </summary>
public enum VolumeProfileRepeatMode
{
    /// <summary>
    /// Default mode: Displays a single Volume Profile across the specified anchor range.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Range Bar mode: Repeatedly displays Volume Profile for each interval of the specified RangeBars count.
    /// </summary>
    RangeBar = 1,

    /// <summary>
    /// Weekly mode: Repeatedly displays Volume Profile for each calendar week (left edge to weekend).
    /// </summary>
    Weekly = 2,

    /// <summary>
    /// Monthly mode: Repeatedly displays Volume Profile for each calendar month (left edge to month end).
    /// </summary>
    Monthly = 3
}
