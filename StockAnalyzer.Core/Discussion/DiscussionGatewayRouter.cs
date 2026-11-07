using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Discussion;

/// <summary>Texts the router and the hub both put in front of the owner.</summary>
internal static class DiscussionRouterTexts
{
    public const string DifferentVersionHint = "The hub is a different server version; restart all clients.";
    public const string RestartHint = "Restart the MCP server, or all clients, and start the discussion again.";
}

/// <summary>Switches that exist for tests only; production code never sets either.</summary>
/// <param name="PipeNameSuffix">Appended to the pipe name, so that a test never meets a real board.</param>
/// <param name="EnableSharing">False keeps the board in this process alone (the behaviour of every platform other than Windows).</param>
internal sealed record DiscussionTransportOptions(string? PipeNameSuffix = null, bool EnableSharing = true)
{
    /// <summary>The pipe hub is guaranteed on Windows only (decision D-12); elsewhere the board is local and nothing claims sharing.</summary>
    public bool SharingAvailable => EnableSharing && OperatingSystem.IsWindows();
}

/// <summary>
/// What the discussion tools talk to when the board may be shared (spec 5.12). The first discussion operation of a process runs the election: a process that finds
/// a board on the pipe becomes a client (REMOTE), the one that creates the first instance of the pipe name becomes the HUB and serves the others. A REMOTE process whose
/// hub vanished elects again once, and repeats the operation only when repeating cannot apply it twice. Where sharing is not available the board is local and the status says so.
/// </summary>
internal sealed class DiscussionGatewayRouter : IDiscussionGateway, IDisposable
{
    private readonly DiscussionSettings _settings;
    private readonly TimeProvider _time;
    private readonly ICommandLog? _commandLog;
    private readonly string? _protocolSuffix;
    private readonly string? _pipeName;
    private readonly string? _sharingKey;
    private readonly SemaphoreSlim _electionLock = new(1, 1);
    private Board? _board;
    private int _disposed;

    public DiscussionGatewayRouter(
        DiscussionSettings settings,
        TimeProvider time,
        ICommandLog? commandLog,
        string? protocolSuffix,
        string resolvedDailyDataPath,
        DiscussionTransportOptions? transport = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        _settings = settings;
        _time = time;
        _commandLog = commandLog;
        _protocolSuffix = protocolSuffix;
        transport ??= new DiscussionTransportOptions();
        if (transport.SharingAvailable)
        {
            _pipeName = DiscussionSharingKey.PipeName(Environment.UserName, resolvedDailyDataPath, transport.PipeNameSuffix);
            _sharingKey = DiscussionSharingKey.DataHash8(resolvedDailyDataPath);
        }
    }

    /// <summary>The role this process has now: <c>hub</c>, <c>remote</c>, or null before the first operation.</summary>
    public string? Role => Volatile.Read(ref _board)?.Info.Role;

    /// <summary>The board of this process when it holds one; null for a REMOTE process or before the election.</summary>
    internal DiscussionHub? LocalHub => Volatile.Read(ref _board)?.Hub;

    /// <summary>The name of the pipe, or null when sharing is not available.</summary>
    internal string? PipeName => _pipeName;

    private sealed class Board
    {
        public Board(DiscussionProcessInfo info, DiscussionHub? hub, DiscussionHubGateway? gateway, DiscussionPipeHost? host)
        {
            Info = info;
            Hub = hub;
            Gateway = gateway;
            Host = host;
        }

        public DiscussionProcessInfo Info { get; }

        public DiscussionHub? Hub { get; }

        /// <summary>Set for a process that holds the board (calls go straight in); null for REMOTE.</summary>
        public DiscussionHubGateway? Gateway { get; }

        public DiscussionPipeHost? Host { get; }
    }

    public async Task<DiscussionReply> ExecuteAsync(DiscussionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Unavailable("The discussion board has been shut down.");
        }

