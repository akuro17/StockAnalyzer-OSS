using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// The error codes of the discussion board. The one definition: the MCP server's closed code set appends the seven board-specific codes by referencing these
/// constants, and the existing codes it shares (<see cref="InvalidArgument"/>, <see cref="LimitExceeded"/>, <see cref="InternalError"/>) keep their spelling.
/// </summary>
public static class DiscussionErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string InternalError = "INTERNAL_ERROR";
    public const string DiscussionNotFound = "DISCUSSION_NOT_FOUND";
    public const string DiscussionClosed = "DISCUSSION_CLOSED";
    public const string ParticipantNotJoined = "PARTICIPANT_NOT_JOINED";
    public const string NotYourTurn = "NOT_YOUR_TURN";
    public const string RateLimited = "RATE_LIMITED";
    public const string HubReset = "HUB_RESET";
    public const string HubUnavailable = "HUB_UNAVAILABLE";
}

/// <summary>A board operation was refused. Carries the error code, an English message without any path, token or message content, and optionally the argument and a hint.</summary>
public sealed class DiscussionException : Exception
{
    public DiscussionException(string code, string message, string? parameter = null, string? hint = null)
        : base(message)
    {
        Code = code;
        Parameter = parameter;
        Hint = hint;
    }

    public string Code { get; }

    public string? Parameter { get; }

    public string? Hint { get; }
}

public enum DiscussionTurnMode
{
    Free,
    RoundRobin,
}

public enum DiscussionSessionState
{
    Active,
    Closed,
}

public enum DiscussionCloseReason
{
    /// <summary>Closed by a participant (the wire name of this reason is not fixed by the specification; see the step log).</summary>
    Participant,
    Idle,
    MaxAge,
}

public enum DiscussionParticipantState
{
    Active,
    Away,
    Left,
}

public enum DiscussionMessageKind
{
    Message,
    Event,
    Analysis,
}

/// <summary>The names the board uses in its JSON results (spec 5.2, 5.7). One definition, used by the adapters.</summary>
public static class DiscussionNames
{
    public const string Free = "free";
    public const string RoundRobin = "roundRobin";

    public static string ToWire(DiscussionTurnMode mode) => mode == DiscussionTurnMode.RoundRobin ? RoundRobin : Free;

    public static bool TryParseTurnMode(string? text, out DiscussionTurnMode mode)
    {
        switch (text)
        {
            case Free:
                mode = DiscussionTurnMode.Free;
                return true;
            case RoundRobin:
                mode = DiscussionTurnMode.RoundRobin;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static string ToWire(DiscussionSessionState state) => state == DiscussionSessionState.Closed ? "closed" : "active";

    public static string? ToWire(DiscussionCloseReason? reason) => reason switch
    {
        DiscussionCloseReason.Idle => "idle",
        DiscussionCloseReason.MaxAge => "maxAge",
        DiscussionCloseReason.Participant => "participant",
        _ => null,
    };

    public static string ToWire(DiscussionParticipantState state) => state switch
    {
        DiscussionParticipantState.Away => "away",
        DiscussionParticipantState.Left => "left",
        _ => "active",
    };

    public static string ToWire(DiscussionMessageKind kind) => kind switch
    {
        DiscussionMessageKind.Event => "event",
        DiscussionMessageKind.Analysis => "analysis",
        _ => "message",
    };

    /// <summary>The origin label of a message: peers write <c>message</c>s, the server writes events and analysis notes.</summary>
    public static string OriginOf(DiscussionMessageKind kind) => kind == DiscussionMessageKind.Message ? "peer" : "server";

    /// <summary>UTC, ISO 8601 round-trip, ending in <c>Z</c> (spec 5.1).</summary>
    public static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

// ---- Results (immutable) ------------------------------------------------------------------------------------------------------------

/// <summary>The participant row of a session header.</summary>
public sealed record DiscussionParticipantInfo(string Handle, string DisplayName, DiscussionParticipantState State, long LastReadSequence);

/// <summary>Whose turn it is in round-robin mode. Always null in free mode.</summary>
public sealed record DiscussionFloorInfo(string Handle, int SecondsLeft);

/// <summary>The <c>session</c> header every result carries (spec 5.7).</summary>
public sealed record DiscussionSessionHeader(
    string Id,
    string Title,
    string? Topic,
    DiscussionSessionState State,
    DiscussionCloseReason? CloseReason,
    DiscussionTurnMode TurnMode,
    DiscussionFloorInfo? Floor,
    int MessageCount,
    int RemainingMessages,
    IReadOnlyList<DiscussionParticipantInfo> Participants,
    int ContextCount,
    string HubEpoch);

public sealed record DiscussionCreateResult(DiscussionSessionHeader Session, DateTimeOffset CreatedUtc);

public sealed record DiscussionJoinResult(string ParticipantToken, string Handle, bool Rejoined, DiscussionSessionHeader Session, long LastSequence, string Protocol);

public sealed record DiscussionLeaveResult(bool AlreadyLeft, DiscussionSessionHeader Session);

/// <summary>What a participant posts. <see cref="Content"/> is stored byte-exact and is required unless <see cref="Pass"/> is set.</summary>
public sealed record DiscussionPostRequest(
    string? Content,
    bool Pass = false,
    long? ReplyToSequence = null,
    IReadOnlyList<long>? ContextIds = null,
    long? BasedOnSequence = null,
    string? ClientRequestId = null);

public sealed record DiscussionPostResult(long Sequence, DateTimeOffset CreatedUtc, long NewerMessages, bool Duplicate, DiscussionFloorInfo? Floor);

/// <summary>One message as a reader sees it. <see cref="DisplayName"/> is the name at join time (an untrusted label); null for server events.</summary>
public sealed record DiscussionReadMessage(
    long Sequence,
    string? Handle,
    string? DisplayName,
    DiscussionMessageKind Kind,
    string Content,
    long? ReplyToSequence,
    IReadOnlyList<long> ContextIds,
    DateTimeOffset CreatedUtc);

public sealed record DiscussionReadResult(
    IReadOnlyList<DiscussionReadMessage> Messages,
    long NextAfterSequence,
    long LastSequence,
    bool HasMore,
    bool TimedOut,
    DiscussionSessionHeader Session);

public sealed record DiscussionAttachResult(long ContextId, bool Reused, string Sha256, int ResultBytes, long Sequence);

public sealed record DiscussionAnalysisResult(
    long ContextId,
    string Tool,
    string ArgumentsJson,
    string Sha256,
    string AttachedByHandle,
    string? Note,
    DateTimeOffset CreatedUtc,
    int ResultBytes,
    string ResultJson);

public sealed record DiscussionCloseResult(bool AlreadyClosed, DiscussionSessionState State, DiscussionCloseReason? CloseReason, int MessageCount, DiscussionSessionHeader Session);

/// <summary>One line of the hub audit ring (spec 5.11). Never content, tokens, display names or results.</summary>
public sealed record DiscussionAuditEntry(long N, DateTimeOffset Utc, string Event, string? SessionId, string? Handle, long? Sequence, string? Code, string? Detail);

public sealed record DiscussionStatusResult(
    string HubEpoch,
    IReadOnlyList<DiscussionSessionHeader> Sessions,
    IReadOnlyList<DiscussionAuditEntry> Audit,
    DateTimeOffset ServerTimeUtc);

/// <summary>Counters for tests and the status: all of them must return to zero when every session is gone.</summary>
public sealed record DiscussionDiagnostics(long TotalBytes, long RecomputedBytes, int SessionCount, int WaiterCount);
