#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// One real RL smoke run (the managed <c>.venv</c> worker) with a source folder, a data root, a model store and a lock folder that all live in a throw-away
/// tree. It proves, by measurement, the premise of the sandboxed RL job: the source tree is byte-identical afterwards and every write of the run is below
/// the run's own data root. Opt-in (starts a Python process); it needs <c>StockAnalyzer.Python/.venv</c> with the pinned RL packages.
/// </summary>
[Collection("ChildProcess")]
public sealed class RlSmokeSandboxRealTests : IDisposable
{
    private const int StoredBars = 1_500;
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_real_" + Guid.NewGuid().ToString("N"));

    public RlSmokeSandboxRealTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [ChildProcessFact]
    public async Task ASmokeRunWritesOnlyBelowItsOwnRootAndLeavesTheSourceTreeUntouched()
    {
        string python = RlWorkerProcess.ResolveInterpreter(RlFiles.TrainerScript).Python;
        if (!File.Exists(python))
        {
            throw SkipException.ForSkip("StockAnalyzer.Python/.venv does not exist.");
        }

        string source = Path.Combine(_root, "source");
        RlSourceFixture.WriteVaryingSymbol(source, "ABC", RlSourceFixture.FirstDay, StoredBars);
        IReadOnlyList<string> before = Fingerprint(source);

        string jobRoot = Path.Combine(_root, "job");
        using var registry = new ModelGenerationRegistry(Path.Combine(jobRoot, "registry"));
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(jobRoot, "locks")), dataRoot: jobRoot)
        {
            LocalSourceFolders = new RlLocalSourceFolders(Path.Combine(source, "Daily"), Path.Combine(source, "Metadata")),
        };
        var job = new RlTrainingJob("ABC", RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(StoredBars),
            new BacktestConfiguration { InitialCapital = BacktestDefaults.InitialCapital, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = RlContract.FixedQuantity }, SmokeOnly: true);

        var clock = Stopwatch.StartNew();
        RlTrainingResult result = await service.RunAsync(job, null, CancellationToken.None);
        clock.Stop();

        Assert.True(result.SmokeOnly);
        Assert.Null(result.ModelId);
        Assert.True(File.Exists(result.ModelPath));
        Assert.True(File.Exists(result.EvaluationPath));
        Assert.StartsWith(Path.GetFullPath(jobRoot), Path.GetFullPath(result.Directory), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Fingerprint(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(jobRoot, "registry"), "*", SearchOption.TopDirectoryOnly)
            .Where(entry => Path.GetFileName(entry) == "RL" && Directory.EnumerateFileSystemEntries(entry).Any()));

        string[] files = Directory.EnumerateFiles(jobRoot, "*", SearchOption.AllDirectories).ToArray();
        // The sandbox path budget rests on this measured value (ExecutionLimits.RlLongestRelativePathChars).
        Assert.InRange(files.Max(f => Path.GetRelativePath(jobRoot, f).Length), 1, StockAnalyzer.Core.Execution.ExecutionLimits.RlLongestRelativePathChars);
        _output.WriteLine($"smoke run: {clock.Elapsed.TotalSeconds:F1} s; {files.Length} files; {files.Sum(f => new FileInfo(f).Length)} bytes; "
            + $"longest relative path {files.Max(f => Path.GetRelativePath(jobRoot, f).Length)} chars; "
            + $"files: {string.Join(", ", files.Select(f => Path.GetRelativePath(jobRoot, f)).OrderBy(f => f, StringComparer.Ordinal))}");
    }

    private static IReadOnlyList<string> Fingerprint(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => $"{Path.GetRelativePath(folder, file)}|{ContentHash.OfFile(file)}|{File.GetLastWriteTimeUtc(file).Ticks}")
            .OrderBy(entry => entry, StringComparer.Ordinal).ToArray();
}
