using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>An execution gateway whose answers a test supplies (a real implementation of the interface; nothing is trained).</summary>
internal sealed class FakeExecutionGateway : ISandboxedExecutionGateway
{
    public const string JobId = "20260101-120000000-abcdef12";

    public List<EphemeralTrainingRequest> Requests { get; } = new();

    public List<string> LeaseRequests { get; } = new();

    public int Released;

    public Func<EphemeralTrainingRequest, Task<TrainingJobTicket>> Start { get; set; }
        = _ => Task.FromResult(new TrainingJobTicket(JobId, TrainingJobState.Running));

    public List<EphemeralRlRequest> RlRequests { get; } = new();

    public Func<EphemeralRlRequest, Task<TrainingJobTicket>> StartRl { get; set; }
        = _ => Task.FromResult(new TrainingJobTicket(JobId, TrainingJobState.Running));

    public Func<string, TrainingJobStatus> Status { get; set; } = jobId => throw new JobNotFoundException(jobId);

    public Func<string, TrainingJobState> CancelJob { get; set; } = jobId => throw new JobNotFoundException(jobId);

    public Func<string, ArtifactLease> Lease { get; set; } = id => throw new ArtifactNotFoundException(id);

    public Task<TrainingJobTicket> StartTrainingAsync(EphemeralTrainingRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Start(request);
    }

    public Task<TrainingJobTicket> StartRlTrainingAsync(EphemeralRlRequest request, CancellationToken cancellationToken)
    {
        RlRequests.Add(request);
        return StartRl(request);
    }

    public TrainingJobStatus GetStatus(string jobId) => Status(jobId);

    public TrainingJobState Cancel(string jobId) => CancelJob(jobId);

    public EphemeralArtifact? FindArtifact(string artifactIdOrLatest) => null;

    public ArtifactLease LeaseArtifact(string artifactId)
    {
        LeaseRequests.Add(artifactId);
        return Lease(artifactId);
    }
}

