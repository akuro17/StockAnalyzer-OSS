using StockAnalyzer.Core.Discussion;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>The closed set of tool error codes (language independent). Tool failures are results with isError, never JSON-RPC errors.</summary>
internal static class ToolErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string SymbolNotFound = "SYMBOL_NOT_FOUND";
    public const string UnsupportedTimeFrame = "UNSUPPORTED_TIMEFRAME";
    public const string UnknownIndicator = "UNKNOWN_INDICATOR";
    public const string InvalidIndicatorParameter = "INVALID_INDICATOR_PARAMETER";
    public const string InvalidOutputSeries = "INVALID_OUTPUT_SERIES";
    public const string ConditionLimitExceeded = "CONDITION_LIMIT_EXCEEDED";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string InsufficientMarketData = "INSUFFICIENT_MARKET_DATA";
    public const string TemplateNotFound = "TEMPLATE_NOT_FOUND";
    public const string TemplateMigrationPending = "TEMPLATE_MIGRATION_PENDING";
    public const string ExecutionTimeout = "EXECUTION_TIMEOUT";
    public const string ServerBusy = "SERVER_BUSY";
    public const string ForbiddenOperation = "FORBIDDEN_OPERATION";
    public const string DataUnavailable = "DATA_UNAVAILABLE";
    public const string GroupNotFound = "GROUP_NOT_FOUND";
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>A backtest run overflowed the engine's decimal arithmetic (an absurdly large capital, quantity or price); owner decision D-8 = C, 2026-11-04.</summary>
    public const string BacktestArithmeticOverflow = "BACKTEST_ARITHMETIC_OVERFLOW";

    // Codes of the execution tools (offered only with --enable-execution).
    public const string ExecutionBusy = "EXECUTION_BUSY";
    public const string JobNotFound = "JOB_NOT_FOUND";
    public const string ArtifactNotFound = "ARTIFACT_NOT_FOUND";
    public const string PythonEnvMissing = "PYTHON_ENV_MISSING";
    public const string SandboxPathTooLong = "SANDBOX_PATH_TOO_LONG";

    // Codes of the discussion board (offered only with --enable-discussion). The one definition of the spelling is Core's DiscussionErrorCodes.
    public const string DiscussionNotFound = DiscussionErrorCodes.DiscussionNotFound;
    public const string DiscussionClosed = DiscussionErrorCodes.DiscussionClosed;
    public const string ParticipantNotJoined = DiscussionErrorCodes.ParticipantNotJoined;
    public const string NotYourTurn = DiscussionErrorCodes.NotYourTurn;
    public const string RateLimited = DiscussionErrorCodes.RateLimited;
    public const string HubReset = DiscussionErrorCodes.HubReset;
    public const string HubUnavailable = DiscussionErrorCodes.HubUnavailable;

    /// <summary>The only message an unmapped exception produces; details go to the log only.</summary>
    public const string UnexpectedErrorMessage = "Unexpected error. See server log.";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        InvalidArgument, SymbolNotFound, UnsupportedTimeFrame, UnknownIndicator, InvalidIndicatorParameter, InvalidOutputSeries,
        ConditionLimitExceeded, LimitExceeded, InsufficientMarketData, TemplateNotFound, TemplateMigrationPending, ExecutionTimeout,
        ServerBusy, ForbiddenOperation, DataUnavailable, GroupNotFound, InternalError, ExecutionBusy, JobNotFound, ArtifactNotFound, PythonEnvMissing,
        SandboxPathTooLong, BacktestArithmeticOverflow,
        DiscussionNotFound, DiscussionClosed, ParticipantNotJoined, NotYourTurn, RateLimited, HubReset, HubUnavailable,
    };
}
