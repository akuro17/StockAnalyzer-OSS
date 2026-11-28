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

    /// <summary>The <c>headerMode</c> values of a read: the whole session header, only the changing part of it, or none.</summary>
    public const string HeaderFull = "full";

    public const string HeaderMinimal = "minimal";
    public const string HeaderNone = "none";

    /// <summary>The <c>view</c> values of a read: every field of a message, or only what is needed to survey the discussion.</summary>
    public const string ViewFull = "full";

    public const string ViewSummary = "summary";

    /// <summary>The <c>Parameter</c> of the LIMIT_EXCEEDED refusal that a full set of waiting readers causes.</summary>
    public const string WaitersParameter = "waiters";

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

/// <summary>
/// Canonical JSON argument keys accepted by the nine MCP discussion tools and dispatched through <see cref="IDiscussionGateway"/>.
/// Covers the 32 top-level public tool input arguments plus the internal <c>attach_commit</c> wire field <see cref="Result"/>.
/// Nested <c>interpretation</c> subfields remain outside this top-level argument registry.
/// </summary>
public static class DiscussionArgumentNames
{
    public const string SessionId = "sessionId";
    public const string ParticipantToken = "participantToken";
    public const string Title = "title";
    public const string Topic = "topic";
    public const string TurnMode = "turnMode";
    public const string DisplayName = "displayName";
    public const string Content = "content";
    public const string Pass = "pass";
    public const string ReplyToSequence = "replyToSequence";
    public const string ContextIds = "contextIds";
    public const string BasedOnSequence = "basedOnSequence";
    public const string ClientRequestId = "clientRequestId";
    public const string Interpretation = "interpretation";
    public const string AfterSequence = "afterSequence";
    public const string Limit = "limit";
    public const string WaitSeconds = "waitSeconds";
    public const string Compact = "compact";
    public const string HeaderMode = "headerMode";
    public const string ExcludeEvents = "excludeEvents";
    public const string ExcludeOwn = "excludeOwn";
    public const string Tail = "tail";
    public const string View = "view";
    public const string WaitForTurn = "waitForTurn";
    public const string ContextId = "contextId";
    public const string IncludeResult = "includeResult";
    public const string Paths = "paths";
    public const string AuditTail = "auditTail";
    public const string CommandTail = "commandTail";
    public const string IncludeContexts = "includeContexts";
    public const string Tool = "tool";
    public const string Arguments = "arguments";
    public const string AnalysisIntent = "analysisIntent";
    public const string Note = "note";

    /// <summary>Internal wire-only payload key sent by <c>attach_commit</c>; not a public MCP input schema argument.</summary>
    public const string Result = "result";
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

/// <summary>
/// Hard bounds for AI analysis provenance and structured peer interpretation.
/// Kept separate from <see cref="DiscussionLimits"/> to preserve reflection parity tests.
/// </summary>
public static class DiscussionEvidenceLimits
{
    public const int MaxAnalysisIntentChars = 128;
    public const int MaxInterpretationSummaryChars = 500;
    public const int MaxInterpretationConclusionChars = 1_000;
    public const int MaxInterpretationListItems = 16;
    public const int MaxInterpretationItemChars = 500;
}

/// <summary>
/// Hard bounds of the <c>paths</c> argument of <c>sa_discussion_get_analysis</c> (picking fields out of a stored result). Kept apart from <see cref="DiscussionLimits"/>
/// like <see cref="DiscussionEvidenceLimits"/>: they are fixed values of the board, not settings.
/// </summary>
public static class DiscussionExtensionLimits
{
    public const int MaxJsonExtractPaths = 16;
    public const int MaxJsonExtractPathChars = 128;
    public const int MaxJsonExtractPathSegments = 16;

    /// <summary>The most digits of an array index in a path.</summary>
    public const int MaxJsonExtractIndexDigits = 6;

    /// <summary>The largest array index of a path: the largest number of <see cref="MaxJsonExtractIndexDigits"/> digits (the two constants move together).</summary>
    public const int MaxJsonExtractIndex = 999999;

