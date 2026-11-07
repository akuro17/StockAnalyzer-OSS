using System.Text.Json;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The one definition of how a tool names and writes what it says about the running last period of its W1 or MN1 bars (sa_get_candles, sa_calculate_indicators; sa_run_screener shares the argument name):
/// the <c>completeBarsOnly</c> argument and the four result keys, in the order they are written. Names, types and format are the same in every tool by construction.
/// </summary>
internal static class PartialBarOutput
{
    public const string CompleteBarsOnlyArgument = "completeBarsOnly";
    public const string ReferenceDateKey = "referenceDate";
    public const string LastBarPeriodEndKey = "lastBarPeriodEnd";
    public const string LastBarPartialKey = "lastBarPartial";
    public const string DroppedPartialBarKey = "droppedPartialBar";

    /// <summary>
    /// Writes the four keys: the reference date (or null), the last calendar day of the period of the last bar (or null), whether that bar is partial, and whether a partial last bar was left out.
    /// </summary>
    public static void Write(Utf8JsonWriter writer, DateTime? referenceDate, DateTime? lastBarPeriodEnd, bool lastBarPartial, bool droppedPartialBar)
    {
        WriteDateOrNull(writer, ReferenceDateKey, referenceDate);
        WriteDateOrNull(writer, LastBarPeriodEndKey, lastBarPeriodEnd);
        writer.WriteBoolean(LastBarPartialKey, lastBarPartial);
        writer.WriteBoolean(DroppedPartialBarKey, droppedPartialBar);
    }

    private static void WriteDateOrNull(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value is { } date)
        {
            writer.WriteString(name, ToolJson.FormatDate(date));
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
