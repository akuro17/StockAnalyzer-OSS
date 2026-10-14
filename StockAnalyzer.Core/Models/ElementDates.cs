namespace StockAnalyzer.Core.Models;

/// <summary>
/// The two dates of one element of a non-time-series chart (Kagi segment, Point and Figure column).
/// Start = date of the source candle that created the element;
/// End = date of the source candle that last extended it (equal to Start when it was never extended).
/// </summary>
public readonly record struct ElementDates(DateTime Start, DateTime End);
