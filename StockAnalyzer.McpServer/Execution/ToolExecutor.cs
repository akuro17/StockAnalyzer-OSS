using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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

    public ToolExecutor(McpLimits limits, ErrorMessageSanitizer sanitizer, LogSink log)
    {
        _limits = limits;
        _sanitizer = sanitizer;
        _log = log;
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
        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        bool registeredForCancel = _inFlight.TryAdd(requestKey, callCancellation);
        try
        {
            try
            {
                ToolArgumentValidator.Validate(registered.Schema, arguments);
            }
            catch (ToolException error)
            {
                return ErrorResult(error);
            }

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
                return ErrorResult(new ToolException(ToolErrorCodes.ServerBusy, "Another tool call is still running.", hint: "Retry in a moment."));
            }

            return await RunAsync(registered, arguments, callCancellation).ConfigureAwait(false);
        }
        finally
        {
            if (registeredForCancel)
            {
                _inFlight.TryRemove(requestKey, out _);
            }
        }
    }

    private async Task<string?> RunAsync(RegisteredTool registered, JsonElement arguments, CancellationTokenSource callCancellation)
    {
        int timeoutSeconds = registered.Tool.IsHeavy ? _limits.HeavyToolTimeoutSeconds : _limits.ToolTimeoutSeconds;
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var execution = CancellationTokenSource.CreateLinkedTokenSource(callCancellation.Token, timeout.Token);

        Task<string> work = Task.Run(() => registered.Tool.InvokeAsync(arguments, execution.Token));
        _ = work.ContinueWith(
            finished =>
            {
                _ = finished.Exception; // observed: an abandoned call must not raise an unobserved-task event
                execution.Dispose();
                timeout.Dispose();
                _turn.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            string structured = await work.WaitAsync(execution.Token).ConfigureAwait(false);
            return SuccessResult(structured);
        }
        catch (OperationCanceledException)
        {
            if (callCancellation.IsCancellationRequested)
            {
                return null;
            }

            return ErrorResult(new ToolException(
                ToolErrorCodes.ExecutionTimeout,
                $"The tool did not finish within {timeoutSeconds} seconds.",
                hint: "Request less data or a narrower window."));
        }
        catch (ToolException error)
        {
            return ErrorResult(error);
        }
        catch (Exception error)
        {
            _log.Error($"Tool '{registered.Tool.Name}' failed: {error}");
            return ErrorResult(new ToolException(ToolErrorCodes.InternalError, ToolErrorCodes.UnexpectedErrorMessage));
        }
    }

    private string SuccessResult(string structuredJson)
    {
        string result = CallResult(structuredJson, isError: false);
        if (Encoding.UTF8.GetByteCount(result) > _limits.MaxResponseBytes)
        {
            return ErrorResult(new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"The result is larger than {_limits.MaxResponseBytes} bytes.",
                hint: "Request fewer items or a narrower window."));
        }

        return result;
    }

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

    /// <summary>The MCP call result: the structured JSON both as a text block and as structuredContent.</summary>
    private static string CallResult(string structuredJson, bool isError)
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
            writer.WriteEndArray();
            writer.WritePropertyName("structuredContent");
            writer.WriteRawValue(structuredJson);
            writer.WriteBoolean("isError", isError);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
