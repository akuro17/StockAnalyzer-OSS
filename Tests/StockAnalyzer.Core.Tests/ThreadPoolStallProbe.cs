#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Tests;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("StockAnalyzer.Core.Tests.StallProbeTestFramework", "StockAnalyzer.Core.Tests")]

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Opt-in diagnostics for the intermittent stalls of the full Core.Tests run (a test that waits for work that a queue does not start for tens of seconds).
/// With the environment variable <see cref="ProbeFileVariable"/> set to a file path, a dedicated background thread queues one work item to the thread pool every
/// <see cref="SampleIntervalMilliseconds"/> and measures how long the pool takes to start it. When that takes <see cref="StallThresholdMilliseconds"/> or more, a
/// "begin" line is appended at once (so a run that is stopped during a stall still shows it) and an "end" line when the item has run. A third kind, "inflight",
/// is written at most every <see cref="InFlightLineIntervalSeconds"/> seconds while a test has been running for <see cref="LongInFlightSeconds"/> seconds or more and the
/// pool is not stalled.
/// A line is: UTC time | begin or end | wait in ms | pool thread count | pool pending items | GC pause since the previous sample in ms | number of dotnet, testhost,
/// MSBuild and python processes of the machine | logical processors | seconds since the test host started | processor use of the process since the previous line in
/// percent of all logical processors | the number of tests in flight, then the oldest of them with
/// their age in seconds (<see cref="StallProbeTestFramework"/> reports the start and the end of every test, because the console log of a run carries no true finish
/// times). The processes of the machine and the tests in flight are read only when a line is written, never on a normal sample. With the variable unset or empty
/// nothing starts and the test framework is the standard one. Every failure of the probe is swallowed after one line: the probe can never fail a test.
/// </summary>
internal static class ThreadPoolStallProbe
{
    internal const string ProbeFileVariable = "SA_TEST_POOL_PROBE_FILE";

    private const int SampleIntervalMilliseconds = 100;
    private const int StallThresholdMilliseconds = 1000;
    private const int ListedTestCount = 8;
    private const int LongInFlightSeconds = 10;
    private const int InFlightLineIntervalSeconds = 5;
    private const string ThreadName = "SA.PoolStallProbe";
    private const string FailureLine = "probe failed: ";

    private static readonly string[] WatchedProcessNames = { "dotnet", "testhost", "MSBuild", "python" };
    private static readonly object InFlightGate = new();
    private static readonly Dictionary<string, List<long>> InFlight = new(StringComparer.Ordinal);

    private static long _startedAt;
    private static long _lastCpuAt;
    private static TimeSpan _lastCpu;

