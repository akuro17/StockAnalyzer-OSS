using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Execution;

/// <summary>
/// Runs tool calls one at a time. A later call waits up to <see cref="McpLimits.QueueWaitSeconds"/> for its turn, then fails
/// with SERVER_BUSY. Every call has a time limit and can be cancelled by the client. The turn is released when the tool's own
/// task ends, not when the caller stops waiting: a tool that cannot be cancelled (it keeps running after a timeout) keeps the
/// turn, so abandoned work never accumulates. Returns the MCP call result as JSON text, or null when the client cancelled.
/// </summary>
internal sealed class ToolExecutor
{
    private readonly McpLimits _limits;
    private readonly ErrorMessageSanitizer _sanitizer;
    private readonly LogSink _log;
    private readonly SemaphoreSlim _turn = new(McpLimitsContract.MaxConcurrentToolCalls, McpLimitsContract.MaxConcurrentToolCalls);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly IToolCallObserver? _observer;
    private int _observerFailureLogged;

    /// <param name="observer">Told once about every call that reaches the executor (see <see cref="IToolCallObserver"/>); null tells nobody and costs nothing.</param>
    public ToolExecutor(McpLimits limits, ErrorMessageSanitizer sanitizer, LogSink log, IToolCallObserver? observer = null)
    {
        _limits = limits;
        _sanitizer = sanitizer;
        _log = log;
        _observer = observer;
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
        try
        {
            try
            {
                ToolArgumentValidator.Validate(registered.Schema, arguments, registered.Tool.MisnamedArgumentHints);
            }
            catch (ToolException error)
            {
                return Fail(outcome, error);
            }

            // A tool that does not use the turn (a bounded wait for other participants) runs beside the other calls and never holds the turn they wait for.
            bool usesTurn = registered.Tool.UsesExecutionTurn;
            if (usesTurn)
            {
                bool acquired;
                try
                {
                    acquired = await _turn.WaitAsync(TimeSpan.FromSeconds(_limits.QueueWaitSeconds), callCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (!acquired)
                {
                    return Fail(outcome, new ToolException(ToolErrorCodes.ServerBusy, "Another tool call is still running.", hint: "Retry in a moment."));
                }
            }

            return await RunAsync(registered, arguments, callCancellation, usesTurn, outcome).ConfigureAwait(false);
        }
        finally
        {
            if (registeredForCancel)
            {
                _inFlight.TryRemove(requestKey, out _);
            }

            Notify(requestKey, registered, arguments, outcome, started);
        }
    }

    private async Task<string?> RunAsync(RegisteredTool registered, JsonElement arguments, CancellationTokenSource callCancellation, bool holdsTurn, CallOutcome outcome)
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
                if (holdsTurn)
                {
                    _turn.Release();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            ToolOutput output = await work.WaitAsync(execution.Token).ConfigureAwait(false);
            return SuccessResult(output, outcome);
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
            return Fail(outcome, error);
        }
        catch (DataUnavailableException)
        {
            return Fail(outcome, new ToolException(
                ToolErrorCodes.DataUnavailable,
                "The ticker list is temporarily unreadable.",
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

    private string SuccessResult(ToolOutput output, CallOutcome outcome)
    {
        string hint = output.Png is null ? "Request fewer items or a narrower window." : "Request a smaller image (width, height) or fewer bars (barCount).";
        if (output.Png is not null && EstimatedImageResultBytes(output.StructuredJson, output.Png.Length) > _limits.MaxResponseBytes)
        {
            // Refused before the large text is built: the Base64 form alone is about 4/3 of the PNG.
            return Fail(outcome, new ToolException(ToolErrorCodes.LimitExceeded, $"The result is larger than {_limits.MaxResponseBytes} bytes.", hint: hint));
        }

        string result = CallResult(output.StructuredJson, isError: false, output.Png);
        if (Encoding.UTF8.GetByteCount(result) > _limits.MaxResponseBytes)
        {
            return Fail(outcome, new ToolException(ToolErrorCodes.LimitExceeded, $"The result is larger than {_limits.MaxResponseBytes} bytes.", hint: hint));
        }

        outcome.Value = ToolCallOutcomes.Ok;
        return result;
    }

    /// <summary>
    /// A lower bound of the result's size in bytes: the JSON twice (text block and structuredContent) plus the Base64 form of the PNG (4 characters per 3 bytes).
    /// A result over the limit by this measure is over it by the real measure too, so the real text need not be built.
    /// </summary>
    private static long EstimatedImageResultBytes(string structuredJson, int pngLength)
        => (2L * Encoding.UTF8.GetByteCount(structuredJson)) + ((pngLength + 2L) / 3L * 4L);

    private string ErrorResult(ToolException error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("code", error.Code);
            writer.WriteString("message", _sanitizer.Sanitize(error.Message));
            if (error.Parameter is not null)
            {
                writer.WriteString("param", _sanitizer.Sanitize(error.Parameter));
            }

            if (error.Hint is not null)
            {
                writer.WriteString("hint", _sanitizer.Sanitize(error.Hint));
            }

            writer.WriteEndObject();
        }

        return CallResult(Encoding.UTF8.GetString(stream.ToArray()), isError: true);
    }

    /// <summary>
    /// The MCP call result: the structured JSON both as a text block and as structuredContent. A tool with an image adds one image block AFTER the text block,
    /// so <c>content[0]</c> stays the JSON text for every tool.
    /// </summary>
    private static string CallResult(string structuredJson, bool isError, byte[]? png = null)
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
            writer.WritePropertyName("structuredContent");
            writer.WriteRawValue(structuredJson);
            writer.WriteBoolean("isError", isError);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
