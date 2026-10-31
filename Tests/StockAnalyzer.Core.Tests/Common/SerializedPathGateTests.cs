#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// One writer per path at a time, so overlapping settings saves no longer collide on the shared "&lt;file&gt;.tmp"
/// (IoHydrationWebAiReview, T3). Ordering between waiting callers is deliberately not asserted.
/// </summary>
public sealed class SerializedPathGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_path_gate_" + Guid.NewGuid().ToString("N"));
    private sealed record Sample(int Value);
    private string PathOf(string name) => Path.Combine(_directory, name);
    public SerializedPathGateTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } }

    [Fact]
    public async Task SamePath_NeverHasTwoHoldersAtOnce()
    {
        string path = PathOf("a.json");
        int inside = 0, maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            using (await SerializedPathGate.AcquireAsync(path))
            {
                int now = Interlocked.Increment(ref inside);
                int seen;
                while ((seen = Volatile.Read(ref maxInside)) < now && Interlocked.CompareExchange(ref maxInside, now, seen) != seen) { }
                await Task.Delay(1);
                Interlocked.Decrement(ref inside);
            }
        })));

        Assert.Equal(1, maxInside);
        Assert.False(SerializedPathGate.IsTracked(path));
    }

    [Fact]
    public async Task DifferentPaths_DoNotWaitForEachOther()
    {
        using var held = await SerializedPathGate.AcquireAsync(PathOf("one.json"));
        var other = SerializedPathGate.AcquireAsync(PathOf("two.json")).AsTask();

        Assert.Same(other, await Task.WhenAny(other, Task.Delay(2000)));
        (await other).Dispose();
    }

    [Fact]
    public async Task CancelledWait_ThrowsAndLeavesNothingTracked()
    {
        string path = PathOf("cancel.json");
        var first = await SerializedPathGate.AcquireAsync(path);
        using var cts = new CancellationTokenSource();
        var waiting = SerializedPathGate.AcquireAsync(path, cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(SerializedPathGate.IsTracked(path)); // the first holder still owns it

        first.Dispose();
        Assert.False(SerializedPathGate.IsTracked(path));
    }

    [Fact]
    public async Task DisposeTwice_DoesNotReleaseSomeoneElsesHold()
    {
        string path = PathOf("twice.json");
        var first = await SerializedPathGate.AcquireAsync(path);
        var secondTask = SerializedPathGate.AcquireAsync(path).AsTask();
        first.Dispose();
        var second = await secondTask;

        first.Dispose(); // idempotent: must not free the path the second holder owns
        var third = SerializedPathGate.AcquireAsync(path).AsTask();

        Assert.NotSame(third, await Task.WhenAny(third, Task.Delay(150)));
        second.Dispose();
        (await third).Dispose();
        Assert.False(SerializedPathGate.IsTracked(path));
    }

    [Fact]
    public async Task PathComparison_FollowsThePlatform()
    {
        using var held = await SerializedPathGate.AcquireAsync(PathOf("Case.json"));
        var other = SerializedPathGate.AcquireAsync(PathOf("case.json")).AsTask();

        if (OperatingSystem.IsWindows())
        {
            Assert.NotSame(other, await Task.WhenAny(other, Task.Delay(150)));
            held.Dispose();
        }
        else
        {
            Assert.Same(other, await Task.WhenAny(other, Task.Delay(2000)));
        }
        (await other).Dispose();
    }

    [Fact]
    public async Task InvalidPath_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SerializedPathGate.AcquireAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(async () => await SerializedPathGate.AcquireAsync("  "));
    }

    [Fact]
    public async Task OverlappingAtomicSaves_UnderTheGate_AllCompleteWithoutException()
    {
        string path = PathOf("settings.json");
        var payloads = Enumerable.Range(0, 200).Select(i => new Sample(i)).ToArray();

        await Task.WhenAll(payloads.Select(p => Task.Run(async () =>
        {
            using (await SerializedPathGate.AcquireAsync(path))
            {
                await AtomicJsonFile.SaveAsync(path, p);
            }
        })));

        var stored = JsonSerializer.Deserialize<Sample>(File.ReadAllText(path));
        Assert.Contains(stored, payloads);
        Assert.False(SerializedPathGate.IsTracked(path));
    }
}
