#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The worker pump of the RL trainers, characterized through the real managed interpreter and the real dependency-capture script.
/// The scenarios were first recorded against the two private copies (T15/T16 and portfolio) and must stay identical for the shared pump.
/// </summary>
[Collection("ChildProcess")]
public sealed class RlWorkerProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_worker_" + Guid.NewGuid().ToString("N"));
    private static readonly TrainingResourceLimits Limits = RlTrainingValidation.FreezeLimits();

    public RlWorkerProcessTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    public static IEnumerable<object[]> Targets() { yield return new object[] { "RlTrainingService" }; yield return new object[] { "RlPortfolioTrainingRunner" }; }

    private delegate Task Driver(string script, string config, string directory, CancellationToken ct);

    private static Driver DriverOf(string target)
    {
        if (target == "RlTrainingService")
        {
            var service = new RlTrainingService(null!);
            var method = typeof(RlTrainingService).GetMethod("RunWorkerAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return (script, config, directory, ct) => Unwrap(() => (Task)method.Invoke(service, new object?[] { script, config, directory, Limits, null, ct })!);
        }
        var portfolio = typeof(RlPortfolioTrainingRunner).GetMethod("RunWorkerAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (script, config, directory, ct) => Unwrap(() => (Task)portfolio.Invoke(null, new object?[] { script, config, directory, Limits, null, ct })!);
    }

    private static async Task Unwrap(Func<Task> call)
    {
        try { await call().ConfigureAwait(false); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private string WriteConfig(bool valid)
    {
        var job = new Dictionary<string, object?>
        {
            ["parent_pid"] = Environment.ProcessId, ["defaults"] = RlContract.ToWireDefaults(), ["artifact_directory"] = _root,
            ["files"] = new Dictionary<string, string> { ["dependencies"] = RlFiles.Dependencies }, ["strict_dependencies"] = false,
        };
        if (!valid) job.Remove("parent_pid");                 // the worker fails before it does any work
        string path = Path.Combine(_root, RlFiles.Job);
        File.WriteAllText(path, JsonSerializer.Serialize(job, RlFiles.Json));
        return path;
    }

    private string StderrLog => Path.Combine(_root, "rl_dependencies.stderr.log");

    [ChildProcessTheory, MemberData(nameof(Targets))]
    public async Task ASuccessfulWorkerWritesItsResultAndLeavesAnEmptyDiagnosticsLog(string target)
    {
        await DriverOf(target)(RlFiles.DependenciesScript, WriteConfig(valid: true), _root, default);
        Assert.True(File.Exists(Path.Combine(_root, RlFiles.Dependencies)));
        Assert.True(File.Exists(StderrLog));
        Assert.Equal(0, new FileInfo(StderrLog).Length);
    }

    [ChildProcessTheory, MemberData(nameof(Targets))]
    public async Task AFailingWorkerNamesItsExitCodeAndTheDiagnosticsFileAndKeepsTheText(string target)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DriverOf(target)(RlFiles.DependenciesScript, WriteConfig(valid: false), _root, default));
        Assert.Equal($"RL worker exited 1; see {StderrLog}.", error.Message);
        Assert.Contains("parent_pid", File.ReadAllText(StderrLog));
        Assert.False(File.Exists(Path.Combine(_root, RlFiles.Dependencies)));
    }

    [ChildProcessTheory, MemberData(nameof(Targets))]
    public async Task ACancelledTokenStopsBeforeAnyProcessStarts(string target)
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DriverOf(target)(RlFiles.DependenciesScript, WriteConfig(valid: true), _root, cancelled.Token));
        Assert.False(File.Exists(Path.Combine(_root, RlFiles.Dependencies)));
    }

    [ChildProcessTheory, MemberData(nameof(Targets))]
    public async Task CancellingWhileTheWorkerRunsClosesItsInputAndWaitsForItToLeave(string target)
    {
        // The capture script does not read its input, so it finishes within the grace period; the pump reports the cancellation only after the worker is gone.
        using var cts = new CancellationTokenSource();
        var run = DriverOf(target)(RlFiles.DependenciesScript, WriteConfig(valid: true), _root, cts.Token);
        CancelFromADedicatedThread(cts, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        string result = Path.Combine(_root, RlFiles.Dependencies);
        Assert.True(File.Exists(result) || !File.Exists(result + ".tmp"), "the worker left a half-written result behind");
    }

    /// <summary>
    /// Cancels after <paramref name="delay"/> without the thread pool. <c>CancelAfter</c> runs its callback on a pool thread, and a parallel run of the whole suite can keep the
    /// pool saturated for longer than the capture script lives (about 0.4 s): the cancellation then arrives after the worker has finished and the run completes without
    /// the exception the test expects. A dedicated thread is not queued behind other tests.
    /// </summary>
    private static void CancelFromADedicatedThread(CancellationTokenSource source, TimeSpan delay) =>
        new Thread(() =>
        {
            Thread.Sleep(delay);
            try { source.Cancel(); } catch (ObjectDisposedException) { }   // the test already ended and disposed the source
        }) { IsBackground = true }.Start();

    // ---- the shared pump itself, driven with the managed interpreter and small inline scripts ----

    private ProcessStartInfo Python(string code)
    {
        var start = new ProcessStartInfo(RlWorkerProcess.ResolveInterpreter(RlFiles.DependenciesScript).Python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(code);
        return start;
    }

    private string Log => Path.Combine(_root, "pump.stderr.log");

    [ChildProcessFact]
    public async Task EveryFrameIsAnsweredInOrderAndTheAnswerReachesTheWorker()
    {
        var seen = new List<int>();
        await RlWorkerProcess.PumpAsync(Python("import sys,json\nfor i in (1,2,3):\n print(json.dumps({'i':i}),flush=True)\n sys.stderr.write('reply '+sys.stdin.readline())"),
            Log, (request, _) => { seen.Add(request.GetProperty("i").GetInt32()); return new Dictionary<string, int> { ["echo"] = seen.Count }; }, null, null, default);
        Assert.Equal(new[] { 1, 2, 3 }, seen);
        var replies = File.ReadAllLines(Log);
        Assert.Equal(new[] { "reply {\"echo\":1}", "reply {\"echo\":2}", "reply {\"echo\":3}" }, replies);
    }

    [ChildProcessFact]
    public async Task ProtocolOutputWithoutAHandlerIsRejected()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RlWorkerProcess.PumpAsync(Python("print('{}',flush=True)"), Log, null, null, null, default));
        Assert.Equal("Source worker wrote unexpected protocol output.", error.Message);
    }

    [ChildProcessFact]
    public async Task AnAnswerAtTheFrameBoundIsRejectedBeforeItIsSent()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RlWorkerProcess.PumpAsync(Python("print('{}',flush=True)"), Log,
            (_, _) => new string('x', RlContract.IpcFrameByteLimit), null, null, default));
        Assert.Equal("RL response exceeds frame bound.", error.Message);
    }

    [ChildProcessFact]
    public async Task TheHooksRunInOrderAroundTheStartAndTheProcessIdIsLive()
    {
        var order = new List<string>(); bool alive = false;
        await RlWorkerProcess.PumpAsync(Python("pass"), Log, null, () => order.Add("before"),
            pid => { order.Add("started"); alive = Process.GetProcessById(pid).Id == pid; }, default);
        Assert.Equal(new[] { "before", "started" }, order);
        Assert.True(alive);
    }

    [ChildProcessFact]
    public async Task AFailingBeforeStartHookPreventsTheProcessFromStarting()
    {
        bool started = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => RlWorkerProcess.PumpAsync(Python("open(r'" + Path.Combine(_root, "ran.txt") + "','w').close()"), Log, null,
            () => throw new InvalidDataException("identity changed"), _ => started = true, default));
        Assert.False(started);
        Assert.False(File.Exists(Path.Combine(_root, "ran.txt")));
    }

    [ChildProcessFact]
    public async Task AWorkerThatIgnoresItsClosedInputIsKilledAfterTheGracePeriodAndIsGone()
    {
        int pid = 0; using var cts = new CancellationTokenSource();
        var run = RlWorkerProcess.PumpAsync(Python("import time\ntime.sleep(300)"), Log, null, null, id => { pid = id; cts.CancelAfter(TimeSpan.FromMilliseconds(300)); }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.NotEqual(0, pid);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }
}
