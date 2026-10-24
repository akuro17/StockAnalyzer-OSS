#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Tests.Services;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Execution;

/// <summary>
/// Decision 2-A: the pre-check of the managed reinforcement-learning environment. The decisions (what is kept, what is shared, what is asked) are tested with
/// a scripted process runner; the real process runner is tested with the application's own <c>.venv</c> interpreter (opt-in, skipped when there is none).
/// </summary>
[Collection("ChildProcess")]
public sealed class RlEnvironmentProbeTests : IDisposable
{
    /// <summary>The scripted runner never starts it; the probe only asks whether the file exists, so the name carries no platform.</summary>
    private const string InterpreterStubFileName = "interpreter.stub";

    private static readonly RlProbeRun Imported =new(TimedOut: false, 0, string.Empty);
    private static readonly RlProbeRun GymnasiumMissing = new(TimedOut: false, 1, "Traceback (most recent call last):\nModuleNotFoundError: No module named 'gymnasium'\n");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sa_rl_probe_" + Guid.NewGuid().ToString("N"));
    private readonly string _python;

    public RlEnvironmentProbeTests()
    {
        Directory.CreateDirectory(_folder);
        _python = Path.Combine(_folder, InterpreterStubFileName);
        File.WriteAllText(_python, string.Empty); // only its existence matters to the scripted runner
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private sealed class ScriptedProcess
    {
        private readonly Queue<RlProbeRun> _results = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<ProcessStartInfo> Starts { get; } = new();

        public TaskCompletionSource? Hold { get; set; }

        public Exception? Throws { get; set; }

        public ScriptedProcess Returns(params RlProbeRun[] runs)
        {
            foreach (RlProbeRun run in runs)
            {
                _results.Enqueue(run);
            }

            return this;
        }

        public async Task<RlProbeRun> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (Starts)
            {
                Starts.Add(start);
            }

            Interlocked.Increment(ref _calls);
            if (Hold is { } hold)
            {
                await hold.Task.ConfigureAwait(false);
            }

            if (Throws is { } error)
            {
                throw error;
            }

            lock (_results)
            {
                return _results.Count > 1 ? _results.Dequeue() : _results.Peek();
            }
        }
    }

    private RlEnvironmentProbe Probe(ScriptedProcess process, string? python = null, TimeSpan? timeout = null)
        => new(() => (python ?? _python, _folder), process.RunAsync, timeout);

