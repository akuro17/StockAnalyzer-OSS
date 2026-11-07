#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>
/// Task T2: the hub under parallel callers (spec 5.15, matrix T005, T006, T018): unique gap-free sequences, joins bounded by the limits, a close racing posts,
/// and several readers waiting at once. The clock is manual, so no timing decides a result; the work is real parallel work on the thread pool.
/// </summary>
public sealed class DiscussionHubConcurrencyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ParallelPostsGetUniqueGapFreeSequencesAndEveryMessageIsStoredOnce()
    {
        const int Posters = 6;
        const int PostsEach = 100;
        using var rig = new HubRig(s => { s.MaxPostsPerMinute = 120; s.MaxMessagesPerSession = 2000; });
        string id = rig.NewSession();
        string[] tokens = Enumerable.Range(0, Posters).Select(i => rig.Join(id, "P" + i).Token).ToArray();
        var sequences = new ConcurrentBag<long>();

        await Task.WhenAll(tokens.Select((token, who) => Task.Run(() =>
        {
            for (int n = 0; n < PostsEach; n++)
            {
                sequences.Add(rig.Post(id, token, $"{who}:{n}").Sequence);
            }
        }))).WaitAsync(Patience);

        long expectedTotal = Posters + (Posters * PostsEach);
        Assert.Equal(Posters * PostsEach, sequences.Distinct().Count());
        IReadOnlyList<DiscussionReadMessage> transcript = rig.Hub.ReadAsync(id, tokens[0], 0, 50).GetAwaiter().GetResult().Messages;
        var all = new List<DiscussionReadMessage>();
        long after = 0;
        while (true)
        {
            DiscussionReadResult page = await rig.Hub.ReadAsync(id, tokens[0], after, 50);
            all.AddRange(page.Messages);
            after = page.NextAfterSequence;
            if (!page.HasMore)
            {
                break;
            }
        }

        Assert.Equal(expectedTotal, all.Count);
        Assert.Equal(Enumerable.Range(1, (int)expectedTotal).Select(n => (long)n), all.Select(m => m.Sequence));
        Assert.Equal(PostsEach, all.Count(m => m.Content.StartsWith("3:", StringComparison.Ordinal)));
        Assert.NotEmpty(transcript);
        DiscussionDiagnostics diagnostics = rig.Hub.GetDiagnostics();
        Assert.Equal(diagnostics.RecomputedBytes, diagnostics.TotalBytes);
    }

    [Fact]
    public async Task ParallelJoinsNeverExceedTheParticipantLimitAndHandlesStayGapFree()
    {
        using var rig = new HubRig(); // six participants
        string id = rig.NewSession();
        var handles = new ConcurrentBag<string>();
        var refused = new ConcurrentBag<DiscussionException>();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            try
            {
                handles.Add(rig.Hub.Join(id, "Agent" + i).Handle);
            }
            catch (DiscussionException error)
            {
                refused.Add(error);
            }
        }))).WaitAsync(Patience);

        Assert.Equal(new[] { "p1", "p2", "p3", "p4", "p5", "p6" }, handles.OrderBy(h => h, StringComparer.Ordinal));
        Assert.Equal(14, refused.Count);
        Assert.All(refused, e => Assert.Equal((DiscussionErrorCodes.LimitExceeded, "participants"), (e.Code, e.Parameter)));
    }

    [Fact]
    public async Task ParallelJoinsWithTheSameNameLetExactlyOneIn()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        int accepted = 0;
        var codes = new ConcurrentBag<string>();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            try
            {
                rig.Hub.Join(id, "Same");
                Interlocked.Increment(ref accepted);
            }
            catch (DiscussionException error)
            {
                codes.Add(error.Code);
            }
        }))).WaitAsync(Patience);

        Assert.Equal(1, accepted);
        Assert.All(codes, code => Assert.Equal(DiscussionErrorCodes.InvalidArgument, code));
    }

    [Fact]
    public async Task ParallelCreatesNeverExceedTheActiveLimit()
    {
        using var rig = new HubRig(); // four active discussions
        int created = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            try
            {
                rig.Hub.Create("T" + i, null, DiscussionTurnMode.Free);
                Interlocked.Increment(ref created);
            }
            catch (DiscussionException)
            {
            }
        }))).WaitAsync(Patience);

        Assert.Equal(4, created);
        Assert.Equal(4, rig.Hub.Status().Sessions.Count);
    }

    [Fact]
    public async Task ACloseRacingPostsLeavesNoMessageAfterTheClosingEventAndNoGap()
    {
        for (int round = 0; round < 100; round++)
        {
            using var rig = new HubRig(s => s.MaxPostsPerMinute = 120);
            string id = rig.NewSession();
            string poster = rig.Join(id, "Poster").Token;
            string closer = rig.Join(id, "Closer").Token;
            int accepted = 0;
            using var start = new ManualResetEventSlim(false);

            Task posting = Task.Run(() =>
            {
                start.Wait();
                for (int n = 0; n < 50; n++)
                {
                    try
                    {
                        rig.Post(id, poster, "m" + n);
                        Interlocked.Increment(ref accepted);
                    }
                    catch (DiscussionException error) when (error.Code == DiscussionErrorCodes.DiscussionClosed)
                    {
                        return;
                    }
                }
            });
            Task closing = Task.Run(() =>
            {
                start.Wait();
                Thread.SpinWait(round * 20);
                rig.Hub.Close(id, closer);
            });

            start.Set();
            await Task.WhenAll(posting, closing).WaitAsync(Patience);

            var all = (await rig.Hub.ReadAsync(id, poster, 0, 50)).Messages.ToList();
            long after = all.Count == 0 ? 0 : all[^1].Sequence;
            while (all.Count < rig.Hub.Status(id).Sessions[0].MessageCount)
            {
                DiscussionReadResult page = await rig.Hub.ReadAsync(id, poster, after, 50);
                all.AddRange(page.Messages);
                after = page.NextAfterSequence;
            }

            Assert.Equal("closed by p2", all[^1].Content);
            Assert.Equal(1, all.Count(m => m.Content.StartsWith("closed by", StringComparison.Ordinal)));
            Assert.Equal(Enumerable.Range(1, all.Count).Select(n => (long)n), all.Select(m => m.Sequence));
            Assert.Equal(accepted, all.Count(m => m.Kind == DiscussionMessageKind.Message));
        }
    }

    [Fact]
    public async Task SeveralWaitingReadersAreAllWokenByOneMessage()
    {
        using var rig = new HubRig();
        string id = rig.NewSession();
        string writer = rig.Join(id, "Writer").Token;
        string[] readers = Enumerable.Range(0, 5).Select(i => rig.Join(id, "Reader" + i).Token).ToArray();
        long after = rig.Hub.Status(id).Sessions[0].MessageCount;

        Task<DiscussionReadResult>[] waiting = readers.Select(token => rig.Hub.ReadAsync(id, token, after, waitSeconds: 20)).ToArray();
        Assert.Equal(5, rig.Hub.GetDiagnostics().WaiterCount);
        rig.Post(id, writer, "wake up");
        DiscussionReadResult[] results = await Task.WhenAll(waiting).WaitAsync(Patience);

        Assert.All(results, r => Assert.Equal(new[] { "wake up" }, r.Messages.Select(m => m.Content)));
        Assert.Equal(0, rig.Hub.GetDiagnostics().WaiterCount);
    }

    [Fact]
    public async Task ReadsPostsAndTheSweeperRunTogetherWithoutLosingTheCounters()
    {
        using var rig = new HubRig(s => { s.MaxPostsPerMinute = 120; s.MaxMessagesPerSession = 2000; }, sweeper: true);
        string id = rig.NewSession();
        string writer = rig.Join(id, "Writer").Token;
        string reader = rig.Join(id, "Reader").Token;
        using var stop = new CancellationTokenSource();

        Task sweeping = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                rig.Hub.Sweep();
            }
        });
        Task reading = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await rig.Hub.ReadAsync(id, reader, 0, 50);
            }
        });
        for (int n = 0; n < 100; n++)
        {
            rig.Post(id, writer, "m" + n);
        }

        stop.Cancel();
        await Task.WhenAll(sweeping, reading).WaitAsync(Patience);

        DiscussionDiagnostics diagnostics = rig.Hub.GetDiagnostics();
        Assert.Equal(diagnostics.RecomputedBytes, diagnostics.TotalBytes);
        Assert.Equal(0, diagnostics.WaiterCount);
    }
}
