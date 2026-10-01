using System.Globalization;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>
/// The single definition of how the equity chart writes its numbers and dates (axis labels, crosshair labels, value panels): the
/// format strings and the culture. Date and number labels are culture-invariant (SA_ARCHITECTURE_RULES section 6), so an OS culture
/// can neither change separators nor swap the calendar (a Thai or Arabic locale would otherwise print another year numbering).
/// Month names are the English abbreviations of the main chart's date axis. Localized prose (panel captions, messages) is not formatted here.
/// </summary>
public static class EquityLabelFormats
{
    public static CultureInfo Culture => CultureInfo.InvariantCulture;

    /// <summary>Full instant (crosshair time label, value panel date, trade time).</summary>
    public const string Instant = "yyyy-MM-dd HH:mm";

    /// <summary>Equity and every other currency or quantity figure of a readout.</summary>
    public const string Decimal = "F4";

    /// <summary>Axis tick of a day step, and of a clock step that falls on midnight.</summary>
    public const string MonthDay = "MM/dd";

    /// <summary>Axis tick of a year step, and of a month step that falls on January.</summary>
    public const string Year = "yyyy";

    /// <summary>Axis tick of a month step (the main chart's "Jan", "Feb", ...).</summary>
    public const string MonthName = "MMM";

    public const string ClockMinutes = "HH:mm";

    public const string ClockSeconds = "HH:mm:ss";
}
