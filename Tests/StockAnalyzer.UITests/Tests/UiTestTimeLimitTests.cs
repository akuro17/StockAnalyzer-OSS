using System.Reflection;
using StockAnalyzer.UITests.Infrastructure;

namespace StockAnalyzer.UITests.Tests;

/// <summary>Rules of the UI test time limit; none of these tests launches the application.</summary>
public sealed class UiTestTimeLimitTests
{
    [Fact]
    public void TheLimitIsFiveMinutes() => Assert.Equal(TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(UiTestLimits.TestTimeLimitMs));

    [Fact]
    public void TheWatchdogFiresOnceTheLimitPassesAndNotBefore()
    {
        using var fired = new ManualResetEventSlim();
        using var watchdog = new UiTestWatchdog(TimeSpan.FromMilliseconds(200), fired.Set);
        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(50)));
        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ADisposedWatchdogNeverFires()
    {
        int calls = 0;
        new UiTestWatchdog(TimeSpan.FromMilliseconds(100), () => Interlocked.Increment(ref calls)).Dispose();
        Thread.Sleep(500);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ADerivedConstructorThatFailsAfterTheBaseConstructorLeavesNoRunningLimit()
    {
        using var fired = new ManualResetEventSlim();

        Assert.Throws<InvalidOperationException>(() => new FailingProbe(fired));

        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(ProbeLimitMs * 4)));
    }

    [Fact]
    public void TheLimitStartsOnlyWhenTheTestStartsItAndAnotherStartDoesNotRestartIt()
    {
        using var fired = new ManualResetEventSlim();
        using var probe = new Probe(fired);
        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(ProbeLimitMs * 2)));

        probe.Start();
        probe.Start();

        Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, probe.Expirations);
    }

    [Fact]
    public void ADisposedProbeNeverFiresAndASecondDisposeIsHarmless()
    {
        using var fired = new ManualResetEventSlim();
        new Probe(fired).Dispose(); // loads what the first dispose needs, so the dispose below is not delayed past the limit
        var probe = new Probe(fired, TimeSpan.FromMilliseconds(LongProbeLimitMs));
        probe.Start();

        probe.Dispose();
        probe.Dispose();

        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(LongProbeLimitMs * 3 / 2)));
        Assert.Equal(0, probe.Expirations);
    }

    private const int ProbeLimitMs = 150;
    private const int LongProbeLimitMs = 1000;

    private class Probe(ManualResetEventSlim fired, TimeSpan? limit = null) : UITestBase
    {
        private int _expirations;

        public int Expirations => Volatile.Read(ref _expirations);

        protected override TimeSpan TimeLimit => limit ?? TimeSpan.FromMilliseconds(ProbeLimitMs);

        public void Start() => StartTimeLimit();

        /// <summary>Replaces the termination of the test host, which a test of the limit must not trigger.</summary>
        protected override void OnTimeLimitExceeded()
        {
            Interlocked.Increment(ref _expirations);
            fired.Set();
        }
    }

    private sealed class FailingProbe : Probe
    {
        public FailingProbe(ManualResetEventSlim fired) : base(fired) => throw new InvalidOperationException("A derived constructor failed after the base constructor ran.");
    }

    [Fact]
    public void NoTestDeclaresAnXunitTimeoutBeyondTheLimit()
    {
        var tooLong = typeof(UiTestTimeLimitTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetCustomAttributes<FactAttribute>().Select(fact => (method, fact.Timeout)))
            .Where(entry => entry.Timeout > UiTestLimits.TestTimeLimitMs)
            .Select(entry => $"{entry.method.DeclaringType!.Name}.{entry.method.Name}: {entry.Timeout} ms")
            .ToArray();
        Assert.Empty(tooLong);
    }
}
