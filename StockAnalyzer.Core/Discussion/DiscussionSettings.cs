using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Discussion;

/// <summary>A configured discussion setting is outside its hard band or breaks a relation between settings (checked at start-up; names the configuration key).</summary>
public sealed class DiscussionLimitsException(string message) : Exception(message);

/// <summary>An inclusive integer range, used for the hard bands of the discussion settings.</summary>
public readonly record struct DiscussionBand(int Minimum, int Maximum)
{
    public bool Contains(int value) => value >= Minimum && value <= Maximum;
}

/// <summary>
/// The tunable settings of the discussion board, bound from the <c>AiIntegration:Discussion</c> configuration section. The initialisers are the standard
/// values (consistent with each other and measured in task T10: see the measurements in SA_AI_INTEGRATION.md, block sa_ai_integration_0018); the hard bands they must stay
/// inside are in <see cref="DiscussionLimits"/> and are checked at start-up by <see cref="DiscussionSettingsValidator"/>.
/// </summary>
public sealed class DiscussionSettings
{
    public const string SectionName = "AiIntegration:Discussion";

    /// <summary>Sessions in state active.</summary>
    public int MaxActiveSessions { get; set; } = 4;

    /// <summary>Participants of one session that have not left.</summary>
    public int MaxParticipantsPerSession { get; set; } = 6;

    /// <summary>Messages of one session, server events included (the closing event is exempt).</summary>
    public int MaxMessagesPerSession { get; set; } = 300;

    /// <summary>UTF-8 bytes of the content of one message.</summary>
    public int MaxMessageBytes { get; set; } = 16 * 1024;

    /// <summary>Attached analysis snapshots of one session.</summary>
    public int MaxContextsPerSession { get; set; } = 20;

    /// <summary>UTF-8 bytes of the result of one analysis snapshot.</summary>
    public int MaxContextBytes { get; set; } = 512 * 1024;

    /// <summary>Logical payload budget over all sessions, in UTF-8 bytes (object overhead is not counted).</summary>
    public long MaxTotalBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>An active session without activity this long is closed ("idle").</summary>
    public int IdleTimeoutSeconds { get; set; } = 1800;

    /// <summary>An active session this old is closed ("maxAge").</summary>
    public int MaxSessionAgeSeconds { get; set; } = 7200;

    /// <summary>A closed session stays readable this long, then it is purged.</summary>
    public int ClosedRetentionSeconds { get; set; } = 300;

    /// <summary>A participant silent this long is shown as away.</summary>
    public int AwaySeconds { get; set; } = 120;

    /// <summary>In round-robin mode the floor passes to the next participant when the holder is silent this long.</summary>
    public int FloorTimeoutSeconds { get; set; } = 60;

    /// <summary>Upper bound of the <c>waitSeconds</c> argument of a read.</summary>
    public int MaxWaitSeconds { get; set; } = 20;

    /// <summary>Accepted message posts per participant in the sliding rate window.</summary>
    public int MaxPostsPerMinute { get; set; } = 20;

    /// <summary>Default <c>limit</c> of a read.</summary>
    public int DefaultReadLimit { get; set; } = 20;

    /// <summary>Largest <c>limit</c> of a read.</summary>
    public int MaxReadLimit { get; set; } = 50;

    /// <summary>Entries of the hub audit ring.</summary>
    public int AuditCapacity { get; set; } = 1000;

    /// <summary>Entries of the per-process command ring.</summary>
    public int CommandLogCapacity { get; set; } = 1000;

    /// <summary>Also write the command metadata to a rolling file below the temp folder (off by default).</summary>
    public bool CommandLogToFile { get; set; }

    /// <summary>The command file is rotated when it reaches this size.</summary>
    public int CommandLogMaxFileBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Command files kept, the current one included.</summary>
    public int CommandLogGenerations { get; set; } = 3;

    /// <summary>The start-up clean-up deletes command files whose last write is older than this.</summary>
    public int CommandLogMaxAgeHours { get; set; } = 24;
}

/// <summary>
/// The safety contract of the discussion board: the hard bands of the tunable settings (a value outside is fatal at start-up) and the fixed values, which
/// are code constants and not configurable. Every number here is normative (spec sections 4.1 and 4.2).
/// </summary>
public static class DiscussionLimits
{
    // ---- Hard bands of the tunable settings (inclusive) --------------------------------------------------------------------------