    /// <summary>The content of a message is cut to this many UTF-16 characters in the summary view of a read (a summary given by the writer is never cut).</summary>
    public const int SummaryViewContentChars = 240;
}

/// <summary>
/// Canonical market direction hypotheses for structured peer interpretation.
/// </summary>
public static class DiscussionInterpretationDirection
{
    public const string Bullish = "Bullish";
    public const string Bearish = "Bearish";
    public const string Neutral = "Neutral";
}

/// <summary>
/// Structured peer interpretation of an analysis or discussion turn.
/// Supports partial input (at least one subfield populated) and tracks which fields were provided.
/// </summary>
public sealed record DiscussionInterpretation(
    string? Summary = null,
    string? Direction = null,
    IReadOnlyList<string>? Findings = null,
    IReadOnlyList<string>? SupportingEvidence = null,
    IReadOnlyList<string>? CounterEvidence = null,
    string? Conclusion = null,
    IReadOnlyList<string>? ProvidedFields = null);

/// <summary>What a participant posts. <see cref="Content"/> is stored byte-exact and is required unless <see cref="Pass"/> is set.</summary>
public sealed record DiscussionPostRequest(
    string? Content,
    bool Pass = false,
    long? ReplyToSequence = null,
    IReadOnlyList<long>? ContextIds = null,
    long? BasedOnSequence = null,
    string? ClientRequestId = null,
    DiscussionInterpretation? Interpretation = null);

public sealed record DiscussionPostResult(
    long Sequence,
    DateTimeOffset CreatedUtc,
    long NewerMessages,
    bool Duplicate,
    DiscussionFloorInfo? Floor,
    bool HasStructuredInput = false,
    bool HasInterpretation = false,
    IReadOnlyList<string>? ProvidedInterpretationFields = null);

/// <summary>One message as a reader sees it. <see cref="DisplayName"/> is the name at join time (an untrusted label); null for server events.</summary>
public sealed record DiscussionReadMessage(
    long Sequence,
    string? Handle,
    string? DisplayName,
    DiscussionMessageKind Kind,
    string Content,
    long? ReplyToSequence,
    IReadOnlyList<long> ContextIds,
    DateTimeOffset CreatedUtc,
    bool HasStructuredInput = false,
    bool HasInterpretation = false,
    DiscussionInterpretation? Interpretation = null);

/// <summary>
/// The optional shaping of a read: leave out the server events, leave out the caller's own messages, read only the newest messages, or wait until the caller holds the turn in round-robin mode.
/// A read without options (or with the defaults) takes the unchanged path of the board.
/// </summary>
/// <param name="ExcludeEvents">True leaves out the messages of kind <see cref="DiscussionMessageKind.Event"/> (joined, left, passed, floor passed, closed); peer messages and analysis entries stay.</param>
/// <param name="Tail">When set, the newest messages that pass the filter (1 to the read limit of the board), in ascending order; it replaces <c>limit</c> and cannot be combined with an after-sequence, a wait or <c>WaitForTurn</c>.</param>
/// <param name="WaitForTurn">True waits until the caller holds the floor in round-robin mode (or the session closes), returning all filtered unread messages accumulated when the turn arrives or on timeout.</param>
/// <param name="ExcludeOwn">True leaves out the caller's own records of kind message and analysis (by handle, so a re-joined participant's earlier records too); events stay unless <paramref name="ExcludeEvents"/> removes them. The cursor still moves past the records left out, and a wait ends only when a record that passes arrives.</param>
public sealed record DiscussionReadOptions(bool ExcludeEvents = false, int? Tail = null, bool WaitForTurn = false, bool ExcludeOwn = false)
{
    /// <summary>True when the options change the read; false for the defaults.</summary>
    public bool IsActive => ExcludeEvents || Tail is not null || WaitForTurn || ExcludeOwn;
}

public sealed record DiscussionReadResult(
    IReadOnlyList<DiscussionReadMessage> Messages,
    long NextAfterSequence,
    long LastSequence,
    bool HasMore,
    bool TimedOut,
    DiscussionSessionHeader Session);

public sealed record DiscussionAttachResult(
    long ContextId,
    bool Reused,
    string Sha256,
    int ResultBytes,
    long Sequence,
    bool HasAnalysisIntent = false,
    string? AnalysisIntent = null,
    string? ProvenanceJson = null);

public sealed record DiscussionAnalysisResult(
    long ContextId,
    string Tool,
    string ArgumentsJson,
    string Sha256,
    string AttachedByHandle,
    string? Note,
    DateTimeOffset CreatedUtc,
    int ResultBytes,
    string ResultJson,
    bool HasAnalysisIntent = false,
    string? AnalysisIntent = null,
    string? ProvenanceJson = null);

public sealed record DiscussionCloseResult(bool AlreadyClosed, DiscussionSessionState State, DiscussionCloseReason? CloseReason, int MessageCount, DiscussionSessionHeader Session);

/// <summary>One line of the hub audit ring (spec 5.11). Never content, tokens, display names or results.</summary>
public sealed record DiscussionAuditEntry(long N, DateTimeOffset Utc, string Event, string? SessionId, string? Handle, long? Sequence, string? Code, string? Detail);

/// <summary>One attached analysis context as the status lists it: what it is and where it came from, never its result, its arguments, its note or its provenance.</summary>
public sealed record DiscussionContextInfo(
    long ContextId,
    string Tool,
    string AttachedBy,
    string? AnalysisIntent,
    int ResultBytes,
    string Sha256,
    long Sequence,
    DateTimeOffset CreatedUtc);

/// <param name="Contexts">The contexts of the one session that was asked for with <c>includeContexts</c>; null when they were not asked for.</param>
public sealed record DiscussionStatusResult(
    string HubEpoch,
    IReadOnlyList<DiscussionSessionHeader> Sessions,
    IReadOnlyList<DiscussionAuditEntry> Audit,
    DateTimeOffset ServerTimeUtc,
    IReadOnlyList<DiscussionContextInfo>? Contexts = null);

/// <summary>Counters for tests and the status: all of them must return to zero when every session is gone.</summary>
public sealed record DiscussionDiagnostics(long TotalBytes, long RecomputedBytes, int SessionCount, int WaiterCount);
