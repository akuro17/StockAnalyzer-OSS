using System.Collections.Concurrent;
using StockAnalyzer.McpServer.Protocol;

namespace StockAnalyzer.McpServer;

/// <summary>
/// The stdio transport: one JSON-RPC message per input line, one response per output line. Messages are handled in order; only a
/// tool call runs in the background, so a later ping or cancellation is read while it runs. When the input ends the host waits for
/// the calls still running (each is bounded by its time limit) and returns, which is how the client stops the server.
/// </summary>
internal static class McpServerHost
{
    private const string RequestTooLargeMessage = "Request too large.";

    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        McpMessageDispatcher dispatcher,
        int maxRequestBytes,
        LogSink log,
        CancellationToken cancellationToken)
    {
        var writer = new ResponseWriter(output);
        var reader = new RequestLineReader(input, maxRequestBytes);
        var running = new ConcurrentDictionary<Task, byte>();
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        log.Info("Started; waiting for messages on stdin.");

        try
        {
            while (!sessionCancellation.IsCancellationRequested)
            {
                RequestLine? next = await reader.ReadAsync(sessionCancellation.Token).ConfigureAwait(false);
                if (next is not { } line)
                {
                    break;
                }

                if (line.TooLong)
                {
                    log.Error($"A request line over {maxRequestBytes} bytes was rejected.");
                    await writer.WriteLineAsync(McpMessageDispatcher.RejectedLine(RequestTooLargeMessage)).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                DispatchOutcome outcome = dispatcher.Dispatch(line.Text, sessionCancellation.Token);
                if (outcome.Immediate is not null)
                {
                    await writer.WriteLineAsync(outcome.Immediate).ConfigureAwait(false);
                }

                if (outcome.Deferred is not null)
                {
                    Task finishing = FinishAsync(outcome.Deferred, writer, log);
                    running.TryAdd(finishing, 0);
                    _ = finishing.ContinueWith(done => running.TryRemove(done, out _), TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested; running calls observe the same token.
        }
        catch (IOException error)
        {
            log.Error($"Transport I/O ended: {error.Message}");
            sessionCancellation.Cancel();
        }
        catch (ObjectDisposedException error)
        {
            log.Error($"Transport stream closed: {error.Message}");
            sessionCancellation.Cancel();
        }

        await Task.WhenAll(running.Keys).ConfigureAwait(false);
        log.Info("Input closed; exiting.");
        return 0;
    }

    private static async Task FinishAsync(Task<string?> call, ResponseWriter writer, LogSink log)
    {
        try
        {
            string? response = await call.ConfigureAwait(false);
            if (response is not null)
            {
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            // The client is gone or the write failed: nothing more can be sent.
            log.Error($"A response could not be written: {error.Message}");
        }
    }
}
