using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using Xunit.Sdk;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>A fact that runs only when the application's embedded Python is installed; otherwise it is reported as skipped with the reason.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class EmbeddedPythonFactAttribute : FactAttribute
{
    public EmbeddedPythonFactAttribute()
    {
        if (!File.Exists(PythonEmbeddedInstaller.ExecutablePath(PythonEmbeddedDistribution.PythonExecutableName)))
        {
            Skip = "Needs the application's embedded Python (PythonEmbeddedInstaller.ExecutablePath) to be installed.";
        }
    }
}

/// <summary>
/// WP-3 + WP-4 end to end, nothing scripted: the real composition with <c>--enable-execution</c>, a real LightGBM training on a stored symbol of the
/// application's own Data folder, the status, a prediction with the model it produced, and the proof that the Data folder was not touched and that
/// the model is gone when the server stops.
/// </summary>
[Collection("ChildProcess")]
public sealed class ExecutionEndToEndTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(6);

    [EmbeddedPythonFact]
    public async Task ATrainingStartedThroughTheToolsProducesAModelThatThePredictionToolRunsAndLeavesDataUntouched()
    {
        string python = PythonEmbeddedInstaller.ExecutablePath(PythonEmbeddedDistribution.PythonExecutableName);
        if (await ModuleInstallProbe.RunAsync(python, "-c \"import lightgbm, polars, numpy\"", TimeSpan.FromMinutes(2)) != ModuleInstallState.Installed)
        {
            throw SkipException.ForSkip("The embedded Python cannot import lightgbm, polars and numpy.");
        }

        string dataRoot = PathDiscovery.ResolveDataPathWithoutCreating(null, "Data");
        string dailyFolder = Path.Combine(dataRoot, "Daily");
        string? symbol = Directory.Exists(dailyFolder)
            ? Directory.EnumerateFiles(dailyFolder, "*.parquet").OrderBy(f => f, StringComparer.Ordinal).Select(Path.GetFileNameWithoutExtension).FirstOrDefault()
            : null;
        if (symbol is null)
        {
            throw SkipException.ForSkip("The application's Data/Daily folder holds no daily file to train on.");
        }

        IReadOnlyList<string> before = Snapshot(dataRoot);
        string jobId;
        string whole;
        using (McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().Build(), new LogSink(new StringWriter()), enableExecution: true))
        {
            runtime.PrepareExecution();
            McpMessageDispatcher dispatcher = runtime.Dispatcher;
            await dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

            using JsonDocument start = (await McpTestSupport.CallAsync(dispatcher, "sa_start_training",
                $$$"""{"symbols":["{{{symbol}}}"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1,"nSplits":2,"hyperparameters":{"n-estimators":5}}"""))!;
            Assert.False(McpTestSupport.IsError(start), McpTestSupport.Structured(start).ToString());
            jobId = McpTestSupport.Structured(start).GetProperty("jobId").GetString()!;

            JsonElement status = await WaitForSucceededAsync(dispatcher, jobId);
            Assert.Equal(100, status.GetProperty("progressPercent").GetInt32());
            JsonElement artifact = status.GetProperty("artifact");
            Assert.True(artifact.GetProperty("validation").GetProperty("loads").GetBoolean());
            Assert.Equal(jobId, artifact.GetProperty("artifactId").GetString());
            Assert.Equal("model.onnx", artifact.GetProperty("onnxFileName").GetString());
            Assert.Equal(10, status.GetProperty("request").GetProperty("windowSize").GetInt32());

            using JsonDocument prediction = (await McpTestSupport.CallAsync(dispatcher, "sa_run_onnx_prediction", $$"""{"artifactId":"latest","symbol":"{{symbol}}","count":3}"""))!;
            Assert.False(McpTestSupport.IsError(prediction), McpTestSupport.Structured(prediction).ToString());
            JsonElement result = McpTestSupport.Structured(prediction);
            Assert.Equal(jobId, result.GetProperty("artifactId").GetString());
            Assert.Equal(artifact.GetProperty("sha256").GetString(), result.GetProperty("modelContract").GetProperty("sha256").GetString());
            Assert.Equal(10, result.GetProperty("modelContract").GetProperty("windowSize").GetInt32());
            JsonElement[] predictions = result.GetProperty("predictions").EnumerateArray().ToArray();
            Assert.Equal(3, predictions.Length);
            Assert.All(predictions, p => Assert.InRange(p.GetProperty("classScores").EnumerateArray().Sum(s => s.GetProperty("probability").GetDouble()), 0.999, 1.001));

            // AT-4.7: no response carries a drive letter, a back slash path, or the temp folder.
            whole = status.GetRawText() + result.GetRawText();
            using JsonDocument cancel = (await McpTestSupport.CallAsync(dispatcher, "sa_cancel_training", $$"""{"jobId":"{{jobId}}"}"""))!;
            Assert.Equal("Succeeded", McpTestSupport.Structured(cancel).GetProperty("state").GetString());
        }

        Assert.DoesNotContain(":\\\\", whole);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/').Replace("\\", "\\\\"), whole);
        Assert.DoesNotContain("ephemeral", whole);

        // AT-4.5: the Data folder is byte-for-byte as it was (names, sizes, write times), and the model went with the server.
        Assert.Equal(before, Snapshot(dataRoot));
        Assert.False(Directory.Exists(EphemeralSandbox.Root) && Directory.EnumerateDirectories(EphemeralSandbox.Root, jobId, SearchOption.AllDirectories).Any());
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
