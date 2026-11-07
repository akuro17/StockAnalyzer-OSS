using System;
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
    /// Tunable settings for the discussion hub (AiIntegration:Discussion).
    /// </summary>
    public DiscussionSettings Discussion { get; set; } = new();

    public McpServerSettings Clone()
    {
        return new McpServerSettings
        {
            EnableDiscussion = EnableDiscussion,
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
