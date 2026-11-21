using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Execution;

/// <summary>
/// Runs tool calls under a reader/writer turn. A light read-only call (neither heavy nor execution) takes one of
/// <see cref="McpLimits.MaxConcurrentReadOnlyCalls"/> shared slots; a heavy or execution call takes the exclusive turn first and then every shared slot,
/// so it runs alone (with one slot the executor is strictly one call at a time). A later call waits up to <see cref="McpLimits.QueueWaitSeconds"/> for its
/// turn, then fails with SERVER_BUSY. Every call has a time limit and can be cancelled by the client. The turn is released when the tool's own
/// task ends, not when the caller stops waiting: a tool that cannot be cancelled (it keeps running after a timeout) keeps the
/// turn, so abandoned work never accumulates. Returns the MCP call result as JSON text, or null when the client cancelled.
/// </summary>
internal sealed class ToolExecutor
{
    private const string TruncatedSuffix = "...";
    private const int MinTruncatedFieldChars = 16;
    private const long BytesPerEstimatedToken = 4L;
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement;

    private readonly McpLimits _limits;
    private readonly ErrorMessageSanitizer _sanitizer;
    private readonly LogSink _log;
    private readonly int _readOnlySlots;
    private readonly SemaphoreSlim _readOnlyTurn;
    private readonly SemaphoreSlim _heavyTurn = new(McpLimitsContract.MaxConcurrentToolCalls, McpLimitsContract.MaxConcurrentToolCalls);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly IToolCallObserver? _observer;
    private readonly McpResponseFormat _responseFormat;
    private readonly McpToolRegistry? _registry;
    private readonly bool _progressiveDiscovery;
    private int _observerFailureLogged;

    internal McpResponseFormat ResponseFormat => _responseFormat;

    /// <param name="observer">Told once about every call that reaches the executor (see <see cref="IToolCallObserver"/>); null tells nobody and costs nothing.</param>
    /// <param name="responseFormat">Selects whether <c>structuredContent</c> is emitted next to <c>content[0].text</c> (<see cref="McpResponseFormat.Dual"/>) or omitted (<see cref="McpResponseFormat.TextOnly"/>).</param>
    /// <param name="registry">Optional tool registry used to unwrap <see cref="InvokeToolProxyTool"/> calls in progressive discovery mode.</param>
    /// <param name="progressiveDiscovery">When true, hidden non-listed callable tools emit progressive discovery argument hints instead of referencing <c>tools/list</c>.</param>
    public ToolExecutor(
        McpLimits limits,
        ErrorMessageSanitizer sanitizer,
        LogSink log,
        IToolCallObserver? observer = null,
        McpResponseFormat responseFormat = McpResponseFormat.Dual,
        McpToolRegistry? registry = null,
        bool progressiveDiscovery = false)
    {
        _limits = limits;
        _sanitizer = sanitizer;
        _log = log;
        _observer = observer;
        _responseFormat = responseFormat;
        _registry = registry;
        _progressiveDiscovery = progressiveDiscovery;
        _readOnlySlots = Math.Max(1, limits.MaxConcurrentReadOnlyCalls); // the start-up check keeps it inside 1..16; a semaphore cannot have no slot
        _readOnlyTurn = new SemaphoreSlim(_readOnlySlots, _readOnlySlots);
    }

    /// <summary>What a call holds of the turn, so that exactly that is given back when the tool's own task ends.</summary>
    private readonly record struct TurnHold(bool IsHeld, bool IsExclusive, int ReadOnlySlots)
    {
        public static TurnHold None { get; } = new(false, false, 0);
    }

    private enum TurnResult
    {
        Acquired,
        Busy,
        Cancelled,
    }

    /// <summary>How a call ended; starts as cancelled because a cancelled call is the one path that returns no result to name it.</summary>
    private sealed class CallOutcome
    {
        public string Value { get; set; } = ToolCallOutcomes.Cancelled;
    }

