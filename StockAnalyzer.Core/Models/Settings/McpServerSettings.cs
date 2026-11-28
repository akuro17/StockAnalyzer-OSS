using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Discussion;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// User configuration for StockAnalyzer.McpServer process launch options and tunable discussion hub parameters.
/// Persisted in Data/Config/user_mcpserver_settings.json.
/// </summary>
public sealed class McpServerSettings
{
    public const string SectionName = "McpServer";
    public const string UserConfigFileName = "user_mcpserver_settings.json";

    /// <summary>
    /// When true, StockAnalyzer.McpServer is launched with the --enable-discussion command-line flag.
    /// Default is false (read-only tools only).
    /// </summary>
    public bool EnableDiscussion { get; set; }

    /// <summary>
    /// Comma-separated <c>--toolset</c> profile list (e.g. <c>core,screener,backtest</c>).
    /// Empty string (default) omits <c>--toolset</c> so all read-only tools are exposed.
    /// </summary>
    public string Toolset { get; set; } = string.Empty;

    /// <summary>
    /// When true, StockAnalyzer.McpServer is launched with <c>--response-format text-only</c> so
    /// <c>outputSchema</c> and <c>structuredContent</c> are omitted from MCP responses.
    /// Default is false (<c>dual</c>).
    /// </summary>
    public bool UseTextOnlyResponse { get; set; }

    /// <summary>
    /// When true, StockAnalyzer.McpServer is launched with <c>--compact-defaults</c> so eligible
    /// tools use compact and rounded defaults when callers omit formatting options.
    /// Default is false.
    /// </summary>
    public bool CompactDefaults { get; set; }

    /// <summary>
    /// When true, StockAnalyzer.McpServer is launched with <c>--progressive-discovery</c> so
    /// <c>tools/list</c> exposes only <c>sa_search_tools</c> and <c>sa_invoke_tool</c>
    /// (plus <c>sa_invoke_action</c> when non-read-only tools are active).
    /// Default is false.
    /// </summary>
    public bool ProgressiveDiscovery { get; set; }

    /// <summary>
    /// Estimated maximum tokens per MCP tool response (<c>AiIntegration:MaxResponseTokens</c>).
    /// Default equals <see cref="McpResponseTokenLimits.Default"/>.
    /// </summary>
    public int MaxResponseTokens { get; set; } = McpResponseTokenLimits.Default;

    /// <summary>
    /// Tunable settings for the discussion hub (AiIntegration:Discussion).
    /// </summary>
    public DiscussionSettings Discussion { get; set; } = new();

    /// <summary>
    /// Builds the ordered CLI argument tokens passed when starting StockAnalyzer.McpServer via <c>ProcessStartInfo.ArgumentList</c>.
    /// </summary>
    public IReadOnlyList<string> BuildLaunchArgumentList()
    {
        var args = new List<string>(7);
        if (EnableDiscussion)
        {
            args.Add(McpToolsetProfiles.EnableDiscussionToken);
        }

        string normalizedToolset = McpToolsetProfiles.NormalizeText(Toolset);
        if (!string.IsNullOrEmpty(normalizedToolset) && McpToolsetProfiles.TryParse(normalizedToolset, out IReadOnlyList<string> parsed))
        {
            args.Add(McpToolsetProfiles.ToolsetToken);
            args.Add(string.Join(",", parsed));
        }

        if (UseTextOnlyResponse)
        {
            args.Add(McpToolsetProfiles.ResponseFormatToken);
            args.Add(McpToolsetProfiles.TextOnlyResponseFormat);
        }

        if (CompactDefaults)
        {
            args.Add(McpToolsetProfiles.CompactDefaultsToken);
        }

        if (ProgressiveDiscovery)
        {
            args.Add(McpToolsetProfiles.ProgressiveDiscoveryToken);
        }

        return args;
    }

    /// <summary>
    /// Builds the CLI argument string passed when starting StockAnalyzer.McpServer.
    /// </summary>
    public string BuildLaunchArguments() => string.Join(" ", BuildLaunchArgumentList());

    public McpServerSettings Clone()
    {
        return new McpServerSettings
        {
            EnableDiscussion = EnableDiscussion,
            Toolset = Toolset,
            UseTextOnlyResponse = UseTextOnlyResponse,
            CompactDefaults = CompactDefaults,
            ProgressiveDiscovery = ProgressiveDiscovery,
            MaxResponseTokens = MaxResponseTokens,
            Discussion = CloneDiscussion(Discussion)
        };
    }

    public static DiscussionSettings CloneDiscussion(DiscussionSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new DiscussionSettings
        {
            MaxActiveSessions = source.MaxActiveSessions,
            MaxParticipantsPerSession = source.MaxParticipantsPerSession,
            MaxMessagesPerSession = source.MaxMessagesPerSession,
            MaxMessageBytes = source.MaxMessageBytes,
            MaxContextsPerSession = source.MaxContextsPerSession,
            MaxContextBytes = source.MaxContextBytes,
            MaxTotalBytes = source.MaxTotalBytes,
            IdleTimeoutSeconds = source.IdleTimeoutSeconds,
            MaxSessionAgeSeconds = source.MaxSessionAgeSeconds,
            ClosedRetentionSeconds = source.ClosedRetentionSeconds,
            AwaySeconds = source.AwaySeconds,
            FloorTimeoutSeconds = source.FloorTimeoutSeconds,
            MaxWaitSeconds = source.MaxWaitSeconds,
            MaxPostsPerMinute = source.MaxPostsPerMinute,
            DefaultReadLimit = source.DefaultReadLimit,
            MaxReadLimit = source.MaxReadLimit,
            AuditCapacity = source.AuditCapacity,
            CommandLogCapacity = source.CommandLogCapacity,
            CommandLogToFile = source.CommandLogToFile,
            CommandLogMaxFileBytes = source.CommandLogMaxFileBytes,
            CommandLogGenerations = source.CommandLogGenerations,
            CommandLogMaxAgeHours = source.CommandLogMaxAgeHours
        };
    }
}
