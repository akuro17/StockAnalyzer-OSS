#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// The strict key check over a real named pipe: a request that a remote process forwards reaches the hub unchanged and the hub refuses an unknown key there, with the hint
/// to restart all clients. Every test uses a private pipe name (a random suffix), so none of them can meet the board of a running server.
/// </summary>
[Collection("ThreadPoolStarvation")]
public sealed class DiscussionOperationArgumentsPipeTests : IDisposable
{
    private const string Unknown = "zzUnknown";

    private static readonly string DataFolder = Path.Combine(Path.GetTempPath(), "dhub-strict-keys-data");

    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (IDisposable disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private DiscussionGatewayRouter NewRouter(string suffix)
    {
        DiscussionGatewayRouter router = DiscussionRouterTestFactory.Create(suffix, DataFolder);
        _disposables.Add(router);
        return router;
    }

    private static Task<DiscussionReply> AskAsync(IDiscussionGateway gateway, string operation, string arguments)
        => gateway.ExecuteAsync(new DiscussionRequest(operation, "test", arguments), CancellationToken.None);

    [Fact]
    public async Task ARemoteRequestWithAnUnknownKeyIsRefusedByTheHubWithTheRestartHint()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        DiscussionGatewayRouter hub = NewRouter(suffix);
        DiscussionGatewayRouter remote = NewRouter(suffix);

        // The first router that is asked wins the election and holds the board; the second one reaches it over the pipe.
        DiscussionReply created = await AskAsync(hub, DiscussionOperations.Create, """{"title":"t"}""");
        Assert.True(created.IsOk, created.Failure?.Message);
        string id = JsonDocument.Parse(created.ResultJson!).RootElement.GetProperty("sessionId").GetString()!;

        DiscussionReply remoteRead = await AskAsync(remote, DiscussionOperations.Read, $$"""{"sessionId":"{{id}}","participantToken":"x","{{Unknown}}":1}""");
        DiscussionReply remoteStatus = await AskAsync(remote, DiscussionOperations.Status, $$"""{"{{Unknown}}":1}""");

        foreach (DiscussionReply reply in new[] { remoteRead, remoteStatus })
        {
            Assert.False(reply.IsOk);
            DiscussionFailure failure = Assert.IsType<DiscussionFailure>(reply.Failure);
            Assert.Equal(DiscussionErrorCodes.InvalidArgument, failure.Code);
            Assert.Equal(Unknown, failure.Parameter);
            Assert.Equal(DiscussionRouterTexts.UnknownArgumentHint, failure.Hint);
        }

        // The remote status still works for a request of known keys, and it was served by the hub (the discussion of the other router is listed).
        DiscussionReply remoteOk = await AskAsync(remote, DiscussionOperations.Status, """{"commandTail":0}""");
        Assert.True(remoteOk.IsOk, remoteOk.Failure?.Message);
        using JsonDocument status = JsonDocument.Parse(remoteOk.ResultJson!);
        Assert.Contains(id, status.RootElement.GetProperty("sessions").EnumerateArray().Select(session => session.GetProperty("id").GetString()));
    }
}