    [Fact]
    public async Task AMissingInterpreterIsUnusableAndStartsNoProcess()
    {
        var process = new ScriptedProcess().Returns(Imported);

        RlEnvironmentStatus status = await Probe(process, Path.Combine(_folder, "no", InterpreterStubFileName)).CheckAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Null(status.MissingModule);
        Assert.Equal(0, process.Calls);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    public async Task PythonSourcesThatCannotBeFoundAreUnusableAndStartNoProcess(Type exception)
    {
        var process = new ScriptedProcess().Returns(Imported);
        var probe = new RlEnvironmentProbe(() => throw (Exception)Activator.CreateInstance(exception)!, process.RunAsync);

        RlEnvironmentStatus status = await probe.CheckAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal(0, process.Calls);
    }

    [Fact]
    public async Task AFailureIsNeverKeptSoPackagesInstalledLaterAreFoundWithoutARestart()
    {
        var process = new ScriptedProcess().Returns(GymnasiumMissing, Imported);
        RlEnvironmentProbe probe = Probe(process);

        RlEnvironmentStatus first = await probe.CheckAsync(CancellationToken.None);
        RlEnvironmentStatus second = await probe.CheckAsync(CancellationToken.None);

        Assert.False(first.Available);
        Assert.Equal("gymnasium", first.MissingModule);
        Assert.True(second.Available);
        Assert.Equal(2, process.Calls);
    }

    [Fact]
    public async Task ASuccessIsKeptAndTheProcessIsNotStartedAgain()
    {
        var process = new ScriptedProcess().Returns(Imported);
        RlEnvironmentProbe probe = Probe(process);

        for (int call = 0; call < 3; call++)
        {
            Assert.True((await probe.CheckAsync(CancellationToken.None)).Available);
        }

        Assert.Equal(1, process.Calls);
    }

    [Fact]
    public async Task ATimeoutIsUnusableWithoutAModuleAndIsNotKept()
    {
        var process = new ScriptedProcess().Returns(new RlProbeRun(TimedOut: true, -1, string.Empty), Imported);
        RlEnvironmentProbe probe = Probe(process);

        RlEnvironmentStatus first = await probe.CheckAsync(CancellationToken.None);
        RlEnvironmentStatus second = await probe.CheckAsync(CancellationToken.None);

        Assert.False(first.Available);
        Assert.Null(first.MissingModule);
        Assert.True(second.Available);
    }

    [Fact]
    public async Task AFailureWithoutAModuleLineIsUnusableWithoutAModule()
    {
        var process = new ScriptedProcess().Returns(new RlProbeRun(TimedOut: false, 1, "ImportError: DLL load failed while importing _C"));

        RlEnvironmentStatus status = await Probe(process).CheckAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Null(status.MissingModule);
    }

    [Fact]
    public async Task AnInterpreterThatCannotBeStartedIsUnusableAndIsNotKept()
    {
        var process = new ScriptedProcess().Returns(Imported);
        process.Throws = new Win32Exception("blocked");
        RlEnvironmentProbe probe = Probe(process);

        RlEnvironmentStatus first = await probe.CheckAsync(CancellationToken.None);
        process.Throws = null;
        RlEnvironmentStatus second = await probe.CheckAsync(CancellationToken.None);

        Assert.False(first.Available);
        Assert.True(second.Available);
    }

    [Fact]
    public async Task CallersWhoArriveTogetherShareOneProcess()
    {
        const int callerCount = 8;
        var process = new ScriptedProcess().Returns(Imported);
        process.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int located = 0;
        var probe = new RlEnvironmentProbe(() => { Interlocked.Increment(ref located); return (_python, _folder); }, process.RunAsync);

        Task<RlEnvironmentStatus>[] callers = Enumerable.Range(0, callerCount).Select(_ => Task.Run(() => probe.CheckAsync(CancellationToken.None))).ToArray();
        await WaitUntilAsync(() => process.Calls >= 1 && Volatile.Read(ref located) == callerCount); // every caller has reached the probe while the one process is still held
        process.Hold.SetResult();
        RlEnvironmentStatus[] results = await Task.WhenAll(callers);

        Assert.All(results, status => Assert.True(status.Available));
        Assert.Equal(1, process.Calls);
    }

    [Fact]
    public async Task ACallerThatStopsWaitingDoesNotStopTheSharedProbe()
    {
        var process = new ScriptedProcess().Returns(Imported);
        process.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RlEnvironmentProbe probe = Probe(process);
        using var impatient = new CancellationTokenSource();

        Task<RlEnvironmentStatus> cancelled = probe.CheckAsync(impatient.Token);
        await WaitUntilAsync(() => process.Calls >= 1);
        Task<RlEnvironmentStatus> patient = probe.CheckAsync(CancellationToken.None);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        process.Hold.SetResult();

        Assert.True((await patient).Available);
        Assert.Equal(1, process.Calls);
    }

    [Fact]
    public async Task TheProbeAsksTheInterpreterToImportTheTwoPackagesWithoutTouchingTheServersStreamsOrTheEnvironment()
    {
        var process = new ScriptedProcess().Returns(Imported);

        await Probe(process).CheckAsync(CancellationToken.None);

        ProcessStartInfo start = Assert.Single(process.Starts);
        Assert.Equal(_python, start.FileName);
        Assert.Equal(_folder, start.WorkingDirectory);
        Assert.Equal(new[] { "-c", "import gymnasium, stable_baselines3" }, start.ArgumentList);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError); // the MCP server's stdin/stdout are the protocol
        Assert.False(start.UseShellExecute);
        Assert.Equal("1", start.Environment["PYTHONDONTWRITEBYTECODE"]);
    }

