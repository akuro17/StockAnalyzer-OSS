namespace StockAnalyzer.Core.Models.Indicators;

/// <summary>
/// The names of output series that carry a role rather than a value line: the discrete trading signals and the up-trend flag. The one definition of these names for every
/// consumer (the AI chart image, the chart's sub-panel range, the price-range scaling, the data window and the chart information line).
/// <para>
/// Two questions are kept apart on purpose (owner decision, 2026-11-05: the data window shows the GranvilleLaw series "Signals"). <see cref="IsDirectionalSignalSeries"/> names the four
/// buy/sell/bullish/bearish series, which the lists the user reads (data window, chart information) leave out. <see cref="IsSignalSeries"/> adds the series "Signals" of GranvilleLaw,
/// which is a signal as well but a value the user wants to read, and is the question for the places that must not let signal values into a price or panel scale or a drawn line
/// (sub-panel auto-range, price-range scaling, the AI chart image). A change of either set is a change of what the user sees.
/// </para>
/// <para>
/// Other sites are not copies of these: <c>IndicatorOutputSeriesResolver</c> skips every name ending in "Signals" and <c>CoreIndicatorBase</c> maps the bullish and bearish names to
/// confluence signal types.
/// </para>
/// </summary>
public static class IndicatorSeriesNames
{
    /// <summary>The 1 or 0 flag series of a trend-following indicator (for example the Parabolic SAR direction); not a price line.</summary>
    public const string UpTrendFlag = "IsUpTrend";

    /// <summary>The name of the signal series of GranvilleLaw (a value the data window shows).</summary>
    public const string GranvilleSignals = "Signals";

    private static readonly string[] DirectionalSignalSeries = { "BullishSignals", "BearishSignals", "BuySignals", "SellSignals" };

    /// <summary>True for the four buy/sell/bullish/bearish signal series; the data window and the chart information line leave them out.</summary>
    public static bool IsDirectionalSignalSeries(string seriesName) => Array.IndexOf(DirectionalSignalSeries, seriesName) >= 0;

    /// <summary>True for a series of discrete trading signals (not a continuous indicator line): the four directional ones and the GranvilleLaw "Signals".</summary>
    public static bool IsSignalSeries(string seriesName) => IsDirectionalSignalSeries(seriesName) || string.Equals(seriesName, GranvilleSignals, StringComparison.Ordinal);
}
