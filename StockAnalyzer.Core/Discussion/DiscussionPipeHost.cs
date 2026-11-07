using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// The listening side of the board (spec 5.12): the process that won the election serves the other servers of its user over a named pipe. One request line and one reply
/// line per connection. The pipe is current-user only; the requests are checked by the gateway exactly like in-process calls.
/// <para>
/// <see cref="TryClaim"/> creates the FIRST instance of the pipe name (<c>FirstPipeInstance</c>): on Windows exactly one process can do that, which is what makes the
/// election safe. The accept loop starts only after the board exists (<see cref="Start"/>) and always keeps one listening instance: the next one is created before the
/// accepted connection is served.
/// </para>
/// </summary>
internal sealed class DiscussionPipeHost : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly HashSet<NamedPipeServerStream> _open = new();
    private NamedPipeServerStream? _first;
    private IDiscussionGateway? _gateway;
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    private DiscussionPipeHost(string pipeName, NamedPipeServerStream first)
    {
        _pipeName = pipeName;
        _first = first;
    }

    /// <summary>
    /// Tries to create the first instance of the pipe name. Null means another process owns the name: that is not an error, the caller becomes a client.
    /// </summary>
    public static DiscussionPipeHost? TryClaim(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        try
        {
            return new DiscussionPipeHost(pipeName, NewInstance(pipeName, first: true));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static NamedPipeServerStream NewInstance(string pipeName, bool first)
    {
        PipeOptions options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (first)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, DiscussionLimits.MaxHubConnections, PipeTransmissionMode.Byte, options);
    }

    /// <summary>Starts serving. Called once, after the board exists.</summary>
    public void Start(IDiscussionGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        NamedPipeServerStream first = _first ?? throw new InvalidOperationException("The host has already been started.");
        _first = null;
        _gateway = gateway;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first));
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream listening)
    {
        CancellationToken stop = _stop.Token;
        NamedPipeServerStream? next = listening;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream current = next ?? await WaitForInstanceAsync(stop).ConfigureAwait(false);
                next = null;
                try
                {
                    await current.WaitForConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The client went away while it was being accepted: take the instance down and listen again.
                    current.Dispose();
                    continue;
                }
                catch
                {
                    current.Dispose();
                    throw;
                }

                if (!Track(current))
                {
                    current.Dispose();
                    break;
                }

                // The next listening instance exists before the accepted connection is served; when all instances are busy it is created after (the caller waits for it).
                next = TryNewInstance();
                _ = ServeAsync(current);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            next?.Dispose();
        }
    }

    private NamedPipeServerStream? TryNewInstance()
    {
        try
        {
            return NewInstance(_pipeName, first: false);
        }
        catch (IOException)
        {
            return null; // every instance is serving a connection
        }
    }

    private async Task<NamedPipeServerStream> WaitForInstanceAsync(CancellationToken stop)
    {
        while (true)
        {
            NamedPipeServerStream? instance = TryNewInstance();
            if (instance is not null)
            {
                return instance;
            }

            await Task.Delay(DiscussionLimits.AcceptRetryMs, stop).ConfigureAwait(false);
        }
    }

    private bool Track(NamedPipeServerStream stream)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            _open.Add(stream);
            return true;
        }
    }

    private void Untrack(NamedPipeServerStream stream)
    {
        lock (_sync)
        {
            _open.Remove(stream);
        }
    }

    /// <summary>Reads one request line, runs it on the board and writes one reply line. Never throws: a broken connection is simply closed.</summary>
    private async Task ServeAsync(NamedPipeServerStream stream)
    {
        try
        {
            using (stream)
            {
                CancellationToken stop = _stop.Token;
                byte[]? line;
                try
                {
                    using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    readLimit.CancelAfter(DiscussionLimits.WireRequestTimeoutMs);
                    line = await DiscussionWire.ReadLineAsync(stream, DiscussionLimits.WireMaxLineBytes, readLimit.Token).ConfigureAwait(false);
                }
                catch (DiscussionWireException error) when (error.Problem == DiscussionWireProblem.TooLong)
                {
                    await WriteReplyAsync(stream, DiscussionReply.Fail(DiscussionErrorCodes.LimitExceeded, "The request is larger than the link between the servers allows.", "arguments"), stop).ConfigureAwait(false);
                    return;
                }
                catch (Exception error) when (error is DiscussionWireException or OperationCanceledException or IOException)
                {
                    return; // nothing sent in time, or the peer left in the middle of a line
                }

                if (line is null)
                {
                    return; // a probe: connected and closed
                }

                (DiscussionReply reply, Task<int>? watch) = await RunAsync(stream, line, stop).ConfigureAwait(false);
                await WriteReplyAsync(stream, reply, stop).ConfigureAwait(false);
                if (watch is not null)
                {
                    await WaitForPeerToCloseAsync(watch, stop).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // A failing connection never reaches the process; the peer sees a closed pipe.
        }
        finally
        {
            Untrack(stream);
        }
    }

    /// <summary>
    /// Runs the request. Besides the reply it returns the read that watched the connection while the board worked (null when the request never reached the board): the
    /// pipe allows one outstanding read, so the wait for the peer's close after the reply uses the same one.
    /// </summary>
    private async Task<(DiscussionReply Reply, Task<int>? Watch)> RunAsync(NamedPipeServerStream stream, byte[] line, CancellationToken stop)
    {
        DiscussionWire.WireRequest request;
        try
        {
            request = DiscussionWire.ParseRequest(line);
        }
        catch (DiscussionWireException error) when (error.Problem == DiscussionWireProblem.VersionMismatch)
        {
            return (DiscussionReply.Fail(DiscussionErrorCodes.HubUnavailable, "The hub is a different server version.", hint: DiscussionRouterTexts.DifferentVersionHint), null);
        }
        catch (DiscussionWireException)
        {
            return (DiscussionReply.Fail(DiscussionErrorCodes.InvalidArgument, "The request is not a valid line of the link between the servers."), null);
        }

        if (!DiscussionOperations.IsWireOperation(request.Operation))
        {
            return (DiscussionReply.Fail(DiscussionErrorCodes.InvalidArgument, "The operation is not known.", "operation"), null);
        }

        // While the board works (a read may wait), the pipe is watched: a peer that closes its end cancels the work instead of leaving a waiter behind.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stop);
        byte[] watchBuffer = new byte[1];
        Task<int> watch = stream.ReadAsync(watchBuffer.AsMemory(), stop).AsTask();
        _ = watch.ContinueWith(
            finished =>
            {
                _ = finished.Exception; // observed: a read that ends because the pipe was closed is not an error
                try
                {
                    work.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            DiscussionReply reply = await _gateway!.ExecuteAsync(new DiscussionRequest(request.Operation, request.Caller, request.ArgumentsJson), work.Token).ConfigureAwait(false);
            return (reply, watch);
        }
        catch (OperationCanceledException)
        {
            return (DiscussionReply.Fail(DiscussionErrorCodes.HubUnavailable, "The request was cancelled."), watch);
        }
        catch (Exception)
        {
            return (DiscussionReply.Fail(DiscussionErrorCodes.InternalError, "The board failed to answer."), watch);
        }
    }

    private static async Task WriteReplyAsync(Stream stream, DiscussionReply reply, CancellationToken cancellationToken)
    {
        byte[] bytes = DiscussionWire.EncodeReply(reply);
        if (bytes.Length - 1 > DiscussionLimits.WireMaxLineBytes)
        {
            bytes = DiscussionWire.EncodeReply(DiscussionReply.Fail(
                DiscussionErrorCodes.LimitExceeded, "The result is larger than the link between the servers allows.", hint: "Ask for less, for example a smaller limit."));
        }

        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The peer closes its end after it has read the reply; closing first could discard what it has not read yet.</summary>
    private static async Task WaitForPeerToCloseAsync(Task<int> watch, CancellationToken stop)
    {
        try
        {
            await watch.WaitAsync(TimeSpan.FromMilliseconds(DiscussionLimits.WireDrainTimeoutMs), stop).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException)
        {
        }
    }

    /// <summary>Stops accepting, closes every open connection and waits (bounded) for the accept loop.</summary>
    public void Dispose()
    {
        NamedPipeServerStream[] open;
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            open = new NamedPipeServerStream[_open.Count];
            _open.CopyTo(open);
            _open.Clear();
        }

        _stop.Cancel();
        _first?.Dispose();
        foreach (NamedPipeServerStream stream in open)
        {
            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
            }
        }

        try
        {
            _acceptLoop.Wait(DiscussionLimits.HubShutdownWaitMs);
        }
        catch (AggregateException)
        {
        }
    }
}