    public static readonly DiscussionBand ActiveSessionsBand = new(1, 16);
    public static readonly DiscussionBand ParticipantsBand = new(2, 16);
    public static readonly DiscussionBand MessagesBand = new(10, 2000);
    public static readonly DiscussionBand MessageBytesBand = new(256, 65536);
    public static readonly DiscussionBand ContextsBand = new(1, 100);
    public static readonly DiscussionBand ContextBytesBand = new(1024, 2 * 1024 * 1024);
    public const long MinTotalBytes = 1024L * 1024;
    public const long MaxTotalBytesAllowed = 256L * 1024 * 1024;
    public static readonly DiscussionBand IdleTimeoutBand = new(60, 86400);
    public static readonly DiscussionBand MaxSessionAgeBand = new(300, 86400);
    public static readonly DiscussionBand ClosedRetentionBand = new(0, 3600);
    public static readonly DiscussionBand AwayBand = new(10, 3600);
    public static readonly DiscussionBand FloorTimeoutBand = new(10, 600);
    public static readonly DiscussionBand WaitBand = new(0, 30);
    public static readonly DiscussionBand PostsPerMinuteBand = new(1, 120);

    /// <summary>The upper end of <see cref="DiscussionSettings.DefaultReadLimit"/> is <see cref="DiscussionSettings.MaxReadLimit"/> itself.</summary>
    public const int MinDefaultReadLimit = 1;

    public static readonly DiscussionBand MaxReadLimitBand = new(1, 200);
    public static readonly DiscussionBand AuditCapacityBand = new(100, 10000);
    public static readonly DiscussionBand CommandLogCapacityBand = new(100, 10000);
    public static readonly DiscussionBand CommandLogFileBytesBand = new(65536, 16 * 1024 * 1024);
    public static readonly DiscussionBand CommandLogGenerationsBand = new(1, 8);
    public static readonly DiscussionBand CommandLogMaxAgeHoursBand = new(1, 168);

    /// <summary>Bytes reserved for the envelope around the messages of one read result when it is checked against the response limit of the host.</summary>
    public const int ReadResponseAllowanceBytes = 65536;

    // ---- Fixed values (spec section 4.2) -----------------------------------------------------------------------------------------

    /// <summary>Hub/remote handshake version; a mismatch is HUB_UNAVAILABLE.</summary>
    public const int WireVersion = 1;

    /// <summary>Request/reply line bound on the pipe.</summary>
    public const int WireMaxLineBytes = 5242880;

    /// <summary>Simultaneous pipe connections the hub accepts.</summary>
    public const int MaxHubConnections = 32;

    public const int PipeConnectTimeoutMs = 300;
    public const int ElectionAttempts = 3;
    public const int ElectionPauseMs = 50;

    /// <summary>How long a hub waits for the request line of an accepted connection before it drops the connection.</summary>
    public const int WireRequestTimeoutMs = 5000;

    /// <summary>How long a hub waits, after it wrote a reply, for the peer to read it and close its end, before it closes its own.</summary>
    public const int WireDrainTimeoutMs = 2000;

    /// <summary>Pause before a hub retries to open a new listening instance while all <see cref="MaxHubConnections"/> are in use.</summary>
    public const int AcceptRetryMs = 20;

    /// <summary>How long a shutting hub waits for its accept loop and its open connections to end.</summary>
    public const int HubShutdownWaitMs = 2000;

    public const int SweepIntervalSeconds = 30;
    public const int TitleMaxChars = 100;
    public const int TopicMaxChars = 2000;
    public const int DisplayNameMaxChars = 64;
    public const int ClientRequestIdMaxChars = 64;
    public const int IdempotencyRemembered = 64;

    /// <summary>The join counter of a session may reach this many times the participant limit (bounds join/leave churn).</summary>
    public const int JoinsPerParticipantFactor = 2;

    public const int PostRateWindowSeconds = 60;
    public const int NoteMaxChars = 500;

    /// <summary>Longest tool name stored with an analysis snapshot (registered tool names are far shorter).</summary>
    public const int ToolNameMaxChars = 64;

    /// <summary>Longest participant token the hub looks at; a longer text cannot be one.</summary>
    public const int TokenMaxChars = 64;

    /// <summary>Most context ids one post may reference.</summary>
    public const int MaxContextIdsPerPost = 8;

    /// <summary>Largest <c>auditTail</c> / <c>commandTail</c> of the status.</summary>
    public const int StatusTailMax = 100;

    /// <summary>Most argument names one command record keeps (the rest are not listed; the byte count still covers all of them).</summary>
    public const int CommandLogMaxArgKeys = 32;

    /// <summary>Longest tool name, argument name, outcome and request id a command record keeps, in characters; longer text is cut. Keeps a record, and so the ring and the file, bounded whatever a client sends.</summary>
    public const int CommandLogTextMaxChars = 64;

