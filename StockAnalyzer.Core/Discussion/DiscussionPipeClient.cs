using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// The calling side of the link (spec 5.12): one connection per call, one request line, one reply line. Every wait is bounded: the connect by
/// <see cref="DiscussionLimits.PipeConnectTimeoutMs"/>, the rest by the token of the tool call.
/// </summary>
internal static class DiscussionPipeClient
{
    private static NamedPipeClientStream NewClient(string pipeName)
        => new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>True when a board answers on the pipe (connected and closed again). False when nothing is listening within the connect time.</summary>
    public static async Task<bool> ProbeAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            using NamedPipeClientStream client = NewClient(pipeName);
            await client.ConnectAsync(DiscussionLimits.PipeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends the request and returns the reply. A request larger than the link allows is answered here with LIMIT_EXCEEDED.
    /// </summary>
    /// <exception cref="TimeoutException">Nothing accepted the connection in time.</exception>
    /// <exception cref="IOException">The pipe broke or the peer closed it without a reply.</exception>
    /// <exception cref="DiscussionWireException">The reply is not a valid line of the wire, or of another version.</exception>
    public static async Task<DiscussionReply> CallAsync(string pipeName, DiscussionRequest request, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = DiscussionWire.EncodeRequest(request);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return DiscussionReply.Fail(DiscussionErrorCodes.InvalidArgument, "The arguments are not valid JSON.", "arguments");
        }

        if (bytes.Length - 1 > DiscussionLimits.WireMaxLineBytes)
        {
            return DiscussionReply.Fail(
                DiscussionErrorCodes.LimitExceeded, "The request is larger than the link between the servers allows.", "arguments", "Send less, for example a shorter note or a smaller result.");
        }

        using NamedPipeClientStream client = NewClient(pipeName);
        await client.ConnectAsync(DiscussionLimits.PipeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        await client.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);

        byte[] line = await DiscussionWire.ReadLineAsync(client, DiscussionLimits.WireMaxLineBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The hub closed the pipe without a reply.");
        return DiscussionWire.ParseReply(line);
    }
}
