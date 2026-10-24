#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.TestSupport;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// Ephemeral mode (MP-2 / WP-1): a training run whose <see cref="TrainingJobConfig.ArtifactsDirectory"/> is set writes only inside that folder, which must lie
/// below the sandbox root. The Python facts use the application's embedded interpreter and are skipped, with the reason, when it (or a framework) is missing.
/// </summary>
[Collection("ChildProcess")]
public sealed class TrainingEphemeralModeTests
{
    private const string TrainingScript = "run_training.py";
    private const string JobId = "20260101-000000000-abcdef12";
    private static readonly TimeSpan PythonTimeout = TimeSpan.FromMinutes(6);
    private static readonly string[] AuditFrameworks = { "lightgbm", "torch", "tensorflow" };
    private static readonly string[] AuditHyperparameterNameParts = { "epoch", "estimators", "rounds" };

    // ---- AT-2.1 Validate ---------------------------------------------------------------------------------------------

    private static string Inside(params string[] parts) => Path.Combine(new[] { EphemeralSandbox.Root }.Concat(parts).ToArray());

    public static TheoryData<string> RejectedDirectories() => new()
    {
        string.Empty,
        "   ",
        Path.Combine("ephemeral", "session", "job"),
        Path.Combine(Path.GetTempPath(), "somewhere_else", "job"),
        EphemeralSandbox.Root,
        EphemeralSandbox.Root + Path.DirectorySeparatorChar,
        Path.GetDirectoryName(EphemeralSandbox.Root)!,
        EphemeralSandbox.Root + "_sibling" + Path.DirectorySeparatorChar + "job",
        Inside("session", "..", "..", "escape"),
        Inside("..", "escape"),
    };

    public static TheoryData<string> AcceptedDirectories() => new()
    {
        Inside("session", JobId),
        Inside("session", JobId) + Path.DirectorySeparatorChar,
        Inside("session", "..", "other", JobId),
        Inside(JobId),
    };

    [Fact]
    public void Validate_NoArtifactsDirectoryIsTheNormalModeAndNeedsNothing()
    {
        TrainingJobConfig config = Config();

        config.Validate();

        Assert.Null(config.ArtifactsDirectory);
    }

    [Theory]
    [MemberData(nameof(RejectedDirectories))]
    public void Validate_ADirectoryOutsideTheSandboxOrNotAbsoluteIsRefusedNamingTheArgument(string directory)
    {
        var error = Assert.Throws<ArgumentException>(() => (Config() with { ArtifactsDirectory = directory, RunId = JobId }).Validate());

        Assert.Equal(nameof(TrainingJobConfig.ArtifactsDirectory), error.ParamName);
    }

    [Theory]
    [MemberData(nameof(AcceptedDirectories))]
    public void Validate_ADirectoryBelowTheSandboxRootIsAccepted(string directory)
        => (Config() with { ArtifactsDirectory = directory, RunId = JobId }).Validate();

    // ---- R-1: the run id is required in ephemeral mode and names the job folder ----------------------------------------------

    [Fact]
    public void Validate_AnEphemeralRunWithoutARunIdIsRefusedNamingRunId()
    {
        var error = Assert.Throws<ArgumentException>(() => (Config() with { ArtifactsDirectory = Inside("session", JobId) }).Validate());

        Assert.Equal(nameof(TrainingJobConfig.RunId), error.ParamName);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("20260101-000000000-ABCDEF12")]
    [InlineData("20260101-000000000-abcdef1")]
    [InlineData("20260101-00000000-abcdef12")]
    [InlineData("20260101-000000000-abcdef123")]
    [InlineData(" 20260101-000000000-abcdef12")]
    public void Validate_AnEphemeralRunIdInAnotherFormatIsRefusedNamingRunId(string runId)
    {
        var error = Assert.Throws<ArgumentException>(() => (Config() with { ArtifactsDirectory = Inside("session", runId.Trim()), RunId = runId }).Validate());

        Assert.Equal(nameof(TrainingJobConfig.RunId), error.ParamName);
    }