    /// <summary>The file names of the command log: <c>commands-{process id}-{start time}.jsonl</c>, rotated to <c>.1</c>, <c>.2</c> ...</summary>
    public const string CommandLogFilePrefix = "commands-";

    public const string CommandLogFileExtension = ".jsonl";
    public const int AnalysisArgumentsMaxBytes = 4096;
    public const int ClientLabelMaxChars = 64;
    public const int EpochTagChars = 6;
    public const int TokenRandomBytes = 16;

    /// <summary>A session id is this letter followed by <see cref="SessionIdRandomChars"/> base32 characters.</summary>
    public const char SessionIdPrefix = 'S';

    public const int SessionIdRandomChars = 6;
    public const int SessionIdChars = 1 + SessionIdRandomChars;

    /// <summary>How many times a new session id is drawn before the draw is given up (a collision among a handful of live sessions is practically impossible).</summary>
    public const int IdDrawAttempts = 16;

    /// <summary>The base32 alphabet of epoch tags and session ids (RFC 4648, lower case).</summary>
    public const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>Maximum concurrent long-polling waiting readers in a single session.</summary>
    public const int MaxWaitersPerSession = 8;

    /// <summary>Maximum concurrent long-polling waiting readers across the entire hub (at most half of <see cref="MaxHubConnections"/> = 32).</summary>
    public const int MaxTotalWaiters = 16;

    /// <summary>Multiplier applied to MaxActiveSessions to determine the maximum retained closed sessions.</summary>
    public const int ClosedSessionRetentionMultiplier = 4;

    /// <summary>The most joins (new and re-joins) a session accepts with this participant limit.</summary>
    public static int MaxJoinsPerSession(int maxParticipantsPerSession) => JoinsPerParticipantFactor * maxParticipantsPerSession;
}

/// <summary>Checks <see cref="DiscussionSettings"/> against <see cref="DiscussionLimits"/>.</summary>
public static class DiscussionSettingsValidator
{
    /// <summary>
    /// Checks every hard band and every relation that needs no host value. A value outside is fatal. Returns the information lines for values that are inside the
    /// bands but beyond the standard zone (the host logs them; they never stop the start-up).
    /// </summary>
    /// <exception cref="DiscussionLimitsException">A value is outside its band or breaks a relation.</exception>
    public static IReadOnlyList<string> Validate(DiscussionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Band(nameof(DiscussionSettings.MaxActiveSessions), settings.MaxActiveSessions, DiscussionLimits.ActiveSessionsBand);
        Band(nameof(DiscussionSettings.MaxParticipantsPerSession), settings.MaxParticipantsPerSession, DiscussionLimits.ParticipantsBand);
        Band(nameof(DiscussionSettings.MaxMessagesPerSession), settings.MaxMessagesPerSession, DiscussionLimits.MessagesBand);
        Band(nameof(DiscussionSettings.MaxMessageBytes), settings.MaxMessageBytes, DiscussionLimits.MessageBytesBand);
        Band(nameof(DiscussionSettings.MaxContextsPerSession), settings.MaxContextsPerSession, DiscussionLimits.ContextsBand);
        Band(nameof(DiscussionSettings.MaxContextBytes), settings.MaxContextBytes, DiscussionLimits.ContextBytesBand);
        Band(nameof(DiscussionSettings.MaxTotalBytes), settings.MaxTotalBytes, DiscussionLimits.MinTotalBytes, DiscussionLimits.MaxTotalBytesAllowed);
        Band(nameof(DiscussionSettings.IdleTimeoutSeconds), settings.IdleTimeoutSeconds, DiscussionLimits.IdleTimeoutBand);
        Band(nameof(DiscussionSettings.MaxSessionAgeSeconds), settings.MaxSessionAgeSeconds, DiscussionLimits.MaxSessionAgeBand);
        Band(nameof(DiscussionSettings.ClosedRetentionSeconds), settings.ClosedRetentionSeconds, DiscussionLimits.ClosedRetentionBand);
        Band(nameof(DiscussionSettings.AwaySeconds), settings.AwaySeconds, DiscussionLimits.AwayBand);
        Band(nameof(DiscussionSettings.FloorTimeoutSeconds), settings.FloorTimeoutSeconds, DiscussionLimits.FloorTimeoutBand);
        Band(nameof(DiscussionSettings.MaxWaitSeconds), settings.MaxWaitSeconds, DiscussionLimits.WaitBand);
        Band(nameof(DiscussionSettings.MaxPostsPerMinute), settings.MaxPostsPerMinute, DiscussionLimits.PostsPerMinuteBand);
        Band(nameof(DiscussionSettings.MaxReadLimit), settings.MaxReadLimit, DiscussionLimits.MaxReadLimitBand);
        Band(nameof(DiscussionSettings.DefaultReadLimit), settings.DefaultReadLimit, new DiscussionBand(DiscussionLimits.MinDefaultReadLimit, settings.MaxReadLimit));
        Band(nameof(DiscussionSettings.AuditCapacity), settings.AuditCapacity, DiscussionLimits.AuditCapacityBand);
        Band(nameof(DiscussionSettings.CommandLogCapacity), settings.CommandLogCapacity, DiscussionLimits.CommandLogCapacityBand);
        Band(nameof(DiscussionSettings.CommandLogMaxFileBytes), settings.CommandLogMaxFileBytes, DiscussionLimits.CommandLogFileBytesBand);
        Band(nameof(DiscussionSettings.CommandLogGenerations), settings.CommandLogGenerations, DiscussionLimits.CommandLogGenerationsBand);
        Band(nameof(DiscussionSettings.CommandLogMaxAgeHours), settings.CommandLogMaxAgeHours, DiscussionLimits.CommandLogMaxAgeHoursBand);

        // A single context or message larger than the whole budget could never be stored.
        if (settings.MaxContextBytes > settings.MaxTotalBytes)
        {
            throw Failure(nameof(DiscussionSettings.MaxContextBytes), $"is {settings.MaxContextBytes}; it must be at most {nameof(DiscussionSettings.MaxTotalBytes)} ({settings.MaxTotalBytes}).");
        }

        if (settings.MaxMessageBytes > settings.MaxTotalBytes)
        {
            throw Failure(nameof(DiscussionSettings.MaxMessageBytes), $"is {settings.MaxMessageBytes}; it must be at most {nameof(DiscussionSettings.MaxTotalBytes)} ({settings.MaxTotalBytes}).");
        }

        var information = new List<string>();
        long perSession = ((long)settings.MaxMessagesPerSession * settings.MaxMessageBytes) + ((long)settings.MaxContextsPerSession * settings.MaxContextBytes);
        long worstCase = settings.MaxActiveSessions * perSession;
        if (worstCase > settings.MaxTotalBytes)
        {
            information.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{DiscussionSettings.SectionName}: {settings.MaxActiveSessions} full sessions would hold {worstCase} bytes, more than {nameof(DiscussionSettings.MaxTotalBytes)} ({settings.MaxTotalBytes}); the budget rejects posts before every session is full."));
        }

        return information;
    }