/// <summary>
/// WP-3 / WP-4 at the tool level: the four execution tools through the real composition (the real dispatcher, the real data tool layer, a throw-away data tree),
/// with a scripted execution gateway. The real gateway, the real trainer and a real model are covered by the Core tests and the child-process test below.
/// </summary>
public sealed class ExecutionToolsTests : IDisposable
{
    private const string Aaa = "AAA";
    private const string Bbb = "BBB-X";
    private const int Bars = 60;
    private const int ShortBars = 8;
    private static readonly DateTime FirstBar = new(2024, 1, 1);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Bars, Aaa, "bbb.x");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly FakeExecutionGateway _gateway = new();

    public ExecutionToolsTests()
    {
        Reseed(Aaa, Bars, seed: 5);
        Reseed(Bbb, ShortBars, seed: 9);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    // ---- hosting ------------------------------------------------------------------------------------------------------------

    private void Reseed(string symbol, int bars, int seed)
    {
        File.Delete(Path.Combine(_fixture.DailyDirectory, $"{symbol}.parquet"));
        var random = new Random(seed);
        var closes = new List<decimal>(bars);
        decimal close = 100m;
        for (int day = 0; day < bars; day++)
        {
            close = Math.Max(5m, close * (1m + (decimal)(random.NextDouble() * 0.06 - 0.03)));
            closes.Add(Math.Round(close, 4));
        }

        StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, symbol, closes);
    }

    private IConfiguration Configuration(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        foreach ((string key, string value) in overrides)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<McpMessageDispatcher> StartAsync(bool enableExecution = true, params (string Key, string Value)[] overrides)
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            Configuration(overrides), new LogSink(new StringWriter()), enableExecution: enableExecution, executionGateway: enableExecution ? _gateway : null);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string argumentsJson = "{}")
        => (await McpTestSupport.CallAsync(dispatcher, tool, argumentsJson))!;

    private static string[] ToolNames(JsonDocument toolsList)
        => toolsList.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();

    private static async Task<JsonDocument> ListToolsAsync(McpMessageDispatcher dispatcher)
        => JsonDocument.Parse((await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!);

    private static readonly string[] ExecutionToolNames = { "sa_start_training", "sa_get_training_status", "sa_cancel_training", "sa_run_onnx_prediction", "sa_start_rl_training" };

    // ---- AT-4.1 / AT-4.3: offered only on request, with honest annotations --------------------------------------------------------

    [Fact]
    public async Task WithoutTheFlagNoExecutionToolIsListedAndCallingOneIsAnUnknownTool()
    {
        McpMessageDispatcher dispatcher = await StartAsync(enableExecution: false);

        using JsonDocument list = await ListToolsAsync(dispatcher);
        Assert.Empty(ToolNames(list).Intersect(ExecutionToolNames));
        using JsonDocument call = await CallAsync(dispatcher, "sa_start_training", """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""");

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, call.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("Unknown tool", call.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task WithoutTheFlagTheExecutionSettingsAreNeverReadAndWithItABadOneIsRefusedAtStartUp()
    {
        (string, string) bad = ("AiIntegration:Execution:KeepLatestRuns", "99");

        await StartAsync(enableExecution: false, bad); // not read: no failure

        var error = Assert.Throws<ExecutionLimitsException>(() => McpServerComposition.Create(Configuration(bad), new LogSink(new StringWriter()), enableExecution: true));
        Assert.Contains("KeepLatestRuns", error.Message);
    }

    [Fact]
    public void TheNumberOfBarsOfOnePredictionCannotExceedTheCandleLimit()
    {
        var error = Assert.Throws<ExecutionLimitsException>(() => McpServerComposition.Create(
            Configuration(("AiIntegration:MaxCandlesPerRequest", "500"), ("AiIntegration:Execution:MaxPredictionBars", "501")),
            new LogSink(new StringWriter()),
            enableExecution: true));

        Assert.Contains("MaxPredictionBars", error.Message);
        Assert.Contains("MaxCandlesPerRequest", error.Message);
    }

    [Fact]
    public async Task WithTheFlagTheFourToolsAreListedAndOnlyTheyAreNotReadOnly()
    {
        McpMessageDispatcher dispatcher = await StartAsync();

        using JsonDocument list = await ListToolsAsync(dispatcher);

        Assert.Empty(ExecutionToolNames.Except(ToolNames(list)));
        foreach (JsonElement tool in list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            JsonElement hints = tool.GetProperty("annotations");
            bool execution = ExecutionToolNames.Contains(tool.GetProperty("name").GetString());
            Assert.Equal(!execution, hints.GetProperty("readOnlyHint").GetBoolean());
            Assert.Equal(!execution, hints.GetProperty("idempotentHint").GetBoolean());
            Assert.False(hints.GetProperty("destructiveHint").GetBoolean());
            Assert.False(hints.GetProperty("openWorldHint").GetBoolean());
        }
    }

    [Fact]
    public async Task TheGatewayIsBuiltOnlyWhenPreparedOrUsed()
    {
        int built = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ISandboxedExecutionGateway>(_ =>
        {
            built++;
            return _gateway;
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        var deferred = new DeferredSandboxedExecutionGateway(provider);
        Assert.Equal(0, built);

        deferred.Prepare();
        deferred.Prepare();
        await deferred.StartTrainingAsync(Request(), CancellationToken.None);
        Assert.Equal(1, built);
        Assert.Throws<JobNotFoundException>(() => deferred.GetStatus(FakeExecutionGateway.JobId));
    }

    [Fact]
    public async Task ATemporaryFailureToBuildTheGatewayIsTriedAgainByTheNextCallAndNeverBuildsTwoGateways()
    {
        int attempts = 0;
        int built = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ISandboxedExecutionGateway>(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("The disk is full.");
            }

            Interlocked.Increment(ref built);
            return _gateway;
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        var deferred = new DeferredSandboxedExecutionGateway(provider);

        Assert.Throws<IOException>(deferred.Prepare); // the start-up attempt fails; the server keeps serving
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => deferred.StartTrainingAsync(Request(), CancellationToken.None))));
        Assert.Throws<JobNotFoundException>(() => deferred.GetStatus(FakeExecutionGateway.JobId));

        Assert.Equal(2, attempts);
        Assert.Equal(1, built);
    }

    [Fact]
    public void AWrongSettingIsPermanentAndIsReportedAgainWithoutBuildingAgain()
    {
        int attempts = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ISandboxedExecutionGateway>(_ =>
        {
            attempts++;
            throw new ExecutionLimitsException("AiIntegration:Execution:KeepLatestRuns is 0.");
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        var deferred = new DeferredSandboxedExecutionGateway(provider);

        var first = Assert.Throws<ExecutionLimitsException>(deferred.Prepare);
        var second = Assert.Throws<ExecutionLimitsException>(() => deferred.GetStatus(FakeExecutionGateway.JobId));

        Assert.Same(first, second);
        Assert.Equal(1, attempts);
    }

    // ---- sa_get_capabilities: the execution block ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Capabilities_SayThatExecutionIsOffWithoutTheFlag()
    {
        using JsonDocument response = await CallAsync(await StartAsync(enableExecution: false), "sa_get_capabilities");

        JsonElement execution = McpTestSupport.Structured(response).GetProperty("execution");
        Assert.False(execution.GetProperty("enabled").GetBoolean());
        Assert.Single(execution.EnumerateObject());
    }

    [Fact]
    public async Task Capabilities_ListWhatTheTrainingToolAcceptsStraightFromTheApplicationsTables()
    {
        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_capabilities");

        JsonElement execution = McpTestSupport.Structured(response).GetProperty("execution");
        Assert.True(execution.GetProperty("enabled").GetBoolean());
        Assert.Equal(Enum.GetNames<TrainingFramework>(), execution.GetProperty("frameworks").EnumerateArray().Select(f => f.GetString()).ToArray());
        foreach (TrainingFramework framework in Enum.GetValues<TrainingFramework>())
        {
            Assert.Equal(TrainingArchitectureCatalog.For(framework), execution.GetProperty("architectures").GetProperty(framework.ToString()).EnumerateArray().Select(a => a.GetString()).ToArray());
            Assert.Equal(TrainingArchitectureCatalog.DefaultFor(framework), execution.GetProperty("defaultArchitectures").GetProperty(framework.ToString()).GetString());
        }

        JsonElement[] hyperparameters = execution.GetProperty("hyperparameters").EnumerateArray().ToArray();
        Assert.Equal(TrainingHyperparameterCatalog.All.Select(d => d.Key), hyperparameters.Select(h => h.GetProperty("key").GetString()));
        JsonElement estimators = hyperparameters.Single(h => h.GetProperty("key").GetString() == "n-estimators");
        Assert.Equal("200", estimators.GetProperty("default").GetString());
        Assert.Equal("Integer", estimators.GetProperty("kind").GetString());
        Assert.Equal(1, estimators.GetProperty("minimum").GetDouble());

        JsonElement fixedValues = execution.GetProperty("fixed");
        Assert.Equal(EphemeralFixedValues.Current.FeatureMode.ToString(), fixedValues.GetProperty("featureMode").GetString());
        Assert.Equal("Daily", fixedValues.GetProperty("timeframe").GetString());
        Assert.False(fixedValues.GetProperty("calibration").GetBoolean());

        JsonElement limits = execution.GetProperty("limits");
        var standard = new SandboxedExecutionSettings();
        Assert.Equal(standard.KeepLatestRuns, limits.GetProperty("keepLatestRuns").GetInt32());
        Assert.Equal(standard.MaxPredictionBars, limits.GetProperty("maxPredictionBars").GetInt32());
        Assert.Equal(ExecutionLimits.MaxStageChars, limits.GetProperty("maxStageChars").GetInt32());
    }

    [Fact]
    public async Task Capabilities_TheLimitsFollowTheConfiguration()
    {
        using JsonDocument response = await CallAsync(
            await StartAsync(true, ("AiIntegration:Execution:KeepLatestRuns", "3"), ("AiIntegration:Execution:MaxPredictionBars", "20")), "sa_get_capabilities");

        JsonElement limits = McpTestSupport.Structured(response).GetProperty("execution").GetProperty("limits");
        Assert.Equal(3, limits.GetProperty("keepLatestRuns").GetInt32());
        Assert.Equal(20, limits.GetProperty("maxPredictionBars").GetInt32());
    }

    // ---- sa_start_training ---------------------------------------------------------------------------------------------------------------

    private const string StartArguments = """
        {"symbols":["aaa","bbb.x"],"startDate":"2024-01-01","endDate":"2024-02-01","framework":"LightGBM","architecture":"gbdt",
         "windowSize":10,"horizon":2,"targetType":"Regression","nSplits":2,"hyperparameters":{"n-estimators":5,"learning-rate":"0.1"}}
        """;

    private static EphemeralTrainingRequest Request() => new()
    {
        Symbols = new[] { Aaa },
        Framework = TrainingFramework.LightGBM,
        Architecture = TrainingArchitectureCatalog.DefaultFor(TrainingFramework.LightGBM),
        WindowSize = 10,
        Horizon = 1,
    };

    [Fact]
    public async Task Start_PassesTheArgumentsAsOneRequestAndAnswersWithTheJobIdAndNoPath()
    {
        using JsonDocument response = await CallAsync(await StartAsync(), "sa_start_training", StartArguments);

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(FakeExecutionGateway.JobId, result.GetProperty("jobId").GetString());
        Assert.Equal("Running", result.GetProperty("state").GetString());
        Assert.Equal("os-temp", result.GetProperty("artifactsLocation").GetString());
        Assert.Equal(3, result.EnumerateObject().Count());

        EphemeralTrainingRequest request = Assert.Single(_gateway.Requests);
        Assert.Equal(new[] { "AAA", "BBB-X" }, request.Symbols); // the canonical forms
        Assert.Equal(new DateOnly(2024, 1, 1), request.StartDate);
        Assert.Equal(new DateOnly(2024, 2, 1), request.EndDate);
        Assert.Equal(TrainingFramework.LightGBM, request.Framework);
        Assert.Equal("gbdt", request.Architecture);
        Assert.Equal(10, request.WindowSize);
        Assert.Equal(2, request.Horizon);
        Assert.Equal(TargetType.Regression, request.TargetType);
        Assert.Equal(2, request.NSplits);
        Assert.Equal(new Dictionary<string, string> { ["n-estimators"] = "5", ["learning-rate"] = "0.1" }, request.Hyperparameters);
    }

    [Fact]
    public async Task Start_TheOptionalArgumentsKeepTheApplicationsDefaults()
    {
        using JsonDocument response = await CallAsync(
            await StartAsync(), "sa_start_training", """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""");

        Assert.False(McpTestSupport.IsError(response));
        EphemeralTrainingRequest request = Assert.Single(_gateway.Requests);
        Assert.Null(request.StartDate);
        Assert.Null(request.EndDate);
        Assert.Equal(TargetType.Classification, request.TargetType);
        Assert.Equal(WalkForwardDataRequirement.DefaultSplitCount, request.NSplits);
        Assert.Empty(request.Hyperparameters);
    }

    public static TheoryData<string, string, string?> StartMistakes => new()
    {
        { """{"symbols":[],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""", "INVALID_ARGUMENT", "symbols" },
        { """{"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""", "INVALID_ARGUMENT", "symbols" },
        { """{"symbols":["AAA"],"framework":"Keras","architecture":"gbdt","windowSize":10,"horizon":1}""", "INVALID_ARGUMENT", "framework" },
        { """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10}""", "INVALID_ARGUMENT", "horizon" },
        { """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":99999999999,"horizon":1}""", "INVALID_ARGUMENT", "windowSize" },
        { """{"symbols":["NOPE"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""", "SYMBOL_NOT_FOUND", "symbols[0]" },
        { """{"symbols":["AAA","a/b"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""", "INVALID_ARGUMENT", "symbols[1]" },
        { """{"symbols":["AAA"],"startDate":"2024-03-01","endDate":"2024-02-01","framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""", "INVALID_ARGUMENT", "startDate" },
        { """{"symbols":["AAA"],"startDate":"01/02/2024","framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""", "INVALID_ARGUMENT", "startDate" },
        { """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1,"hyperparameters":{"n-estimators":true}}""", "INVALID_ARGUMENT", "hyperparameters.n-estimators" },
        { """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1,"hyperparameters":{"n-estimators":"1","n-estimators":"2"}}""", "INVALID_ARGUMENT", "hyperparameters.n-estimators" },
        { """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1,"outputName":"x"}""", "INVALID_ARGUMENT", "outputName" },
    };

    [Theory]
    [MemberData(nameof(StartMistakes))]
    public async Task Start_AMistakeIsReportedWithItsCodeAndTheArgumentAndNoJobIsStarted(string arguments, string code, string? parameter)
    {
        using JsonDocument response = await CallAsync(await StartAsync(), "sa_start_training", arguments);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task Start_MoreSymbolsThanTheLimitIsLimitExceeded()
    {
        McpMessageDispatcher dispatcher = await StartAsync(true, ("AiIntegration:MaxSymbolsPerMetadataCall", "2"));

        using JsonDocument response = await CallAsync(dispatcher, "sa_start_training", """{"symbols":["AAA","BBB-X","AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""");

        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("symbols", McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.Empty(_gateway.Requests);
    }

    public static TheoryData<string, string?> GatewayRefusals => new()
    {
        { "invalid-with-parameter", "windowSize" },
        { "invalid-without-parameter", "request" },
        { "invalid-pascal-case", "hyperparameters" },
        { "busy", null },
        { "python", null },
        { "path-too-long", null },
    };

    [Theory]
    [MemberData(nameof(GatewayRefusals))]
    public async Task Start_TheGatewaysRefusalsBecomeTheDocumentedCodes(string refusal, string? parameter)
    {
        _gateway.Start = _ => throw refusal switch
        {
            "invalid-with-parameter" => new ExecutionRequestInvalidException("WindowSize is too small.", "windowSize", new ArgumentException("x")),
            "invalid-without-parameter" => new ExecutionRequestInvalidException("The configuration is inconsistent.", null, new InvalidOperationException("x")),
            "invalid-pascal-case" => new ExecutionRequestInvalidException("Hyperparameters are wrong.", "Hyperparameters", new ArgumentException("x")),
            "busy" => new ExecutionBusyException(),
            "path-too-long" => new ExecutionPathTooLongException(263),
            _ => new PythonEnvironmentMissingException(),
        };

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_start_training", """{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1}""");

        JsonElement error = McpTestSupport.Structured(response);
        Assert.True(McpTestSupport.IsError(response));
        string expectedCode = refusal switch { "busy" => "EXECUTION_BUSY", "python" => "PYTHON_ENV_MISSING", "path-too-long" => "SANDBOX_PATH_TOO_LONG", _ => "INVALID_ARGUMENT" };
        Assert.Equal(expectedCode, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, error.TryGetProperty("param", out JsonElement param) ? param.GetString() : null);
        if (refusal == "python")
        {
            // AT-4.2b: the hint is the fixed sentence and no path.
            Assert.Equal("Start StockAnalyzer once to install the embedded Python.", error.GetProperty("hint").GetString());
            Assert.DoesNotContain(":\\", error.ToString());
        }

        if (refusal == "path-too-long")
        {
            Assert.Equal(ExecutionToolSupport.PathTooLongHint, error.GetProperty("hint").GetString());
            Assert.Contains("263", error.GetProperty("message").GetString());
            Assert.Contains("260", error.GetProperty("message").GetString());
            Assert.DoesNotContain(":\\", error.ToString());
        }
    }

    // ---- sa_start_rl_training (MP-3) -----------------------------------------------------------------------------------------------------

    private static readonly string[] RlToolNames = { "sa_start_rl_training" };

    [Fact]
    public async Task RlStart_PassesTheCanonicalSymbolAndThePeriodAndAnswersWithTheJobIdAndNoPath()
    {
        using JsonDocument response = await CallAsync(await StartAsync(), "sa_start_rl_training", """{"symbol":"aaa","startDate":"2024-01-02","endDateExclusive":"2024-02-01"}""");

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(FakeExecutionGateway.JobId, result.GetProperty("jobId").GetString());
        Assert.Equal("Running", result.GetProperty("state").GetString());
        Assert.Equal("os-temp", result.GetProperty("artifactsLocation").GetString());
        Assert.Equal(3, result.EnumerateObject().Count());
        EphemeralRlRequest request = Assert.Single(_gateway.RlRequests);
        Assert.Equal("AAA", request.Symbol);
        Assert.Equal(new DateOnly(2024, 1, 2), request.StartDate);
        Assert.Equal(new DateOnly(2024, 2, 1), request.EndDateExclusive);
        Assert.Empty(_gateway.Requests);
    }

    [Fact]
    public async Task RlStart_OmittedDatesStayOmittedSoTheGatewayProposesThemAsTheWindowDoes()
    {
        using JsonDocument response = await CallAsync(await StartAsync(), "sa_start_rl_training", """{"symbol":"AAA"}""");

        Assert.False(McpTestSupport.IsError(response));
        EphemeralRlRequest request = Assert.Single(_gateway.RlRequests);
        Assert.Null(request.StartDate);
        Assert.Null(request.EndDateExclusive);
    }

    public static TheoryData<string, string, string?> RlStartMistakes => new()
    {
        { """{}""", "INVALID_ARGUMENT", "symbol" },
        { """{"symbol":"NOPE"}""", "SYMBOL_NOT_FOUND", "symbol" },
        { """{"symbol":"a/b"}""", "INVALID_ARGUMENT", "symbol" },
        { """{"symbol":"AAA","startDate":"2024-03-01","endDateExclusive":"2024-02-01"}""", "INVALID_ARGUMENT", "startDate" },
        { """{"symbol":"AAA","startDate":"2024-02-01","endDateExclusive":"2024-02-01"}""", "INVALID_ARGUMENT", "startDate" },
        { """{"symbol":"AAA","endDateExclusive":"01/02/2024"}""", "INVALID_ARGUMENT", "endDateExclusive" },
        { """{"symbol":"AAA","smokeOnly":false}""", "INVALID_ARGUMENT", "smokeOnly" },
        { """{"symbol":"AAA","framework":"LightGBM"}""", "INVALID_ARGUMENT", "framework" },
    };

    [Theory]
    [MemberData(nameof(RlStartMistakes))]
    public async Task RlStart_AMistakeIsReportedWithItsCodeAndTheArgumentAndNoJobIsStarted(string arguments, string code, string? parameter)
    {
        using JsonDocument response = await CallAsync(await StartAsync(), "sa_start_rl_training", arguments);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.Empty(_gateway.RlRequests);
    }

    [Theory]
    [InlineData("invalid-with-parameter", "INVALID_ARGUMENT", "startDate")]
    [InlineData("invalid-without-parameter", "INVALID_ARGUMENT", "request")]
    [InlineData("busy", "EXECUTION_BUSY", null)]
    [InlineData("rl-env", "PYTHON_ENV_MISSING", null)]
    [InlineData("rl-module", "PYTHON_ENV_MISSING", null)]
    [InlineData("path-too-long", "SANDBOX_PATH_TOO_LONG", null)]
    [InlineData("insufficient", "INSUFFICIENT_MARKET_DATA", "symbol")]
    [InlineData("no-bars", "INSUFFICIENT_MARKET_DATA", "symbol")]
    public async Task RlStart_TheGatewaysRefusalsBecomeTheDocumentedCodes(string refusal, string expectedCode, string? parameter)
    {
        _gateway.StartRl = _ => throw refusal switch
        {
            "invalid-with-parameter" => new ExecutionRequestInvalidException("The start date must be before the end date.", "startDate", new ArgumentException("x")),
            "invalid-without-parameter" => new ExecutionRequestInvalidException("The source date interval must be nonempty.", null, new ArgumentException("x")),
            "busy" => new ExecutionBusyException(),
            "rl-env" => new RlEnvironmentMissingException(),
            "rl-module" => new RlEnvironmentMissingException("gymnasium"),
            "path-too-long" => new ExecutionPathTooLongException(263),
            "insufficient" => new InsufficientStoredDataException(1278, 900),
            _ => new InsufficientStoredDataException(1278, 0),
        };

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_start_rl_training", """{"symbol":"AAA"}""");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(expectedCode, McpTestSupport.ErrorCodeOf(response));
        JsonElement error = McpTestSupport.Structured(response);
        Assert.Equal(parameter, error.TryGetProperty("param", out JsonElement param) && param.ValueKind == JsonValueKind.String ? param.GetString() : null);
        if (refusal is "rl-env" or "rl-module")
        {
            Assert.Equal(ExecutionToolSupport.RlEnvironmentHint, error.GetProperty("hint").GetString());
            Assert.DoesNotContain("embedded", error.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", error.GetRawText());
            Assert.Equal(refusal == "rl-module", error.GetProperty("message").GetString()!.Contains("'gymnasium'", StringComparison.Ordinal));
        }

        if (refusal == "path-too-long")
        {
            Assert.Equal(ExecutionToolSupport.PathTooLongHint, error.GetProperty("hint").GetString());
            Assert.DoesNotContain(":\\", error.GetRawText());
        }

        if (refusal == "insufficient")
        {
            Assert.Contains("1278", error.GetProperty("message").GetString());
            Assert.Contains("900", error.GetProperty("message").GetString());
        }

        if (refusal == "no-bars")
        {
            Assert.Contains("no usable stored daily bars", error.GetProperty("message").GetString());
        }
    }

    private static TrainingJobStatus RlStatus(
        TrainingJobState state, EphemeralRlResult? result = null, TrainingFailureReason? reason = null, string? stage = "Training", int progress = 50, string[]? messages = null)
        => new(
            FakeExecutionGateway.JobId, state, stage, progress, TimeSpan.FromSeconds(8.76), null, messages ?? Array.Empty<string>(), null, reason, null,
            TrainingJobKind.ReinforcementLearning,
            new EphemeralRlRequest { Symbol = "AAA", StartDate = new DateOnly(2019, 1, 2), EndDateExclusive = new DateOnly(2024, 3, 29) },
            result);

    [Fact]
    public async Task RlStatus_ARunningJobEchoesItsPeriodAndHasNoMetricsOrArtifact()
    {
        _gateway.Status = _ => RlStatus(TrainingJobState.Running);

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("reinforcement_learning", result.GetProperty("kind").GetString());
        Assert.Equal("Running", result.GetProperty("state").GetString());
        Assert.Equal("Training", result.GetProperty("stage").GetString());
        Assert.Equal(50, result.GetProperty("progressPercent").GetInt32());
        Assert.Equal(8.8, result.GetProperty("elapsedSeconds").GetDouble());
        JsonElement request = result.GetProperty("request");
        Assert.Equal("AAA", request.GetProperty("symbol").GetString());
        Assert.Equal("2019-01-02", request.GetProperty("startDate").GetString());
        Assert.Equal("2024-03-29", request.GetProperty("endDateExclusive").GetString());
        Assert.True(request.GetProperty("smokeOnly").GetBoolean());
        Assert.False(request.TryGetProperty("symbols", out _));
        foreach (string absent in new[] { "metrics", "artifact", "result", "failureReason" })
        {
            Assert.False(result.TryGetProperty(absent, out _), absent);
        }
    }

    [Fact]
    public async Task RlStatus_ASucceededJobReportsTheExactDecimalTextAndTheModelHash()
    {
        var rl = new EphemeralRlResult("-0.012300000000000000001", 42, 512, 1024, 4321, new string('b', 64), new string('c', 64));
        _gateway.Status = _ => RlStatus(TrainingJobState.Succeeded, rl, stage: "Complete", progress: 100);

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        JsonElement result = McpTestSupport.Structured(response).GetProperty("result");
        Assert.Equal(JsonValueKind.String, result.GetProperty("validationMeanReturn").ValueKind); // the text, never a double
        Assert.Equal("-0.012300000000000000001", result.GetProperty("validationMeanReturn").GetString());
        Assert.Equal(42, result.GetProperty("candidateSeed").GetInt32());
        Assert.Equal(512, result.GetProperty("checkpointStep").GetInt32());
        Assert.Equal(1024, result.GetProperty("trainingTransitions").GetInt32());
        Assert.Equal(4321, result.GetProperty("model").GetProperty("sizeBytes").GetInt64());
        Assert.Equal(new string('b', 64), result.GetProperty("model").GetProperty("sha256").GetString());
        Assert.Equal(new string('c', 64), result.GetProperty("sourceRevision").GetString());
        Assert.False(McpTestSupport.Structured(response).TryGetProperty("artifact", out _));
    }

    [Fact]
    public async Task RlStatus_AFailedJobNamesItsReasonAndItsMessagesCarryNoPath()
    {
        string worker = Path.Combine(Path.GetTempPath(), "StockAnalyzer", "ephemeral", "session", FakeExecutionGateway.JobId, "TrainingArtifacts", "RL", "x", "train_rl.stderr.log");
        _gateway.Status = _ => RlStatus(TrainingJobState.Failed, reason: TrainingFailureReason.TrainingFailed, messages: new[] { $"RL worker exited 1; see {worker}. Worker: RuntimeError: Pinned dependency mismatch: stable-baselines3" });

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("TRAINING_FAILED", result.GetProperty("failureReason").GetString());
        string text = result.GetProperty("messages")[0].GetString()!;
        Assert.Contains("<path>", text);
        Assert.DoesNotContain("ephemeral", result.GetRawText());
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('\\', '/').Replace("\\", "\\\\"), result.GetRawText());
    }

    [Fact]
    public async Task Status_ASupervisedJobSaysItIsSupervisedAndHasNoRlParts()
    {
        _gateway.Status = _ => Status(TrainingJobState.Running);

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("supervised", result.GetProperty("kind").GetString());
        Assert.True(result.TryGetProperty("request", out JsonElement request) && request.TryGetProperty("symbols", out _));
        Assert.False(result.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Capabilities_DescribeTheRlSmokeRunStraightFromTheContractWhenExecutionIsOn()
    {
        using JsonDocument on = await CallAsync(await StartAsync(), "sa_get_capabilities");
        using JsonDocument off = await CallAsync(await StartAsync(enableExecution: false), "sa_get_capabilities");

        JsonElement rl = McpTestSupport.Structured(on).GetProperty("execution").GetProperty("reinforcementLearning");
        Assert.Equal("sa_start_rl_training", rl.GetProperty("tool").GetString());
        Assert.True(rl.GetProperty("smokeOnly").GetBoolean());
        Assert.Equal(StockAnalyzer.Core.Models.ReinforcementLearning.RlContract.SmokeTrainingTransitions, rl.GetProperty("trainingTransitions").GetInt32());
        Assert.Equal(StockAnalyzer.Core.Models.ReinforcementLearning.RlContract.JobDeadlineMinutes, rl.GetProperty("deadlineMinutes").GetInt32());
        Assert.False(McpTestSupport.Structured(off).GetProperty("execution").TryGetProperty("reinforcementLearning", out _));
    }

    [Fact]
    public async Task WithoutTheFlagTheRlToolIsNotListedAndIsAnUnknownTool()
    {
        McpMessageDispatcher dispatcher = await StartAsync(enableExecution: false);

        using JsonDocument list = await ListToolsAsync(dispatcher);
        using JsonDocument call = await CallAsync(dispatcher, "sa_start_rl_training", """{"symbol":"AAA"}""");

        Assert.Empty(ToolNames(list).Intersect(RlToolNames));
        Assert.Contains("Unknown tool", call.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(_gateway.RlRequests);
    }

    // ---- sa_get_training_status ------------------------------------------------------------------------------------------------------------

    private static TrainingJobStatus Status(
        TrainingJobState state,
        string? stage = "train",
        int progress = 0,
        IReadOnlyDictionary<string, double>? metrics = null,
        IReadOnlyList<string>? messages = null,
        EphemeralArtifact? artifact = null,
        TrainingFailureReason? reason = null,
        EphemeralTrainingRequest? request = null)
        => new(FakeExecutionGateway.JobId, state, stage, progress, TimeSpan.FromSeconds(12.34), metrics, messages ?? Array.Empty<string>(), artifact, reason, request ?? Request());

    private static string Arguments(string jobId = FakeExecutionGateway.JobId) => $$"""{"jobId":"{{jobId}}"}""";

    [Fact]
    public async Task Status_AJustStartedJobShowsZeroProgressAndEchoesTheStartArguments()
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        using (await CallAsync(dispatcher, "sa_start_training", StartArguments))
        {
        }

        _gateway.Status = _ => Status(TrainingJobState.Running, stage: null, request: _gateway.Requests[0]);

        using JsonDocument response = await CallAsync(dispatcher, "sa_get_training_status", Arguments());

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("Running", result.GetProperty("state").GetString());
        Assert.Equal(0, result.GetProperty("progressPercent").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("stage").ValueKind);
        Assert.Equal(12.3, result.GetProperty("elapsedSeconds").GetDouble());
        Assert.False(result.TryGetProperty("artifact", out _));
        Assert.False(result.TryGetProperty("failureReason", out _));
        Assert.False(result.TryGetProperty("metrics", out _));

        // The echo is the start call's arguments (the canonical symbols), so it can start the same job again.
        using JsonDocument expected = JsonDocument.Parse(StartArguments);
        JsonElement echo = result.GetProperty("request");
        Assert.Equal(new[] { "AAA", "BBB-X" }, echo.GetProperty("symbols").EnumerateArray().Select(s => s.GetString()).ToArray());
        foreach (string name in new[] { "startDate", "endDate", "framework", "architecture", "targetType" })
        {
            Assert.Equal(expected.RootElement.GetProperty(name).GetString(), echo.GetProperty(name).GetString());
        }

        foreach (string name in new[] { "windowSize", "horizon", "nSplits" })
        {
            Assert.Equal(expected.RootElement.GetProperty(name).GetInt32(), echo.GetProperty(name).GetInt32());
        }

        Assert.Equal("5", echo.GetProperty("hyperparameters").GetProperty("n-estimators").GetString());
        Assert.Equal("0.1", echo.GetProperty("hyperparameters").GetProperty("learning-rate").GetString());
    }

    [Fact]
    public async Task Status_NonFiniteMetricsKeepTheirKeyWithANullValue()
    {
        _gateway.Status = _ => Status(
            TrainingJobState.Running,
            progress: 40,
            metrics: new Dictionary<string, double> { ["accuracy"] = 0.75, ["loss"] = double.NaN, ["gain"] = double.PositiveInfinity, ["drop"] = double.NegativeInfinity });

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        JsonElement metrics = McpTestSupport.Structured(response).GetProperty("metrics");
        Assert.Equal(0.75, metrics.GetProperty("accuracy").GetDouble());
        Assert.Equal(new[] { "accuracy", "drop", "gain", "loss" }, metrics.EnumerateObject().Select(p => p.Name).ToArray());
        foreach (string key in new[] { "loss", "gain", "drop" })
        {
            Assert.Equal(JsonValueKind.Null, metrics.GetProperty(key).ValueKind);
        }

        Assert.Equal(40, McpTestSupport.Structured(response).GetProperty("progressPercent").GetInt32());
    }

    [Fact]
    public async Task Status_TheStageAndTheMessagesAreCutAndNeverCarryAPath()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "StockAnalyzer", "ephemeral", "session", FakeExecutionGateway.JobId, "model.onnx");
        string[] messages =
        {
            @"Saving C:\Users\someone\AppData\Local\Temp\x.onnx now",
            "Reading /tmp/work/data.parquet and /var/lib/x and /home/me/y and /Users/me/z",
            @"Share \\server\share\file.bin",
            $"Wrote {tempPath}",
            new string('m', 500),
        };
        _gateway.Status = _ => Status(TrainingJobState.Running, stage: new string('s', 300), messages: messages);

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        JsonElement result = McpTestSupport.Structured(response);
        Assert.True(result.GetProperty("stage").GetString()!.Length <= ExecutionLimits.MaxStageChars);
        string[] shown = result.GetProperty("messages").EnumerateArray().Select(m => m.GetString()!).ToArray();
        Assert.Equal(messages.Length, shown.Length);
        Assert.All(shown, text => Assert.True(text.Length <= ExecutionLimits.MaxStatusMessageChars));
        string whole = McpTestSupport.Structured(response).GetRawText();
        foreach (string forbidden in new[] { "C:\\\\", "\\\\\\\\", "/tmp/", "/var/", "/home/", "/Users/", "someone", Path.GetTempPath().TrimEnd('\\', '/').Replace("\\", "\\\\") })
        {
            Assert.DoesNotContain(forbidden, whole);
        }

        Assert.Contains("<path>", shown[0]);
        Assert.Equal("Wrote <path>", shown[3]); // the rest of a path under a known root is dropped too
        Assert.DoesNotContain("ephemeral", whole);
    }

    [Fact]
    public async Task Status_ASucceededJobDescribesItsModel()
    {
        var artifact = new EphemeralArtifact(FakeExecutionGateway.JobId, "model.onnx", 1234, new string('a', 64), "model.onnx.metrics.json", true, new long[] { -1, 10, 5 }, new long[] { -1, 3 });
        _gateway.Status = _ => Status(TrainingJobState.Succeeded, stage: "done", progress: 100, artifact: artifact);

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        JsonElement model = McpTestSupport.Structured(response).GetProperty("artifact");
        Assert.Equal(FakeExecutionGateway.JobId, model.GetProperty("artifactId").GetString());
        Assert.Equal("model.onnx", model.GetProperty("onnxFileName").GetString());
        Assert.Equal(1234, model.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(new string('a', 64), model.GetProperty("sha256").GetString());
        JsonElement validation = model.GetProperty("validation");
        Assert.True(validation.GetProperty("loads").GetBoolean());
        Assert.Equal(new[] { -1L, 10, 5 }, validation.GetProperty("inputShape").EnumerateArray().Select(d => d.GetInt64()).ToArray());
        Assert.Equal(new[] { -1L, 3 }, validation.GetProperty("outputShape").EnumerateArray().Select(d => d.GetInt64()).ToArray());
        Assert.False(McpTestSupport.Structured(response).TryGetProperty("failureReason", out _));
    }

    [Theory]
    [InlineData(TrainingFailureReason.TrainingFailed, "TRAINING_FAILED")]
    [InlineData(TrainingFailureReason.Timeout, "TIMEOUT")]
    [InlineData(TrainingFailureReason.InvalidOnnx, "INVALID_ONNX")]
    public async Task Status_AFailedJobNamesItsReason(TrainingFailureReason reason, string wireName)
    {
        _gateway.Status = _ => Status(TrainingJobState.Failed, reason: reason);

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_get_training_status", Arguments());

        Assert.Equal(wireName, McpTestSupport.Structured(response).GetProperty("failureReason").GetString());
    }

    [Fact]
    public void EveryFailureReasonHasAWireName()
    {
        foreach (TrainingFailureReason reason in Enum.GetValues<TrainingFailureReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(GetTrainingStatusTool.FailureReasonName(reason)));
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("cancel")]
    public async Task StatusAndCancel_AnUnknownJobIsJobNotFoundAndAMalformedIdIsAnInvalidArgument(string tool)
    {
        McpMessageDispatcher dispatcher = await StartAsync();
        string name = tool == "status" ? "sa_get_training_status" : "sa_cancel_training";

        using JsonDocument unknown = await CallAsync(dispatcher, name, Arguments());
        using JsonDocument malformed = await CallAsync(dispatcher, name, Arguments("not-a-job"));
        using JsonDocument missing = await CallAsync(dispatcher, name);

        Assert.Equal("JOB_NOT_FOUND", McpTestSupport.ErrorCodeOf(unknown));
        Assert.Equal("jobId", McpTestSupport.Structured(unknown).GetProperty("param").GetString());
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(malformed));
        Assert.Equal("jobId", McpTestSupport.Structured(malformed).GetProperty("param").GetString());
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(missing));
    }

    // ---- sa_cancel_training ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TrainingJobState.Cancelling)]
    [InlineData(TrainingJobState.Cancelled)]
    [InlineData(TrainingJobState.Succeeded)]
    public async Task Cancel_ReturnsTheStateTheGatewayReports(TrainingJobState state)
    {
        string? asked = null;
        _gateway.CancelJob = jobId =>
        {
            asked = jobId;
            return state;
        };

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_cancel_training", Arguments());

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(FakeExecutionGateway.JobId, asked);
        Assert.Equal(FakeExecutionGateway.JobId, result.GetProperty("jobId").GetString());
        Assert.Equal(state.ToString(), result.GetProperty("state").GetString());
    }

    // ---- sa_run_onnx_prediction (WP-4) ----------------------------------------------------------------------------------------------------------

    private static string GoodModel => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets", "trend_predictor_goodmeta.onnx"));

    private static string RegressionModel => Path.Combine(Path.GetDirectoryName(GoodModel)!, "trend_predictor_regression_v2.onnx");

    private ArtifactLease LeaseOf(string path)
    {
        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var artifact = new EphemeralArtifact(FakeExecutionGateway.JobId, "model.onnx", new FileInfo(path).Length, hash, "model.onnx.metrics.json", true, new long[] { -1, 10, 5 }, new long[] { -1, 3 });
        return new ArtifactLease(artifact, path, () => Interlocked.Increment(ref _gateway.Released));
    }

    private async Task<McpMessageDispatcher> StartWithModelAsync(string? modelPath = null, params (string Key, string Value)[] overrides)
    {
        string path = modelPath ?? GoodModel;
        _gateway.Lease = _ => LeaseOf(path);
        return await StartAsync(true, overrides);
    }

    private static string Prediction(string artifactId = "latest", string symbol = Aaa, string? asOf = null, int? count = null)
        => $$"""{"artifactId":"{{artifactId}}","symbol":"{{symbol}}"{{(asOf is null ? string.Empty : $",\"asOfDate\":\"{asOf}\"")}}{{(count is null ? string.Empty : $",\"count\":{count}")}}}""";

    private static string DateOfBar(int index) => FirstBar.AddDays(index).ToString("yyyy-MM-dd");

    private static string[] Scores(JsonElement prediction)
        => prediction.GetProperty("classScores").EnumerateArray().Select(s => $"{s.GetProperty("class").GetString()}:{s.GetProperty("probability").GetRawText()}").ToArray();

    [Fact]
    public async Task Prediction_TheLatestBarGetsClassScoresAPredictedClassAndTheModelContract()
    {
        using JsonDocument response = await CallAsync(await StartWithModelAsync(), "sa_run_onnx_prediction", Prediction());

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(FakeExecutionGateway.JobId, result.GetProperty("artifactId").GetString());
        Assert.Equal(new[] { "latest" }, _gateway.LeaseRequests);
        Assert.Equal(Aaa, result.GetProperty("symbol").GetString());
        Assert.Equal("D1", result.GetProperty("timeFrame").GetString());
        Assert.Equal(DateOfBar(Bars - 1), result.GetProperty("asOfDate").GetString());

        JsonElement contract = result.GetProperty("modelContract");
        Assert.Equal(10, contract.GetProperty("windowSize").GetInt32());
        Assert.Equal("OhlcvMinMax", contract.GetProperty("featureMode").GetString());
        Assert.Equal(3, contract.GetProperty("classCount").GetInt32());
        Assert.Equal("Classification", contract.GetProperty("targetType").GetString());
        Assert.Equal(64, contract.GetProperty("sha256").GetString()!.Length);

        JsonElement prediction = Assert.Single(result.GetProperty("predictions").EnumerateArray());
        Assert.Equal(DateOfBar(Bars - 1), prediction.GetProperty("date").GetString());
        double[] probabilities = prediction.GetProperty("classScores").EnumerateArray().Select(s => s.GetProperty("probability").GetDouble()).ToArray();
        Assert.Equal(3, probabilities.Length);
        Assert.InRange(probabilities.Sum(), 0.9999, 1.0001);
        string[] classes = prediction.GetProperty("classScores").EnumerateArray().Select(s => s.GetProperty("class").GetString()!).ToArray();
        Assert.Equal(classes[Array.IndexOf(probabilities, probabilities.Max())], prediction.GetProperty("predictedClass").GetString());
        Assert.Equal(probabilities.Max(), prediction.GetProperty("confidence").GetDouble(), 5);
        Assert.Equal(1, _gateway.Released); // the lease is released after the call
    }

    [Fact]
    public async Task Prediction_TheLastOfSeveralBarsIsTheSingleResultAndALaterBarNeverChangesAnEarlierOne()
    {
        McpMessageDispatcher dispatcher = await StartWithModelAsync();

        using JsonDocument several = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 5));
        using JsonDocument single = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 1));

        JsonElement[] list = McpTestSupport.Structured(several).GetProperty("predictions").EnumerateArray().ToArray();
        Assert.Equal(5, list.Length);
        Assert.Equal(Enumerable.Range(Bars - 5, 5).Select(DateOfBar), list.Select(p => p.GetProperty("date").GetString()));
        JsonElement only = McpTestSupport.Structured(single).GetProperty("predictions")[0];
        Assert.Equal(Scores(only), Scores(list[^1]));
        Assert.Equal(only.GetProperty("date").GetString(), list[^1].GetProperty("date").GetString());

        // AT-5.2: asking as of an earlier bar (later bars are stored) gives that bar's prediction from the longer run.
        for (int offset = 0; offset < list.Length; offset++)
        {
            string date = list[offset].GetProperty("date").GetString()!;
            using JsonDocument asOf = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(asOf: date));
            JsonElement result = McpTestSupport.Structured(asOf);
            Assert.Equal(date, result.GetProperty("asOfDate").GetString());
            Assert.Equal(Scores(list[offset]), Scores(result.GetProperty("predictions")[0]));
        }
    }

    [Fact]
    public async Task Prediction_ARegressionModelReturnsItsValue()
    {
        using JsonDocument response = await CallAsync(await StartWithModelAsync(RegressionModel), "sa_run_onnx_prediction", Prediction(count: 2));

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal("Regression", result.GetProperty("modelContract").GetProperty("targetType").GetString());
        JsonElement[] list = result.GetProperty("predictions").EnumerateArray().ToArray();
        Assert.Equal(2, list.Length);
        Assert.All(list, prediction =>
        {
            Assert.Equal(0.04, prediction.GetProperty("value").GetDouble(), 5); // the fixture's constant output
            Assert.False(prediction.TryGetProperty("classScores", out _));
            Assert.False(prediction.TryGetProperty("predictedClass", out _));
        });
    }

    public static TheoryData<string, string, string?> PredictionMistakes => new()
    {
        { """{"artifactId":"latest"}""", "INVALID_ARGUMENT", "symbol" },
        { """{"symbol":"AAA"}""", "INVALID_ARGUMENT", "artifactId" },
        { """{"artifactId":"nope","symbol":"AAA"}""", "INVALID_ARGUMENT", "artifactId" },
        { """{"artifactId":"latest","symbol":"NOPE"}""", "SYMBOL_NOT_FOUND", "symbol" },
        { """{"artifactId":"latest","symbol":"AAA","asOfDate":"2024/02/01"}""", "INVALID_ARGUMENT", "asOfDate" },
        { """{"artifactId":"latest","symbol":"AAA","count":0}""", "INVALID_ARGUMENT", "count" },
        { """{"artifactId":"latest","symbol":"AAA","count":65}""", "LIMIT_EXCEEDED", "count" },
        { """{"artifactId":"latest","symbol":"AAA","count":1.5}""", "INVALID_ARGUMENT", "count" },
    };

    [Theory]
    [MemberData(nameof(PredictionMistakes))]
    public async Task Prediction_AMistakeIsReportedBeforeAnyModelIsLeased(string arguments, string code, string? parameter)
    {
        using JsonDocument response = await CallAsync(await StartWithModelAsync(), "sa_run_onnx_prediction", arguments);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(code, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
        Assert.Empty(_gateway.LeaseRequests);
    }

    [Fact]
    public async Task Prediction_AnUnknownOrExpiredArtifactIsArtifactNotFound()
    {
        _gateway.Lease = id => throw new ArtifactNotFoundException(id);

        using JsonDocument response = await CallAsync(await StartAsync(), "sa_run_onnx_prediction", Prediction(FakeExecutionGateway.JobId));

        Assert.Equal("ARTIFACT_NOT_FOUND", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal("artifactId", McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task Prediction_TooFewStoredBarsIsInsufficientMarketDataAndTheLeaseIsStillReleased()
    {
        McpMessageDispatcher dispatcher = await StartWithModelAsync();

        using JsonDocument shortSymbol = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(symbol: Bbb)); // 8 bars, 10 needed
        using JsonDocument earlyDate = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(asOf: DateOfBar(5)));
        using JsonDocument tooMany = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 60)); // 10 + 59 bars needed, 60 stored

        Assert.Equal("INSUFFICIENT_MARKET_DATA", McpTestSupport.ErrorCodeOf(shortSymbol));
        Assert.Equal("INSUFFICIENT_MARKET_DATA", McpTestSupport.ErrorCodeOf(earlyDate));
        Assert.Equal("INSUFFICIENT_MARKET_DATA", McpTestSupport.ErrorCodeOf(tooMany));
        Assert.Equal(3, _gateway.Released);
    }

    [Fact]
    public async Task Prediction_AModelOutsideTheContractIsAnUnexpectedErrorAndTheLeaseIsReleased()
    {
        string jointFeatureModel = Path.Combine(Path.GetDirectoryName(GoodModel)!, "trend_predictor_jointmeta.onnx");

        using JsonDocument response = await CallAsync(await StartWithModelAsync(jointFeatureModel), "sa_run_onnx_prediction", Prediction());

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("INTERNAL_ERROR", McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(1, _gateway.Released);
    }

    [Fact]
    public async Task Prediction_ACountThatNeedsMoreBarsThanTheRequestLimitSaysTheLargestCountOfThisModel()
    {
        McpMessageDispatcher dispatcher = await StartWithModelAsync(null, SmallRequestLimit.Where(o => !o.Key.EndsWith("MaxPredictionBars")).Append(("AiIntegration:Execution:MaxPredictionBars", "20")).ToArray());

        using JsonDocument over = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 12)); // window 10 + 11 = 21 bars, limit 20
        using JsonDocument atTheLimit = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 11));

        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(over));
        JsonElement error = McpTestSupport.Structured(over);
        Assert.Equal("count", error.GetProperty("param").GetString());
        Assert.Equal("For this model (window 10) use a count of at most 11.", error.GetProperty("hint").GetString());
        Assert.False(McpTestSupport.IsError(atTheLimit), McpTestSupport.Structured(atTheLimit).ToString());
        Assert.Equal(11, McpTestSupport.Structured(atTheLimit).GetProperty("predictions").GetArrayLength());
    }

    [Fact]
    public async Task Prediction_TheCountLimitFollowsTheConfiguration()
    {
        McpMessageDispatcher dispatcher = await StartWithModelAsync(null, ("AiIntegration:Execution:MaxPredictionBars", "3"));

        using JsonDocument over = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 4));
        using JsonDocument within = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 3));

        Assert.Equal("LIMIT_EXCEEDED", McpTestSupport.ErrorCodeOf(over));
        Assert.Equal(3, McpTestSupport.Structured(within).GetProperty("predictions").GetArrayLength());
        using JsonDocument list = await ListToolsAsync(dispatcher);
        JsonElement count = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "sa_run_onnx_prediction")
            .GetProperty("inputSchema").GetProperty("properties").GetProperty("count");
        Assert.Equal(3, count.GetProperty("maximum").GetInt32());
    }

    // A request limit far below the stored history: 60 bars are stored, at most 20 may be read for one request.
    private static readonly (string Key, string Value)[] SmallRequestLimit =
    {
        ("AiIntegration:MaxCandlesPerRequest", "20"),
        ("AiIntegration:DefaultCandleCount", "10"),
        ("AiIntegration:MaxOutputTailBars", "10"),
        ("AiIntegration:DefaultOutputTailBars", "10"),
        ("AiIntegration:Execution:MaxPredictionBars", "5"),
    };

    [Fact]
    public async Task Prediction_AnAsOfDateOlderThanTheNewestLimitBarsStillPredictsFromTheStoredHistory()
    {
        string date = DateOfBar(29); // 30 bars are stored up to it; the newest 20 bars all lie after it
        McpMessageDispatcher limited = await StartWithModelAsync(null, SmallRequestLimit);
        McpMessageDispatcher unlimited = await StartWithModelAsync();

        using JsonDocument response = await CallAsync(limited, "sa_run_onnx_prediction", Prediction(asOf: date, count: 3));
        using JsonDocument reference = await CallAsync(unlimited, "sa_run_onnx_prediction", Prediction(asOf: date, count: 3));

        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        JsonElement[] list = McpTestSupport.Structured(response).GetProperty("predictions").EnumerateArray().ToArray();
        Assert.Equal(date, McpTestSupport.Structured(response).GetProperty("asOfDate").GetString());
        Assert.Equal(Enumerable.Range(27, 3).Select(DateOfBar), list.Select(p => p.GetProperty("date").GetString()));
        JsonElement[] expected = McpTestSupport.Structured(reference).GetProperty("predictions").EnumerateArray().ToArray();
        Assert.Equal(expected.Select(Scores), list.Select(Scores));
    }

    [Fact]
    public async Task Prediction_WithTheSmallLimitNoDateStillGivesTheLatestBar()
    {
        using JsonDocument response = await CallAsync(await StartWithModelAsync(null, SmallRequestLimit), "sa_run_onnx_prediction", Prediction());

        Assert.Equal(DateOfBar(Bars - 1), McpTestSupport.Structured(response).GetProperty("asOfDate").GetString());
    }

    [Fact]
    public async Task Prediction_FewerStoredBarsUpToTheDateThanNeededIsInsufficientMarketDataWhateverTheRequestLimit()
    {
        McpMessageDispatcher dispatcher = await StartWithModelAsync(null, SmallRequestLimit);

        using JsonDocument response = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(asOf: DateOfBar(5))); // 6 bars up to it, 10 needed

        Assert.Equal("INSUFFICIENT_MARKET_DATA", McpTestSupport.ErrorCodeOf(response));
        Assert.Contains("6 are stored up to " + DateOfBar(5), McpTestSupport.Structured(response).GetProperty("message").GetString());
        Assert.Equal(1, _gateway.Released);
    }

    [Fact]
    public async Task Prediction_AnAsOfDateAfterTheLastBarGivesTheLatestBars()
    {
        McpMessageDispatcher dispatcher = await StartWithModelAsync();

        using JsonDocument future = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(asOf: DateOfBar(Bars + 30), count: 2));
        using JsonDocument latest = await CallAsync(dispatcher, "sa_run_onnx_prediction", Prediction(count: 2));

        JsonElement result = McpTestSupport.Structured(future);
        Assert.Equal(DateOfBar(Bars - 1), result.GetProperty("asOfDate").GetString());
        Assert.Equal(
            McpTestSupport.Structured(latest).GetProperty("predictions").EnumerateArray().Select(Scores),
            result.GetProperty("predictions").EnumerateArray().Select(Scores));
    }

    // ---- the log bridge (E-5) -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLogBridgeWritesInformationAndAboveAsOneLineAndNeverAnExceptionsText()
    {
        var writer = new StringWriter();
        ILogger<SandboxedExecutionGateway> logger = new LogSinkLogger<SandboxedExecutionGateway>(new LogSink(writer));

        logger.LogInformation("Training job {JobId} started.", FakeExecutionGateway.JobId);
        logger.LogDebug("below the threshold");
        logger.LogWarning(new IOException(@"cannot delete C:\Users\someone\secret"), "A folder could not be deleted.");
        logger.LogError("Something failed.");

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith($"INFO Training job {FakeExecutionGateway.JobId} started.", lines[0]);
        Assert.EndsWith("INFO Warning: A folder could not be deleted. (IOException)", lines[1]);
        Assert.EndsWith("ERROR Something failed.", lines[2]);
        Assert.DoesNotContain("someone", writer.ToString());
    }
}
