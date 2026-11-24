using System.Collections.Frozen;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Shared <see cref="IMcpTool.MisnamedArgumentHints"/> maps for tools that accept date cutoffs or windows under tool-specific names.
/// </summary>
internal static class DateArgumentHints
{
    public static IReadOnlyDictionary<string, string> ForEndDate { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["asOfDate"] = "This tool names the last-bar date 'endDate' (sa_run_screener uses 'asOfDate').",
            ["toDate"] = "This tool names the last-bar date 'endDate'.",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> ForStartAndEndDate { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fromDate"] = "This tool names the start date 'startDate'.",
            ["asOfDate"] = "This tool names the end date 'endDate' (sa_run_screener uses 'asOfDate').",
            ["toDate"] = "This tool names the end date 'endDate'.",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> ForStartAndEndDateExclusive { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fromDate"] = "This tool names the start date 'startDate'.",
            ["endDate"] = "This tool names the exclusive end date 'endDateExclusive'.",
            ["asOfDate"] = "This tool names the exclusive end date 'endDateExclusive'.",
            ["toDate"] = "This tool names the exclusive end date 'endDateExclusive'.",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> ForAsOfDate { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["endDate"] = "This tool names the evaluation date 'asOfDate' (sa_calculate_indicators uses 'endDate').",
            ["toDate"] = "This tool names the evaluation date 'asOfDate'.",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> ForFromToDate { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["startDate"] = "This tool names the start date 'fromDate'.",
            ["endDate"] = "This tool names the end date 'toDate'.",
            ["asOfDate"] = "This tool names the start date 'fromDate' and the end date 'toDate'.",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> baseHints,
        params (string Key, string Hint)[] extras)
    {
        var map = new Dictionary<string, string>(baseHints, StringComparer.Ordinal);
        foreach ((string key, string hint) in extras)
        {
            map[key] = hint;
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