    /// <summary>
    /// The relations with limits of the host (the MCP server's response size and tool time limit), which this assembly does not know. The host calls it with its own values.
    /// </summary>
    /// <param name="maxResponseBytes">The host's largest tool result in bytes.</param>
    /// <param name="toolTimeoutSeconds">The host's time limit of a normal tool call.</param>
    /// <exception cref="DiscussionLimitsException">A read of the largest page would not fit the response limit, or a wait could outlast the tool time limit.</exception>
    public static void ValidateAgainstHost(DiscussionSettings settings, int maxResponseBytes, int toolTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        long readBytes = ((long)settings.MaxReadLimit * settings.MaxMessageBytes) + DiscussionLimits.ReadResponseAllowanceBytes;
        if (readBytes > maxResponseBytes)
        {
            throw Failure(
                nameof(DiscussionSettings.MaxReadLimit),
                $"is {settings.MaxReadLimit} with {nameof(DiscussionSettings.MaxMessageBytes)} {settings.MaxMessageBytes}; a full read page ({readBytes} bytes) must fit the response limit of {maxResponseBytes} bytes.");
        }

        if (settings.MaxWaitSeconds >= toolTimeoutSeconds)
        {
            throw Failure(
                nameof(DiscussionSettings.MaxWaitSeconds),
                $"is {settings.MaxWaitSeconds}; it must be less than the tool time limit of {toolTimeoutSeconds} seconds.");
        }
    }

    private static void Band(string name, int value, DiscussionBand band)
    {
        if (!band.Contains(value))
        {
            throw Failure(name, $"is {value}; it must be {band.Minimum} to {band.Maximum}.");
        }
    }

    private static void Band(string name, long value, long minimum, long maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw Failure(name, $"is {value}; it must be {minimum} to {maximum}.");
        }
    }

    private static DiscussionLimitsException Failure(string name, string detail)
        => new($"{DiscussionSettings.SectionName}:{name} {detail}");
}