    [Theory]
    [InlineData("ModuleNotFoundError: No module named 'gymnasium'", "gymnasium")]
    [InlineData("ModuleNotFoundError: No module named 'torch.nn'", "torch")]
    [InlineData("ModuleNotFoundError: No module named 'stable_baselines3'", "stable_baselines3")]
    [InlineData("ModuleNotFoundError: No module named 'C:\\Users\\someone\\x'", null)]
    [InlineData("ModuleNotFoundError: No module named '../x'", null)]
    [InlineData("ModuleNotFoundError: No module named '/home/someone/x'", null)]
    [InlineData("ImportError: DLL load failed while importing _C", null)]
    [InlineData("", null)]
    public void TheModuleNameIsTakenOnlyFromPythonsOwnLineAndOnlyAsAPlainIdentifier(string standardError, string? expected)
    {
        Assert.Equal(expected, RlEnvironmentProbe.MissingModuleOf(standardError));
    }

    [Fact]
    public void AnOverlongModuleNameIsNotReported()
    {
        Assert.Null(RlEnvironmentProbe.MissingModuleOf($"No module named '{new string('a', 65)}'"));
    }

    [Fact]
    public void TheTimeLimitIsTheContractsAndBelowTheDefaultToolTimeout()
    {
        Assert.Equal(20, ExecutionLimits.RlEnvironmentProbeSeconds);
        Assert.InRange(ExecutionLimits.RlEnvironmentProbeSeconds, 1, 29);
    }

    // ---- the real process runner, with the application's own interpreter --------------------------------------------------------------

    private static string RequireInterpreter()
    {
        string python = RlWorkerProcess.ResolveInterpreter(RlFiles.TrainerScript).Python;
        if (!File.Exists(python))
        {
            throw SkipException.ForSkip("StockAnalyzer.Python/.venv does not exist.");
        }

        return python;
    }

    private static ProcessStartInfo Script(string python, string code)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(code);
        return start;
    }

    [ChildProcessFact]
    public async Task Real_TheManagedEnvironmentCanImportTheTrainersPackages()
    {
        RequireInterpreter();
        var probe = new RlEnvironmentProbe(() =>
        {
            var (_, root, python) = RlWorkerProcess.ResolveInterpreter(RlFiles.TrainerScript);
            return (python, root);
        });

        RlEnvironmentStatus status = await probe.CheckAsync(CancellationToken.None);

        Assert.True(status.Available, $"missing module: {status.MissingModule}");
    }

    [ChildProcessFact]
    public async Task Real_AMissingModuleIsReportedFromTheInterpretersOwnErrorText()
    {
        string python = RequireInterpreter();

        RlProbeRun run = await RlEnvironmentProbe.RunProcessAsync(Script(python, "import nosuchmodule_for_sa_probe"), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.False(run.TimedOut);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("nosuchmodule_for_sa_probe", RlEnvironmentProbe.MissingModuleOf(run.StandardError));
    }

    [ChildProcessFact]
    public async Task Real_AProcessThatOutlivesTheLimitIsKilledAndReportedAsTimedOut()
    {
        string python = RequireInterpreter();
        var clock = Stopwatch.StartNew();

        RlProbeRun run = await RlEnvironmentProbe.RunProcessAsync(Script(python, "import time; time.sleep(60)"), TimeSpan.FromMilliseconds(700), CancellationToken.None);

        Assert.True(run.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [ChildProcessFact]
    public async Task Real_TheChildNeverWaitsOnItsInputAndALargeOutputIsDrained()
    {
        string python = RequireInterpreter();

        RlProbeRun run = await RlEnvironmentProbe.RunProcessAsync(
            Script(python, "import sys; sys.stdin.read(); sys.stdout.write('x' * 2000000); sys.stderr.write('e' * 100000)"), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.False(run.TimedOut);
        Assert.Equal(0, run.ExitCode);
        Assert.InRange(run.StandardError.Length, 1, 8 * 1024); // the error text is bounded
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "condition not reached in time");
            await Task.Delay(5);
        }
    }
}