    [Fact]
    public void Validate_TheLastFolderMustBeTheRunId()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            (Config() with { ArtifactsDirectory = Inside("session", "20260101-000000000-00000000"), RunId = JobId }).Validate());

        Assert.Equal(nameof(TrainingJobConfig.ArtifactsDirectory), error.ParamName);
    }

    [Fact]
    public void Validate_ANormalRunKeepsItsFreedomAboutTheRunId()
    {
        (Config() with { RunId = null }).Validate();
        (Config() with { RunId = "anything-the-orchestrator-overwrites" }).Validate();
    }

    [Theory]
    [InlineData("20260101-000000000-abcdef12", true)]
    [InlineData("99999999-999999999-00000000", true)]
    [InlineData("", false)]
    [InlineData("20260101-000000000-abcdef12\n", false)]
    [InlineData("20260101-000000000-ABCDEF12", false)]
    [InlineData("2026-01-01-000000000-abcdef12", false)]
    public void RunId_TheFormatIsExactlyTheDocumentedOne(string value, bool valid)
        => Assert.Equal(valid, TrainingRunId.IsValid(value));

    [Fact]
    public void RunId_NewProducesValidDistinctIdsStampedWithTheGivenUtcTime()
    {
        var at = new DateTimeOffset(2026, 10, 24, 1, 2, 3, 456, TimeSpan.Zero);

        string first = TrainingRunId.New(at);
        string second = TrainingRunId.New(at);

        Assert.True(TrainingRunId.IsValid(first));
        Assert.StartsWith("20261024-010203456-", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SandboxRoot_IsTheTempFolderNameThenEphemeral()
        => Assert.Equal(Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName, "ephemeral"), EphemeralSandbox.Root);

    // ---- AT-2.2 wire format ------------------------------------------------------------------------------------------

    [Fact]
    public void Wire_WithoutADirectoryTheKeyIsNeverWritten()
    {
        string json = TrainingConfigJson.Serialize(Config());

        Assert.DoesNotContain("artifacts_directory", json, StringComparison.Ordinal);
        Assert.Null(TrainingConfigJson.DeserializeConfig(json).ArtifactsDirectory);
    }

    [Fact]
    public void Wire_ALegacyConfigWithoutTheKeyStillReads()
    {
        const string Legacy = """{"symbols":["A"],"architecture":"gbdt","window_size":10,"horizon":1,"framework":"lightgbm"}""";

        TrainingJobConfig config = TrainingConfigJson.DeserializeConfig(Legacy);

        Assert.Null(config.ArtifactsDirectory);
        Assert.Equal("gbdt", config.Architecture);
    }

    [Fact]
    public void Wire_ADirectoryIsWrittenAsArtifactsDirectoryAndRoundTrips()
    {
        string directory = Inside("session", JobId);
        string json = TrainingConfigJson.Serialize(Config() with { ArtifactsDirectory = directory, RunId = JobId });

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(directory, document.RootElement.GetProperty("artifacts_directory").GetString());
        Assert.Equal(directory, TrainingConfigJson.DeserializeConfig(json).ArtifactsDirectory);
    }

    // ---- the Python side mirrors the two folder names ----------------------------------------------------------------

    [Fact]
    public void Python_TheSandboxFolderNamesEqualTheCSharpOnes()
    {
        string script = File.ReadAllText(Path.Combine(TrainingDirectory(), TrainingScript));

        Assert.Equal(PathDiscovery.TempFolderName, PythonString(script, "EPHEMERAL_TEMP_FOLDER"));
        Assert.Equal(EphemeralSandbox.FolderName, PythonString(script, "EPHEMERAL_SANDBOX_FOLDER"));
    }

    // ---- AT-2.3 the Python self-check (it includes the ephemeral cases) -------------------------------------------------

    [EmbeddedPythonFact]
    public async Task Python_TheSelfCheckPassesIncludingTheEphemeralFolderRules()
    {
        string python = EmbeddedPython();
        await RequireModulesAsync(python, "numpy", "polars", "lightgbm", "torch");
        var start = new ProcessStartInfo(python) { WorkingDirectory = TrainingDirectory() };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(TrainingDirectory(), TrainingScript));
        start.ArgumentList.Add("--selfcheck");

        string output = await TrainingPythonTestRunner.RunAsync(start, PythonTimeout);

        Assert.Contains("run_training.py selfcheck: OK", output);
    }

    // ---- AT-2.4 write audit ------------------------------------------------------------------------------------------

    [EmbeddedPythonFact]
    public async Task Audit_AnEphemeralRunLeavesTheDataTreeAndTheScriptFolderByteIdentical()
    {
        string python = EmbeddedPython();
        string module = await FirstInstalledFrameworkAsync(python);
        TrainingFramework framework = module switch
        {
            "lightgbm" => TrainingFramework.LightGBM,
            "torch" => TrainingFramework.PyTorch,
            _ => TrainingFramework.TensorFlow,
        };
        // The audit tree has the layout the scripts expect (<root>/StockAnalyzer.Python/training next to <root>/Data), so the trainer's data root is the copy.
        string auditRoot = TestDataIsolationBootstrap.NewTempDirectory("EphemeralAudit");
        string trainingCopy = Path.Combine(auditRoot, "StockAnalyzer.Python", "training");
        string dailyDirectory = Path.Combine(auditRoot, "Data", "Daily");
        string jobId = "20260101-000000000-" + Guid.NewGuid().ToString("N")[..8];
        string sessionDirectory = Inside("audit-" + Guid.NewGuid().ToString("N"));
        string artifacts = Path.Combine(sessionDirectory, jobId);
        try
        {
            Directory.CreateDirectory(trainingCopy);
            Directory.CreateDirectory(dailyDirectory);
            foreach (string file in Directory.EnumerateFiles(TrainingDirectory()).Where(f => f.EndsWith(".py", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal)))
            {
                File.Copy(file, Path.Combine(trainingCopy, Path.GetFileName(file)));
            }

            const string Symbol = "SYM";
            ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(dailyDirectory, Symbol, RandomWalk(400));

            string architecture = framework == TrainingFramework.LightGBM ? TrainingEnsembleContract.LightGbmArchitecture : "lstm";
            TrainingJobConfig config = new TrainingJobConfig
            {
                Symbols = new[] { Symbol },
                Framework = framework,
                Architecture = architecture,
                WindowSize = 10,
                Horizon = 1,
                NSplits = 2,
                RunId = jobId,
                ArtifactsDirectory = artifacts,
                Hyperparameters = SmallestHyperparameters(framework, architecture),
            };
            config.Validate();
            // The job file lives outside the audited tree, like the orchestrator's own temp job file.
            string configFile = Path.Combine(Path.GetTempPath(), "sa_audit_job_" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(configFile, TrainingConfigJson.Serialize(config), TrainingConfigJson.Utf8NoBom);
            IReadOnlyList<string> before = ReadOnlyDataTreeFixture.SnapshotOf(auditRoot);

            try
            {
                var start = new ProcessStartInfo(python) { WorkingDirectory = trainingCopy };
                start.ArgumentList.Add("-u");
                start.ArgumentList.Add(Path.Combine(trainingCopy, TrainingScript));
                start.ArgumentList.Add("--config");
                start.ArgumentList.Add(configFile);
                start.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; // what the orchestrator sets for an ephemeral run
                await TrainingPythonTestRunner.RunAsync(start, PythonTimeout);
            }
            finally
            {
                File.Delete(configFile);
            }

            Assert.Single(Directory.EnumerateFiles(artifacts, "*.onnx"));
            Assert.Single(Directory.EnumerateFiles(artifacts, "*.onnx.metrics.json"));
            IReadOnlyList<string> after = ReadOnlyDataTreeFixture.SnapshotOf(auditRoot);
            Assert.Equal(before, after);
            Assert.Empty(Directory.EnumerateDirectories(auditRoot, "__pycache__", SearchOption.AllDirectories));
        }
        finally
        {
            TryDelete(sessionDirectory);
            TryDelete(auditRoot);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static TrainingJobConfig Config() => new()
    {
        Symbols = new[] { "A" },
        Framework = TrainingFramework.LightGBM,
        Architecture = TrainingEnsembleContract.LightGbmArchitecture,
        WindowSize = 10,
        Horizon = 1,
    };

    private static IReadOnlyDictionary<string, string> SmallestHyperparameters(TrainingFramework framework, string architecture)
        => TrainingHyperparameterCatalog.For(framework, architecture)
            .Where(d => d.Domain is { Kind: HyperparameterKind.Integer, Minimum: not null }
                && AuditHyperparameterNameParts.Any(part => d.Key.Contains(part, StringComparison.Ordinal)))
            .ToDictionary(d => d.Key, d => ((long)d.Domain!.Minimum!.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static IReadOnlyList<decimal> RandomWalk(int days)
    {
        var random = new Random(7);
        var closes = new List<decimal>(days);
        decimal close = 100m;
        for (int i = 0; i < days; i++)
        {
            close = Math.Max(5m, close * (1m + (decimal)(random.NextDouble() * 0.06 - 0.03)));
            closes.Add(Math.Round(close, 4));
        }

        return closes;
    }

    private static string PythonString(string script, string name)
    {
        Match match = Regex.Match(script, "^" + Regex.Escape(name) + @"\s*=\s*""([^""]*)""", RegexOptions.Multiline);
        Assert.True(match.Success, $"{TrainingScript} defines no {name}");
        return match.Groups[1].Value;
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private static string TrainingDirectory() => Path.Combine(RepositoryRoot(), "StockAnalyzer.Python", "training");

    private static string EmbeddedPython()
        => PythonEmbeddedInstaller.ExecutablePath(PythonEmbeddedDistribution.PythonExecutableName);

    private static async Task RequireModulesAsync(string python, params string[] modules)
    {
        foreach (string module in modules)
        {
            if (await ModuleInstallProbe.RunAsync(python, $"-c \"import {module}\"", TimeSpan.FromMinutes(2)) != ModuleInstallState.Installed)
            {
                throw SkipException.ForSkip($"The embedded Python cannot import '{module}'.");
            }
        }
    }

    private static async Task<string> FirstInstalledFrameworkAsync(string python)
    {
        await RequireModulesAsync(python, "numpy", "polars");
        foreach (string module in AuditFrameworks)
        {
            if (await ModuleInstallProbe.RunAsync(python, $"-c \"import {module}\"", TimeSpan.FromMinutes(2)) == ModuleInstallState.Installed)
            {
                return module;
            }
        }

        throw SkipException.ForSkip($"The embedded Python cannot import any of: {string.Join(", ", AuditFrameworks)}.");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // best-effort: the OS temp cleaner removes the rest
        }
    }
}
