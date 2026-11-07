#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T2, spec 5.9 and matrix T011/T012: the byte counter equals the recomputed sum at every step and returns to exactly zero when the sessions are gone.</summary>
public sealed class DiscussionByteAccountingTests
{
    private static void AssertExact(DiscussionHub hub)
    {
        DiscussionDiagnostics diagnostics = hub.GetDiagnostics();
        Assert.Equal(diagnostics.RecomputedBytes, diagnostics.TotalBytes);
    }

    [Fact]
    public void TwoHundredSessionsCreatedFilledClosedAndPurgedEndAtExactlyZero()
    {
        using var rig = new HubRig(s => { s.MaxPostsPerMinute = 120; s.ClosedRetentionSeconds = 60; });

        for (int round = 0; round < 200; round++)
        {
            string id = rig.NewSession("round " + round);
            string alice = rig.Join(id, "Alice").Token;
            string bob = rig.Join(id, "Bob").Token;
            for (int n = 0; n < 5; n++)
            {
                rig.Post(id, n % 2 == 0 ? alice : bob, $"message {round}/{n} あ\U0001F4C8");
            }

            rig.Hub.AttachCommit(id, alice, "sa_x", $"{{\"round\":{round}}}", $"{{\"v\":{round},\"text\":\"日本\"}}", "note");
            AssertExact(rig.Hub);
            Assert.True(rig.Hub.GetDiagnostics().TotalBytes > 0);

            rig.Hub.Close(id, alice);
            AssertExact(rig.Hub);
            rig.Time.Advance(60); // retention over: the next evaluation removes it
            rig.Hub.Sweep();

            DiscussionDiagnostics after = rig.Hub.GetDiagnostics();
            Assert.Equal(0, after.TotalBytes);
            Assert.Equal(0, after.SessionCount);
            Assert.Equal(0, after.WaiterCount);
        }
    }

    [Fact]
    public async Task ASessionRemovedWhileAnotherIsFilledOnlySubtractsItsOwnBytes()
    {
        using var rig = new HubRig();
        string first = rig.NewSession();
        string second = rig.NewSession();
        string firstToken = rig.Join(first, "Alice").Token;
        string secondToken = rig.Join(second, "Bob").Token;
        rig.Post(first, firstToken, new string('a', 1000));
        rig.Post(second, secondToken, new string('b', 2000));
        long firstOnly = rig.Hub.GetDiagnostics().TotalBytes;

        rig.Hub.Close(first, firstToken);
        rig.Time.Advance(rig.Settings.ClosedRetentionSeconds);
        rig.Hub.Sweep();

        DiscussionDiagnostics diagnostics = rig.Hub.GetDiagnostics();
        Assert.Equal(1, diagnostics.SessionCount);
        Assert.True(diagnostics.TotalBytes < firstOnly);
        Assert.True(diagnostics.TotalBytes >= 2000);
        AssertExact(rig.Hub);
        Assert.NotEmpty((await rig.Hub.ReadAsync(second, secondToken)).Messages);
    }

    [Fact]
    public void TheCounterIsUnchangedByRefusedOperations()
    {
        using var rig = new HubRig(s => s.MaxMessageBytes = 256);
        string id = rig.NewSession();
        string token = rig.Join(id, "Alice").Token;
        rig.Post(id, token, "ok");
        long before = rig.Hub.GetDiagnostics().TotalBytes;

        Assert.Throws<DiscussionException>(() => rig.Post(id, token, new string('x', 300)));
        Assert.Throws<DiscussionException>(() => rig.Post(id, token, "bell\a"));
        Assert.Throws<DiscussionException>(() => rig.Hub.Join(id, "Alice"));
        Assert.Throws<DiscussionException>(() => rig.Hub.AttachCommit(id, token, "", "{}", "{}", null));

        Assert.Equal(before, rig.Hub.GetDiagnostics().TotalBytes);
        AssertExact(rig.Hub);
    }

    [Fact]
    public void DisposingTheHubZeroesTheCounterEvenWithOpenSessions()
    {
        var rig = new HubRig();
        for (int i = 0; i < 3; i++)
        {
            string id = rig.NewSession();
            string token = rig.Join(id, "Alice").Token;
            rig.Post(id, token, "x");
        }

        Assert.True(rig.Hub.GetDiagnostics().TotalBytes > 0);
        rig.Dispose();

        Assert.Equal(new DiscussionDiagnostics(0, 0, 0, 0), rig.Hub.GetDiagnostics());
        Assert.Throws<ObjectDisposedException>(() => rig.NewSession());
        rig.Hub.Dispose(); // a second dispose is harmless
    }

    [Fact]
    public void HundredCreateCloseCyclesWithTheDefaultRetentionKeepTheCounterAtZero()
    {
        using var rig = new HubRig();

        for (int cycle = 0; cycle < 100; cycle++)
        {
            string id = rig.NewSession();
            string token = rig.Join(id, "Alice").Token;
            rig.Post(id, token, "hello");
            rig.Hub.Close(id, token);
            rig.Time.Advance(rig.Settings.ClosedRetentionSeconds);
            rig.Hub.Sweep();
        }

        Assert.Equal(new DiscussionDiagnostics(0, 0, 0, 0), rig.Hub.GetDiagnostics());
        Assert.Equal(0, rig.Hub.Status().Sessions.Count());
    }
}