        try
        {
            return request.Operation == DiscussionOperations.Status
                ? await StatusAsync(request, cancellationToken).ConfigureAwait(false)
                : await CallAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return Unavailable("The discussion board has been shut down.");
        }
    }

    // ---- Calling ------------------------------------------------------------------------------------------------------------------------

    private async Task<DiscussionReply> CallAsync(DiscussionRequest request, CancellationToken cancellationToken)
    {
        Board? board = await EnsureBoardAsync(failed: null, cancellationToken).ConfigureAwait(false);
        if (board is null)
        {
            return Unavailable("No discussion board could be reached.");
        }

        if (board.Gateway is not null)
        {
            return await board.Gateway.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        (DiscussionReply? reply, bool lost) = await TryRemoteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!lost)
        {
            return reply!;
        }

        if (!DiscussionOperations.IsRepeatable(request.Operation, HasClientRequestId(request)))
        {
            return Unavailable("The link to the discussion board broke before the answer arrived.", "Read the discussion again to see whether the request was applied.");
        }

        // The hub is gone or unreachable: elect once more and repeat the operation, which cannot be applied twice.
        Board? elected = await EnsureBoardAsync(failed: board, cancellationToken).ConfigureAwait(false);
        if (elected is null)
        {
            return Unavailable("No discussion board could be reached.");
        }

        if (elected.Gateway is not null)
        {
            return await elected.Gateway.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        (reply, lost) = await TryRemoteAsync(request, cancellationToken).ConfigureAwait(false);
        return lost ? Unavailable("The link to the discussion board broke before the answer arrived.") : reply!;
    }

    /// <summary>One call over the pipe. <c>lost</c> is true when the link failed (no reply to use); the reply is then null.</summary>
    private async Task<(DiscussionReply? Reply, bool Lost)> TryRemoteAsync(DiscussionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return (await DiscussionPipeClient.CallAsync(_pipeName!, request, cancellationToken).ConfigureAwait(false), false);
        }
        catch (DiscussionWireException error) when (error.Problem == DiscussionWireProblem.VersionMismatch)
        {
            return (Unavailable("The hub is a different server version.", DiscussionRouterTexts.DifferentVersionHint), false);
        }
        catch (Exception error) when (error is TimeoutException or IOException or DiscussionWireException or UnauthorizedAccessException)
        {
            return (null, true);
        }
    }

    private static bool HasClientRequestId(DiscussionRequest request)
    {
        if (request.Operation != DiscussionOperations.Post)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("clientRequestId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(id.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- Status -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The board's part comes from the board (here or over the pipe) as data; this process adds its own: role, sharing key and the tail of its command log.
    /// </summary>
    private async Task<DiscussionReply> StatusAsync(DiscussionRequest request, CancellationToken cancellationToken)
    {
        DiscussionReply hubPart = await CallAsync(request with { Operation = DiscussionOperations.StatusHubPart }, cancellationToken).ConfigureAwait(false);
        if (!hubPart.IsOk)
        {
            return hubPart;
        }

        Board? board = Volatile.Read(ref _board);
        if (board is null)
        {
            return Unavailable("No discussion board could be reached.");
        }

        int commandTail = ReadCommandTail(request.ArgumentsJson);
        IReadOnlyList<CommandLogEntry> commands = _commandLog is null || commandTail == 0 ? Array.Empty<CommandLogEntry>() : _commandLog.Tail(commandTail);
        try
        {
            return DiscussionReply.Ok(DiscussionStatusComposer.Compose(hubPart.ResultJson!, board.Info, commands));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return Unavailable("The hub answered with a status this server does not understand.", DiscussionRouterTexts.DifferentVersionHint);
        }
    }

    /// <summary>The board has already checked the value; this only reads it.</summary>
    private static int ReadCommandTail(string argumentsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("commandTail", out JsonElement tail)
                && tail.ValueKind == JsonValueKind.Number
                && tail.TryGetInt32(out int value)
                    ? Math.Max(0, value)
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    // ---- Election -----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The current board. With <paramref name="failed"/> set, a board other than that one (somebody elected in the meantime) is returned as it is; otherwise the election
    /// runs again. Null when no board could be found or created.
    /// </summary>
    private async Task<Board?> EnsureBoardAsync(Board? failed, CancellationToken cancellationToken)
    {
        Board? board = Volatile.Read(ref _board);
        if (board is not null && !ReferenceEquals(board, failed))
        {
            return board;
        }

        await _electionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            board = Volatile.Read(ref _board);
            if (board is not null && !ReferenceEquals(board, failed))
            {
                return board;
            }

            Board? elected = _pipeName is null ? CreateLocalBoard() : await ElectAsync(cancellationToken).ConfigureAwait(false);
            if (elected is not null)
            {
                Volatile.Write(ref _board, elected);
            }

            return elected;
        }
        finally
        {
            _electionLock.Release();
        }
    }

    private Board CreateLocalBoard()
    {
        var hub = new DiscussionHub(_settings, _time);
        return new Board(DiscussionProcessInfo.LocalOnly, hub, new DiscussionHubGateway(hub, _settings, _commandLog, _protocolSuffix), host: null);
    }

    /// <summary>
    /// Spec 5.12: connect as a client; else try to create the first instance of the pipe name; losing that race is not an error, the next round connects to the winner.
    /// </summary>
    private async Task<Board?> ElectAsync(CancellationToken cancellationToken)
    {
        string pipeName = _pipeName!;
        for (int attempt = 1; attempt <= DiscussionLimits.ElectionAttempts; attempt++)
        {
            if (await DiscussionPipeClient.ProbeAsync(pipeName, cancellationToken).ConfigureAwait(false))
            {
                return new Board(new DiscussionProcessInfo(DiscussionProcessInfo.RoleRemote, true, _sharingKey), hub: null, gateway: null, host: null);
            }

            DiscussionPipeHost? host = DiscussionPipeHost.TryClaim(pipeName);
            if (host is not null)
            {
                DiscussionHub? hub = null;
                try
                {
                    hub = new DiscussionHub(_settings, _time);
                    var gateway = new DiscussionHubGateway(hub, _settings, _commandLog, _protocolSuffix);
                    host.Start(gateway);
                    return new Board(new DiscussionProcessInfo(DiscussionProcessInfo.RoleHub, true, _sharingKey), hub, gateway, host);
                }
                catch
                {
                    host.Dispose();
                    hub?.Dispose();
                    throw;
                }
            }

            await Task.Delay(DiscussionLimits.ElectionPauseMs, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static DiscussionReply Unavailable(string message, string? hint = null)
        => DiscussionReply.Fail(DiscussionErrorCodes.HubUnavailable, message, hint: hint ?? DiscussionRouterTexts.RestartHint);

    /// <summary>Hub shutdown order: stop accepting and close the connections, then purge every discussion (the counters return to zero) and stop the sweeper.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Board? board = Interlocked.Exchange(ref _board, null);
        board?.Host?.Dispose();
        board?.Hub?.Dispose();
        _electionLock.Dispose();
    }
}
