using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using Xunit.Sdk;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// A fact that runs only when the managed reinforcement-learning environment exists (the application's own <c>StockAnalyzer.Python/.venv</c>) and the heavy
/// child-process tests are switched on (<c>SA_ENABLE_CHILD_PROCESS_TESTS=1</c>, as for every test that starts a trainer); otherwise it is reported as skipped with the reason.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RlEnvironmentFactAttribute : FactAttribute
{
    public const string EnabledVariable = "SA_ENABLE_CHILD_PROCESS_TESTS";

    public RlEnvironmentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
        {
            Skip = $"Opt-in heavy child process test (starts the RL trainer); set {EnabledVariable}=1 to run.";
        }
        else if (!File.Exists(RlPython()))
        {
            Skip = "Needs the managed RL environment (StockAnalyzer.Python/.venv with the pinned RL packages).";
        }
    }

    private static string RlPython()
    {
        try
        {
            string script = PythonScriptLocator.Resolve(RlFiles.TrainerScript);
            string pythonRoot = Directory.GetParent(Path.GetDirectoryName(script)!)!.FullName;
            return Path.Combine(pythonRoot, RlFiles.VirtualEnvironment, OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
        }
        catch (FileNotFoundException)
        {
            return "";
        }
    }
}

/// <summary>
/// MP-3 end to end, nothing scripted: the real composition with <c>--enable-execution</c>, a real RL smoke run on a stored equity of the application's own Data
/// folder with the period proposed by the application's own rule, the status, and the proof that the Data folder was not touched and that no file of the run is left.
/// </summary>
[Collection("ChildProcess")]
public sealed class RlExecutionEndToEndTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);
    private const int SymbolsToTry = 25;

    [RlEnvironmentFact]
    public async Task ASmokeRunStartedThroughTheToolsReportsItsResultAndLeavesDataUntouchedAndNoFileBehind()
    {
        string dataRoot = PathDiscovery.ResolveDataPathWithoutCreating(null, "Data");
        string dailyFolder = Path.Combine(dataRoot, "Daily");
        string[] symbols = Directory.Exists(dailyFolder)
            ? Directory.EnumerateFiles(dailyFolder, "*.parquet").OrderBy(f => f, StringComparer.Ordinal).Select(Path.GetFileNameWithoutExtension).OfType<string>().Take(SymbolsToTry).ToArray()
            : Array.Empty<string>();
        if (symbols.Length == 0)
        {
            throw SkipException.ForSkip("The application's Data/Daily folder holds no daily file to train on.");
        }

        IReadOnlyList<string> before = Snapshot(dataRoot);
        string? jobId = null;
        string whole = "";
        using (McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().Build(), new LogSink(new StringWriter()), enableExecution: true))
        {
            runtime.PrepareExecution();
            McpMessageDispatcher dispatcher = runtime.Dispatcher;
            await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

            // The first stored symbol that is an equity with enough final bars (the others are refused with INSUFFICIENT_MARKET_DATA before any job exists).
            foreach (string symbol in symbols)
            {
                using JsonDocument start = (await McpTestSupport.CallAsync(dispatcher, "sa_start_rl_training", $$"""{"symbol":"{{symbol}}"}"""))!;
                if (McpTestSupport.IsError(start))
                {
                    Assert.Equal("INSUFFICIENT_MARKET_DATA", McpTestSupport.ErrorCodeOf(start));
                    continue;
                }

                jobId = McpTestSupport.Structured(start).GetProperty("jobId").GetString()!;
                break;
            }

            if (jobId is null)
            {
                throw SkipException.ForSkip($"None of the first {SymbolsToTry} stored symbols is an equity with enough stored bars.");
            }

            JsonElement status = await WaitForSucceededAsync(dispatcher, jobId);
            Assert.Equal("reinforcement_learning", status.GetProperty("kind").GetString());
            JsonElement request = status.GetProperty("request");
            Assert.True(request.GetProperty("smokeOnly").GetBoolean());
            Assert.True(DateOnly.ParseExact(request.GetProperty("startDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                < DateOnly.ParseExact(request.GetProperty("endDateExclusive").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            JsonElement result = status.GetProperty("result");
            Assert.True(decimal.TryParse(result.GetProperty("validationMeanReturn").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            Assert.Equal(RlContract.CandidateSeeds[0], result.GetProperty("candidateSeed").GetInt32());
            Assert.Equal(RlContract.SmokeTrainingTransitions, result.GetProperty("trainingTransitions").GetInt32());
            Assert.True(result.GetProperty("model").GetProperty("sizeBytes").GetInt64() > 0);
            Assert.Matches("^[0-9a-f]{64}$", result.GetProperty("model").GetProperty("sha256").GetString()!);
            Assert.False(status.TryGetProperty("artifact", out _));

            // The model of a reinforcement-learning run is not a prediction model: nothing offers it to the prediction tool.
            using JsonDocument prediction = (await McpTestSupport.CallAsync(dispatcher, "sa_run_onnx_prediction", $$"""{"artifactId":"{{jobId}}","symbol":"{{symbols[0]}}"}"""))!;
            Assert.Equal("ARTIFACT_NOT_FOUND", McpTestSupport.ErrorCodeOf(prediction));

            // No file of the run is left, even while the server runs (the job folder is removed when the job ends).
            await WaitUntilAsync(() => !JobFolderExists(jobId));
            whole = status.GetRawText();
        }

        Assert.DoesNotContain(":\\\\", whole);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/').Replace("\\", "\\\\"), whole);
        Assert.DoesNotContain("ephemeral", whole);

        // The Data folder is byte-for-byte as it was (names, sizes, write times).
        Assert.Equal(before, Snapshot(dataRoot));
    }

    private static bool JobFolderExists(string jobId)
        => Directory.Exists(EphemeralSandbox.Root) && Directory.EnumerateDirectories(EphemeralSandbox.Root, jobId, SearchOption.AllDirectories).Any();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "The job folder was not removed in time.");
            await Task.Delay(100);
        }
    }

    private static async Task<JsonElement> WaitForSucceededAsync(McpMessageDispatcher dispatcher, string jobId)
    {
        DateTime limit = DateTime.UtcNow + Patience;
        while (true)
        {
            using JsonDocument response = (await McpTestSupport.CallAsync(dispatcher, "sa_get_training_status", $$"""{"jobId":"{{jobId}}"}"""))!;
            Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
            JsonElement status = McpTestSupport.Structured(response).Clone();
            string state = status.GetProperty("state").GetString()!;
            if (state == "Succeeded")
            {
                return status;
            }

            Assert.True(state is "Running" or "Validating", $"The job ended as {state}: {status}");
            Assert.True(DateTime.UtcNow < limit, $"The job did not finish in time: {status}");
            await Task.Delay(500);
        }
    }

    private static IReadOnlyList<string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file =>
            {
                var info = new FileInfo(file);
                return $"{Path.GetRelativePath(root, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            })
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();
}
