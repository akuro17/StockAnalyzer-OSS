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
    /// Tunable settings for the discussion hub (AiIntegration:Discussion).
    /// </summary>
    public DiscussionSettings Discussion { get; set; } = new();

    /// <summary>
    /// Builds the CLI argument string passed when starting StockAnalyzer.McpServer.
    /// </summary>
    public string BuildLaunchArguments()
    {
        var args = new List<string>(3);
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

        return string.Join(" ", args);
    }

    public McpServerSettings Clone()
    {
        return new McpServerSettings
        {
            EnableDiscussion = EnableDiscussion,
            Toolset = Toolset,
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