    /// <summary>Cancels the call with this request id (raw JSON text of the id), whether queued or running. Returns false when none is in flight.</summary>
    public bool Cancel(string requestKey)
    {
        if (!_inFlight.TryGetValue(requestKey, out CancellationTokenSource? source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public async Task<string?> ExecuteAsync(string requestKey, RegisteredTool registered, JsonElement arguments, CancellationToken shutdown)
    {
        long started = Stopwatch.GetTimestamp();
        var outcome = new CallOutcome();
        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        bool registeredForCancel = _inFlight.TryAdd(requestKey, callCancellation);
        RegisteredTool effectiveTool = registered;
        JsonElement effectiveArgs = arguments;
        try
        {
            string? outerHint = ShouldUseProgressiveHint(registered.Tool) ? ToolArgumentValidator.ProgressiveUnknownArgumentHint : null;
            try
            {
                ToolArgumentValidator.Validate(registered.Schema, arguments, registered.Tool.MisnamedArgumentHints, outerHint);
            }
            catch (ToolException error)
            {
                return Fail(outcome, error);
            }

            bool isProxiedCall = registered.Tool is InvokeToolProxyTool;
            if (registered.Tool is InvokeToolProxyTool proxy)
            {
                if (!TryResolveProxyTarget(proxy, arguments, out RegisteredTool? target, out JsonElement innerArgs, out ToolException? unwrapError))
                {
                    return Fail(outcome, unwrapError!);
                }

                // Target identity is committed before inner validation so observer attribution reflects the resolved target tool.
                effectiveTool = target!;
                effectiveArgs = innerArgs;
                try
                {
                    string? innerHint = _progressiveDiscovery ? ToolArgumentValidator.ProgressiveUnknownArgumentHint : null;
                    ToolArgumentValidator.Validate(effectiveTool.Schema, effectiveArgs, effectiveTool.Tool.MisnamedArgumentHints, innerHint);
                }
                catch (ToolException error)
                {
                    return Fail(outcome, PrefixProxyParameter(error, fallbackToArgumentsRoot: true));
                }
            }

            // A tool that does not use the turn (a bounded wait for other participants) runs beside the other calls and never holds the turn they wait for.
            TurnHold hold = TurnHold.None;
            if (effectiveTool.Tool.UsesExecutionTurn)
            {
                bool exclusive = effectiveTool.Tool.IsHeavy || effectiveTool.Tool.IsExecution;
                (TurnResult result, TurnHold acquired) = await AcquireTurnAsync(exclusive, callCancellation.Token).ConfigureAwait(false);
                if (result == TurnResult.Cancelled)
                {
                    return null;
                }

                if (result == TurnResult.Busy)
                {
                    return Fail(outcome, new ToolException(ToolErrorCodes.ServerBusy, "Another tool call is still running.", hint: "Retry in a moment."));
                }

                hold = acquired;
            }

            return await RunAsync(effectiveTool, effectiveArgs, callCancellation, hold, outcome, isProxiedCall).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error)
        {
            _log.Error($"Tool '{effectiveTool.Tool.Name}' failed before execution: {error}");
            return Fail(outcome, new ToolException(ToolErrorCodes.InternalError, ToolErrorCodes.UnexpectedErrorMessage));
        }
        finally
        {
            if (registeredForCancel)
            {
                _inFlight.TryRemove(requestKey, out _);
            }

            Notify(requestKey, effectiveTool, effectiveArgs, outcome, started);
        }
    }

    private bool ShouldUseProgressiveHint(IMcpTool tool)
        => _progressiveDiscovery
            && tool is not InvokeToolProxyTool and not SearchToolsTool
            && (_registry is null || !_registry.TryGet(tool.Name, out _));

    private bool TryResolveProxyTarget(
        InvokeToolProxyTool proxy,
        JsonElement arguments,
        out RegisteredTool? target,
        out JsonElement innerArguments,
        out ToolException? error)
    {
        target = null;
        innerArguments = EmptyArguments;

        string targetName = ToolArgs.String(arguments, "toolName") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            error = new ToolException(ToolErrorCodes.InvalidArgument, "Argument 'toolName' must not be empty.", "toolName");
            return false;
        }

        if (string.Equals(targetName, InvokeToolProxyTool.ToolName, StringComparison.Ordinal)
            || string.Equals(targetName, InvokeToolProxyTool.ActionToolName, StringComparison.Ordinal)
            || string.Equals(targetName, SearchToolsTool.ToolName, StringComparison.Ordinal))
        {
            error = new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Tool '{targetName}' cannot be invoked through {proxy.Name}.",
                "toolName");
            return false;
        }

        if (_registry is null || !_registry.TryGetCallable(targetName, out RegisteredTool? resolved))
        {
            error = new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Tool '{targetName}' is not available in this server configuration.",
                "toolName",
                "Call sa_search_tools to list available tools.");
            return false;
        }

        if (proxy.IsReadOnly && !resolved.Tool.IsReadOnly)
        {
            error = new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Tool '{targetName}' is not read-only and cannot be invoked through {proxy.Name}.",
                "toolName",
                $"Use {InvokeToolProxyTool.ActionToolName} to invoke state-changing or execution tools.");
            return false;
        }