    internal static bool IsEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProbeFileVariable));

    [ModuleInitializer]
    internal static void Start()
    {
        string? path = Environment.GetEnvironmentVariable(ProbeFileVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        new Thread(() => Run(path)) { IsBackground = true, Name = ThreadName }.Start();
    }

    internal static void TestStarted(string displayName)
    {
        lock (InFlightGate)
        {
            if (!InFlight.TryGetValue(displayName, out List<long>? starts))
            {
                InFlight[displayName] = starts = new List<long>();
            }

            starts.Add(Stopwatch.GetTimestamp());
        }
    }

    internal static void TestFinished(string displayName)
    {
        lock (InFlightGate)
        {
            if (InFlight.TryGetValue(displayName, out List<long>? starts) && starts.Count > 0)
            {
                starts.RemoveAt(0);
                if (starts.Count == 0)
                {
                    InFlight.Remove(displayName);
                }
            }
        }
    }

    private static void Run(string path)
    {
        try
        {
            TimeSpan previousPause = GC.GetTotalPauseDuration();
            long lastInFlightLine = Stopwatch.GetTimestamp();
            while (true)
            {
                // The probe waits on an event, not on the task: Task.Wait would run a task that the pool has not started yet inline on this thread and
                // report no delay at all.
                using var started = new ManualResetEventSlim();
                long queuedAt = Stopwatch.GetTimestamp();
                _ = Task.Run(() =>
                {
                    Interlocked.Exchange(ref _startedAt, Stopwatch.GetTimestamp());
                    started.Set();
                });
                bool stalled = !started.Wait(StallThresholdMilliseconds);
                TimeSpan pause = GC.GetTotalPauseDuration();
                if (stalled)
                {
                    Append(path, "begin", Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds, pause - previousPause);
                    started.Wait();
                    pause = GC.GetTotalPauseDuration();
                }

                double waited = Stopwatch.GetElapsedTime(queuedAt, Interlocked.Read(ref _startedAt)).TotalMilliseconds;
                if (stalled || waited >= StallThresholdMilliseconds)
                {
                    Append(path, "end", waited, pause - previousPause);
                }

                // A test that has been running for a long time while the pool answers at once points at the xUnit worker queue or at a wait that the tests share,
                // not at the pool: one "inflight" line every InFlightLineIntervalSeconds says what is in flight and how busy the process is.
                if (!stalled && Stopwatch.GetElapsedTime(lastInFlightLine).TotalSeconds >= InFlightLineIntervalSeconds && OldestInFlightSeconds() >= LongInFlightSeconds)
                {
                    lastInFlightLine = Stopwatch.GetTimestamp();
                    Append(path, "inflight", waited, pause - previousPause);
                }

                previousPause = pause;
                Thread.Sleep(SampleIntervalMilliseconds);
            }
        }
        catch (Exception error)
        {
            TryWrite(path, FailureLine + error.GetType().Name);
        }
    }

    private static void Append(string path, string kind, double waitedMilliseconds, TimeSpan pause)
    {
        using Process self = Process.GetCurrentProcess();
        TryWrite(path, string.Join(" | ",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            kind,
            waitedMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
            ThreadPool.ThreadCount.ToString(CultureInfo.InvariantCulture),
            ThreadPool.PendingWorkItemCount.ToString(CultureInfo.InvariantCulture),
            pause.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
            CountWatchedProcesses().ToString(CultureInfo.InvariantCulture),
            Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            (DateTime.Now - self.StartTime).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
            CpuPercent(self).ToString("F0", CultureInfo.InvariantCulture),
            DescribeInFlight()));
    }

    /// <summary>Processor time of this process since the previous line, as a percentage of all logical processors.</summary>
    private static double CpuPercent(Process self)
    {
        self.Refresh();
        long now = Stopwatch.GetTimestamp();
        TimeSpan cpu = self.TotalProcessorTime;
        double percent = _lastCpuAt == 0 ? 0 : (cpu - _lastCpu).TotalSeconds / (Stopwatch.GetElapsedTime(_lastCpuAt, now).TotalSeconds * Environment.ProcessorCount) * 100;
        _lastCpuAt = now;
        _lastCpu = cpu;
        return percent;
    }

    private static double OldestInFlightSeconds()
    {
        lock (InFlightGate)
        {
            long now = Stopwatch.GetTimestamp();
            double oldest = 0;
            foreach (List<long> starts in InFlight.Values)
            {
                foreach (long start in starts)
                {
                    oldest = Math.Max(oldest, Stopwatch.GetElapsedTime(start, now).TotalSeconds);
                }
            }

            return oldest;
        }
    }

    private static string DescribeInFlight()
    {
        lock (InFlightGate)
        {
            long now = Stopwatch.GetTimestamp();
            var all = InFlight.SelectMany(pair => pair.Value.Select(start => (Name: pair.Key, Age: Stopwatch.GetElapsedTime(start, now).TotalSeconds))).ToList();
            string oldest = string.Join("; ", all.OrderByDescending(test => test.Age).Take(ListedTestCount)
                .Select(test => test.Name + " (" + test.Age.ToString("F1", CultureInfo.InvariantCulture) + " s)"));
            return all.Count.ToString(CultureInfo.InvariantCulture) + " | " + oldest;
        }
    }

    private static int CountWatchedProcesses()
    {
        int count = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (Array.Exists(WatchedProcessNames, name => string.Equals(name, process.ProcessName, StringComparison.OrdinalIgnoreCase)))
                    {
                        count++;
                    }
                }
                catch (InvalidOperationException)
                {
                    // the process ended while it was being read
                }
            }
        }

        return count;
    }

    private static void TryWrite(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // the probe file cannot be written: nothing else to do
        }
    }
}

/// <summary>
/// The test framework of this assembly: the standard xUnit framework, except that with <see cref="ThreadPoolStallProbe.ProbeFileVariable"/> set it reports the start
/// and the end of every test to <see cref="ThreadPoolStallProbe"/> (an assembly-level BeforeAfterTest attribute is not run by xUnit 2.5.3). Without the variable it
/// returns the standard executor, so nothing changes.
/// </summary>
public sealed class StallProbeTestFramework : XunitTestFramework
{
    public StallProbeTestFramework(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => ThreadPoolStallProbe.IsEnabled
            ? new TracingExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink)
            : base.CreateExecutor(assemblyName);

    private sealed class TracingExecutor : XunitTestFrameworkExecutor
    {
        public TracingExecutor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
            : base(assemblyName, sourceInformationProvider, diagnosticMessageSink)
        {
        }

        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new XunitTestAssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink, new TracingSink(executionMessageSink), executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class TracingSink : IMessageSink
    {
        private readonly IMessageSink _inner;

        public TracingSink(IMessageSink inner) => _inner = inner;

        public bool OnMessage(IMessageSinkMessage message)
        {
            if (message is ITestStarting starting)
            {
                ThreadPoolStallProbe.TestStarted(starting.Test.DisplayName);
            }
            else if (message is ITestFinished finished)
            {
                ThreadPoolStallProbe.TestFinished(finished.Test.DisplayName);
            }

            return _inner.OnMessage(message);
        }
    }
}
