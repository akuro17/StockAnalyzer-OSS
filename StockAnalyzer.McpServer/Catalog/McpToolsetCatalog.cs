using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Closed partition of the 47 read-only MCP tools into opt-in CLI <c>--toolset</c> profiles.
/// Execution tools (<c>--enable-execution</c>) and discussion tools (<c>--enable-discussion</c>) remain governed by their own flags.
/// </summary>
internal static class McpToolsetCatalog
{
    public const string AllProfile = McpToolsetProfiles.All;
    public const string CoreProfile = McpToolsetProfiles.Core;
    public const string TechnicalProfile = McpToolsetProfiles.Technical;
    public const string TechnicalExtendedProfile = McpToolsetProfiles.TechnicalExtended;
    public const string ScreenerProfile = McpToolsetProfiles.Screener;
    public const string BacktestProfile = McpToolsetProfiles.Backtest;
    public const string PortfolioProfile = McpToolsetProfiles.Portfolio;
    public const string MlProfile = McpToolsetProfiles.Ml;

    public static IReadOnlyList<string> AllowedProfiles => McpToolsetProfiles.AllowedProfiles;

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ToolsByProfile =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [CoreProfile] = Set(
                "sa_list_tickers",
                "sa_list_ticker_groups",
                "sa_filter_tickers",
                "sa_get_candles",
                "sa_get_ticker_metadata",
                "sa_get_symbol_coverage",
                "sa_get_capabilities",
                "sa_get_settings",
                "sa_get_active_workspace",
                "sa_evaluate_expressions"),
            [TechnicalProfile] = Set(
                "sa_calculate_indicators",
                "sa_analyze_market_structure",
                "sa_find_support_resistance",
                "sa_detect_candle_patterns",
                "sa_detect_divergences",
                "sa_render_chart_image"),
            [TechnicalExtendedProfile] = Set(
                "sa_calculate_indicators_batch",
                "sa_calculate_chart_series",
                "sa_calculate_reverse_watch",
                "sa_calculate_volume_profile",
                "sa_calculate_drawing_values",
                "sa_get_chart_drawings",
                "sa_detect_chart_patterns",
                "sa_analyze_cycles",
                "sa_project_price_path",
                "sa_get_seasonality"),
            [ScreenerProfile] = Set(
                "sa_run_screener",
                "sa_list_templates",
                "sa_get_template_detail",
                "sa_build_template",
                "sa_validate_condition_tree"),
            [BacktestProfile] = Set(
                "sa_run_backtest",
                "sa_optimize_backtest",
                "sa_run_walk_forward_analysis",
                "sa_run_portfolio_backtest",
                "sa_extract_signal_events"),
            [PortfolioProfile] = Set(
                "sa_get_portfolio_summary",
                "sa_analyze_portfolio_risk",
                "sa_search_notes",
                "sa_list_upcoming_catalysts",
                "sa_calculate_relative_performance",
                "sa_calculate_cross_asset_stats",
                "sa_calculate_market_breadth"),
            [MlProfile] = Set(
                "sa_check_portfolio_rl_preflight",
                "sa_check_rl_playback_preflight",
                "sa_list_models",
                "sa_get_model_explanation"),
        };

    /// <summary>Exposes the per-profile tool sets (excluding <c>all</c>) for partition completeness verification.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Profiles => ToolsByProfile;

    /// <summary>
    /// Parses a comma-separated list of profile names (case-sensitive lower-case ASCII, no whitespace or empty items).
    /// Duplicate profile names are folded idempotently in order of first appearance.
    /// </summary>
    public static bool TryParse(string raw, out IReadOnlyList<string> profiles)
        => McpToolsetProfiles.TryParse(raw, out profiles);

    /// <summary>
    /// Filters the server tool list when <paramref name="toolsets"/> is specified.
    /// Execution and discussion tools are always kept when enabled by their own flags; read-only tools are included if they belong to any selected profile (or <c>all</c>).
    /// </summary>
    public static List<IMcpTool> Filter(IEnumerable<IMcpTool> tools, IReadOnlyList<string>? toolsets)
    {
        if (toolsets is null || toolsets.Contains(AllProfile, StringComparer.Ordinal))
        {
            return tools is List<IMcpTool> existing ? existing : tools.ToList();
        }

        var allowedReadOnly = new HashSet<string>(StringComparer.Ordinal);
        foreach (string profile in toolsets)
        {
            if (ToolsByProfile.TryGetValue(profile, out IReadOnlySet<string>? set))
            {
                allowedReadOnly.UnionWith(set);
            }
        }

        var filtered = new List<IMcpTool>();
        foreach (IMcpTool tool in tools)
        {
            if (tool.IsExecution || IsDiscussionTool(tool) || allowedReadOnly.Contains(tool.Name))
            {
                filtered.Add(tool);
            }
        }

        return filtered;
    }

    private static bool IsDiscussionTool(IMcpTool tool) => tool.Name.StartsWith("sa_discussion_", StringComparison.Ordinal);

    private static IReadOnlySet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);
}