        if (!proxy.IsReadOnly && resolved.Tool.IsReadOnly)
        {
            error = new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Tool '{targetName}' is read-only and must be invoked through {InvokeToolProxyTool.ToolName}.",
                "toolName",
                $"Use {InvokeToolProxyTool.ToolName} for read-only tools.");
            return false;
        }

        target = resolved;
        innerArguments = arguments.TryGetProperty("arguments", out JsonElement innerObj) ? innerObj : EmptyArguments;
        error = null;
        return true;
    }

    private static ToolException PrefixProxyParameter(ToolException error, bool fallbackToArgumentsRoot)
    {
        string? path = !string.IsNullOrEmpty(error.Parameter)
            ? "arguments." + error.Parameter
            : (fallbackToArgumentsRoot ? "arguments" : null);
        return path == error.Parameter
            ? error
            : new ToolException(error.Code, error.Message, path, error.Hint);
    }

    /// <summary>
    /// Takes the turn within <see cref="McpLimits.QueueWaitSeconds"/> in total. An exclusive call takes the exclusive turn and then every shared slot (so no light call runs
    /// beside it); a light call takes one shared slot. A call that cannot get all it needs gives back what it already took before it reports Busy or Cancelled.
    /// </summary>
    private async Task<(TurnResult Result, TurnHold Hold)> AcquireTurnAsync(bool exclusive, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        TimeSpan total = TimeSpan.FromSeconds(_limits.QueueWaitSeconds);
        TimeSpan Remaining()
        {
            TimeSpan left = total - Stopwatch.GetElapsedTime(started);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        int wanted = exclusive ? _readOnlySlots : 1;
        bool exclusiveHeld = false;
        int slotsHeld = 0;
        bool succeeded = false;
        try
        {
            if (exclusive)
            {
                if (!await _heavyTurn.WaitAsync(Remaining(), cancellationToken).ConfigureAwait(false))
                {
                    return (TurnResult.Busy, TurnHold.None);
                }

                exclusiveHeld = true;
            }

            while (slotsHeld < wanted)
            {
                if (!await _readOnlyTurn.WaitAsync(Remaining(), cancellationToken).ConfigureAwait(false))
                {
                    return (TurnResult.Busy, TurnHold.None);
                }

                slotsHeld++;
            }

            succeeded = true;
            return (TurnResult.Acquired, new TurnHold(true, exclusive, wanted));
        }
        catch (OperationCanceledException)
        {
            return (TurnResult.Cancelled, TurnHold.None);
        }
        finally
        {
            if (!succeeded)
            {
                if (slotsHeld > 0)
                {
                    _readOnlyTurn.Release(slotsHeld);
                }

                if (exclusiveHeld)
                {
                    _heavyTurn.Release();
                }
            }
        }
    }

    /// <summary>Gives back exactly what <see cref="AcquireTurnAsync"/> took; a call that took nothing (a tool without a turn) gives back nothing.</summary>
    private void ReleaseTurn(TurnHold hold)
    {
        if (!hold.IsHeld)
        {
            return;
        }

        _readOnlyTurn.Release(hold.ReadOnlySlots);
        if (hold.IsExclusive)
        {
            _heavyTurn.Release();
        }
    }

    private async Task<string?> RunAsync(RegisteredTool registered, JsonElement arguments, CancellationTokenSource callCancellation, TurnHold hold, CallOutcome outcome, bool isProxiedCall)
    {
        int timeoutSeconds = registered.Tool.IsHeavy ? _limits.HeavyToolTimeoutSeconds : _limits.ToolTimeoutSeconds;
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var execution = CancellationTokenSource.CreateLinkedTokenSource(callCancellation.Token, timeout.Token);

        Task<ToolOutput> work = Task.Run(() => InvokeToolAsync(registered.Tool, arguments, execution.Token));
        _ = work.ContinueWith(
            finished =>
            {
                _ = finished.Exception; // observed: an abandoned call must not raise an unobserved-task event
                execution.Dispose();
                timeout.Dispose();
                ReleaseTurn(hold);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            ToolOutput output = await work.WaitAsync(execution.Token).ConfigureAwait(false);
            return SuccessResult(registered.Tool, output, outcome);
        }
        catch (OperationCanceledException)
        {
            if (callCancellation.IsCancellationRequested)
            {
                return null;
            }

            return Fail(outcome, new ToolException(
                ToolErrorCodes.ExecutionTimeout,
                $"The tool did not finish within {timeoutSeconds} seconds.",
                hint: "Request less data or a narrower window."));
        }
        catch (ToolException error)
        {
            return Fail(outcome, isProxiedCall ? PrefixProxyParameter(error, fallbackToArgumentsRoot: false) : error);
        }
        catch (DataUnavailableException error)
        {
            string message = string.IsNullOrWhiteSpace(error.Message)
                ? "Stored data is temporarily unreadable."
                : error.Message;
            return Fail(outcome, new ToolException(
                ToolErrorCodes.DataUnavailable,
                message,
                hint: "Retry in a moment."));
        }
        catch (Exception error)
        {
            _log.Error($"Tool '{registered.Tool.Name}' failed: {error}");
            return Fail(outcome, new ToolException(ToolErrorCodes.InternalError, ToolErrorCodes.UnexpectedErrorMessage));
        }
    }

    /// <summary>Records the error code as the outcome of the call and builds the error result.</summary>
    private string Fail(CallOutcome outcome, ToolException error)
    {
        outcome.Value = error.Code;
        return ErrorResult(error);
    }

    /// <summary>Tells the observer about the finished call. Runs in a finally, so it never throws: an observer failure is logged once (the type only, never a message) and ignored.</summary>
    private void Notify(string requestKey, RegisteredTool registered, JsonElement arguments, CallOutcome outcome, long started)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<string> keys = Array.Empty<string>();
            int bytes = 0;
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                keys = arguments.EnumerateObject().Select(property => property.Name).ToArray();
                bytes = Encoding.UTF8.GetByteCount(arguments.GetRawText());
            }

            _observer.OnToolCall(new ToolCallRecord(registered.Tool.Name, requestKey, keys, bytes, outcome.Value, Stopwatch.GetElapsedTime(started)));
        }
        catch (Exception error)
        {
            if (Interlocked.Exchange(ref _observerFailureLogged, 1) == 0)
            {
                _log.Error($"A tool call observer failed ({error.GetType().Name}); later failures are not logged.");
            }
        }
    }

    /// <summary>What a tool returned: the structured JSON and, for an <see cref="IMcpImageTool"/>, the PNG bytes of its image block.</summary>
    private sealed record ToolOutput(string StructuredJson, byte[]? Png);

    private static async Task<ToolOutput> InvokeToolAsync(IMcpTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (tool is IMcpImageTool imageTool)
        {
            ToolImageResult image = await imageTool.InvokeImageAsync(arguments, cancellationToken).ConfigureAwait(false);
            return new ToolOutput(image.StructuredJson, image.Png);
        }

        return new ToolOutput(await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false), null);
    }

    private string SuccessResult(IMcpTool tool, ToolOutput output, CallOutcome outcome)
    {
        string hint = output.Png is null ? "Request fewer items or a narrower window." : "Request a smaller image (width, height) or fewer bars (barCount).";
        if (output.Png is not null && EstimatedImageResultBytes(output.StructuredJson, output.Png.Length, _responseFormat) > _limits.MaxResponseBytes)
        {
            // Refused before the large text is built: the Base64 form alone is about 4/3 of the PNG.
            return Fail(outcome, new ToolException(ToolErrorCodes.LimitExceeded, $"The result is larger than {_limits.MaxResponseBytes} bytes.", hint: hint));
        }

        if (output.Png is not null)
        {
            long multiplier = _responseFormat == McpResponseFormat.TextOnly ? 1L : 2L;
            long imageTextTokens = EstimateTokens(multiplier * Encoding.UTF8.GetByteCount(output.StructuredJson));
            long totalImageTokens = imageTextTokens + EstimateImageVisionTokens(output.Png);
            if (totalImageTokens > _limits.MaxResponseTokens)
            {
                return Fail(outcome, TokenLimitExceeded(totalImageTokens, tool));
            }
        }

        string result = CallResult(output.StructuredJson, isError: false, output.Png);
        int resultBytes = Encoding.UTF8.GetByteCount(result);
        if (resultBytes > _limits.MaxResponseBytes)
        {
            return Fail(outcome, new ToolException(ToolErrorCodes.LimitExceeded, $"The result is larger than {_limits.MaxResponseBytes} bytes.", hint: hint));
        }

        if (output.Png is null)
        {
            long estimatedTokens = EstimateTokens(resultBytes);
            if (estimatedTokens > _limits.MaxResponseTokens)
            {
                return Fail(outcome, TokenLimitExceeded(estimatedTokens, tool));
            }
        }

        outcome.Value = ToolCallOutcomes.Ok;
        return result;
    }

    private ToolException TokenLimitExceeded(long estimatedTokens, IMcpTool tool) => new(
        ToolErrorCodes.LimitExceeded,
        $"The result is estimated at {estimatedTokens} tokens, exceeding the limit of {_limits.MaxResponseTokens} tokens.",
        hint: tool.CompactUsageHint ?? "Request fewer items, fewer fields, or a narrower window.");

    /// <summary>
    /// Estimates vision-model tokens for an image block. Reads PNG IHDR dimensions when the 8-byte PNG signature,
    /// <c>IHDR</c> chunk type, and positive width/height are present; falls back to a byte-size ratio otherwise.
    /// </summary>
    internal static long EstimateImageVisionTokens(ReadOnlySpan<byte> png)
    {
        if (png.Length == 0)
        {
            return 0L;
        }

        if (png.Length >= 24
            && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47
            && png[4] == 0x0D && png[5] == 0x0A && png[6] == 0x1A && png[7] == 0x0A
            && png[12] == 0x49 && png[13] == 0x48 && png[14] == 0x44 && png[15] == 0x52)
        {
            uint width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
            if (width > 0 && height > 0)
            {
                ulong pixels = (ulong)width * height;
                return (long)Math.Max(1UL, (pixels + 749UL) / 750UL);
            }
        }

        return Math.Max(1L, ((long)png.Length + 29L) / 30L);
    }

    /// <summary>
    /// A lower bound of the result's size in bytes: the JSON once (text-only) or twice (text block and structuredContent) plus the Base64 form of the PNG (4 characters per 3 bytes).
    /// A result over the limit by this measure is over it by the real measure too, so the real text need not be built.
    /// </summary>
    private static long EstimatedImageResultBytes(string structuredJson, int pngLength, McpResponseFormat responseFormat)
    {
        long multiplier = responseFormat == McpResponseFormat.TextOnly ? 1L : 2L;
        return (multiplier * Encoding.UTF8.GetByteCount(structuredJson)) + ((pngLength + 2L) / 3L * 4L);
    }

    private static long EstimateTokens(long utf8Bytes)
        => (utf8Bytes + (BytesPerEstimatedToken - 1L)) / BytesPerEstimatedToken;

    private bool FitsResponseLimits(long utf8Bytes)
        => utf8Bytes <= _limits.MaxResponseBytes && EstimateTokens(utf8Bytes) <= _limits.MaxResponseTokens;

    private string ErrorResult(ToolException error)
    {
        string code = error.Code;
        string message = _sanitizer.Sanitize(error.Message);
        string? param = error.Parameter is null ? null : _sanitizer.Sanitize(error.Parameter);
        string? hint = error.Hint is null ? null : _sanitizer.Sanitize(error.Hint);

        if (FitsEnvelope(code, message, param, hint, out string fastEnvelope))
        {
            return fastEnvelope;
        }

        if (hint is not null)
        {
            if (hint.Length >= MinTruncatedFieldChars
                && TryFitByShrinkingField(hint, MinTruncatedFieldChars, h => BuildErrorEnvelope(code, message, param, h), out string fitted))
            {
                return fitted;
            }

            hint = null;
            if (FitsEnvelope(code, message, param, null, out string withoutHint))
            {
                return withoutHint;
            }
        }

        if (param is not null)
        {
            if (param.Length >= MinTruncatedFieldChars
                && TryFitByShrinkingField(param, MinTruncatedFieldChars, p => BuildErrorEnvelope(code, message, p, null), out string fitted))
            {
                return fitted;
            }

            param = null;
            if (FitsEnvelope(code, message, null, null, out string withoutParam))
            {
                return withoutParam;
            }
        }

        if (TryFitByShrinkingField(message, 0, m => BuildErrorEnvelope(code, m, null, null), out string finalEnvelope))
        {
            return finalEnvelope;
        }

        return BuildErrorEnvelope(code, string.Empty, null, null);
    }

    private bool FitsEnvelope(string code, string message, string? param, string? hint, out string envelope)
    {
        envelope = BuildErrorEnvelope(code, message, param, hint);
        return FitsResponseLimits(Encoding.UTF8.GetByteCount(envelope));
    }

    private bool TryFitByShrinkingField(
        string text,
        int minChars,
        Func<string, string> buildEnvelope,
        out string fittedEnvelope)
    {
        int effectiveMin = minChars;
        if (minChars > 0)
        {
            // Stepping back to avoid splitting a UTF-16 surrogate pair can shorten the output by 1 char;
            // advance until the truncated candidate still honors the minimum kept length.
            while (effectiveMin <= text.Length && TruncateSurrogateSafe(text, effectiveMin).Length < minChars)
            {
                effectiveMin++;
            }

            if (effectiveMin > text.Length)
            {
                fittedEnvelope = string.Empty;
                return false;
            }
        }

        string minCandidate = TruncateSurrogateSafe(text, effectiveMin);
        fittedEnvelope = buildEnvelope(minCandidate);
        if (!FitsResponseLimits(Encoding.UTF8.GetByteCount(fittedEnvelope)))
        {
            return false;
        }

        int lo = effectiveMin;
        int hi = text.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo + 1) / 2);
            string textCandidate = TruncateSurrogateSafe(text, mid);
            string envelopeCandidate = buildEnvelope(textCandidate);
            if (FitsResponseLimits(Encoding.UTF8.GetByteCount(envelopeCandidate)))
            {
                lo = mid;
                fittedEnvelope = envelopeCandidate;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return true;
    }

    private static string TruncateSurrogateSafe(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars <= TruncatedSuffix.Length)
        {
            int rawLen = maxChars;
            if (rawLen > 0 && char.IsHighSurrogate(text[rawLen - 1]))
            {
                rawLen--;
            }

            return text[..rawLen];
        }

        int keep = maxChars - TruncatedSuffix.Length;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep] + TruncatedSuffix;
    }

    private string BuildErrorEnvelope(string code, string message, string? param, string? hint)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            if (param is not null)
            {
                writer.WriteString("param", param);
            }

            if (hint is not null)
            {
                writer.WriteString("hint", hint);
            }

            writer.WriteEndObject();
        }

        return CallResult(Encoding.UTF8.GetString(stream.ToArray()), isError: true);
    }

    /// <summary>
    /// The MCP call result: the structured JSON as a text block and, in <see cref="McpResponseFormat.Dual"/> mode, as <c>structuredContent</c>.
    /// A tool with an image adds one image block AFTER the text block, so <c>content[0]</c> stays the JSON text for every tool.
    /// </summary>
    private string CallResult(string structuredJson, bool isError, byte[]? png = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", structuredJson);
            writer.WriteEndObject();
            if (png is not null)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteBase64String("data", png);
                writer.WriteString("mimeType", IMcpImageTool.PngMimeType);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (_responseFormat != McpResponseFormat.TextOnly)
            {
                writer.WritePropertyName("structuredContent");
                writer.WriteRawValue(structuredJson);
            }
            writer.WriteBoolean("isError", isError);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
