using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.CommandLine;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class McpSecurityHardeningMp5Tests : IDisposable
{
    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "sa_mp5_mcp_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_emptyRoot))
            {
                Directory.Delete(_emptyRoot, recursive: true);
            }
        }
        catch { /* best effort */ }
    }

    [Fact]
    public void McpHelpText_Overview_DisclosesNonOsSandboxedExecution()
    {
        using McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot),
            new LogSink(TextWriter.Null),
            enableExecution: true);

        string help = McpHelpText.Overview(runtime.Tools, includeExecutionTools: true);
        Assert.Contains("without OS/network sandboxing", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_ExecutionEnabled_ExposesNonSandboxIsolationMetadata()
    {
        using McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot),
            new LogSink(TextWriter.Null),
            enableExecution: true);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument resp = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_get_capabilities"))!;
        Assert.False(McpTestSupport.IsError(resp));
        JsonElement isolation = McpTestSupport.Structured(resp).GetProperty("execution").GetProperty("isolation");
        Assert.Equal("os-temp-folder", isolation.GetProperty("kind").GetString());
        Assert.False(isolation.GetProperty("osSandboxed").GetBoolean());
        Assert.False(isolation.GetProperty("networkIsolated").GetBoolean());
        Assert.False(isolation.GetProperty("mutatesDataTree").GetBoolean());
        Assert.Contains("without OS or network sandboxing", isolation.GetProperty("disclosure").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1,"hyperparameters":{"opset":17}}""", "hyperparameters.opset", "n-estimators", "Unknown hyperparameter")]
    [InlineData("""{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1,"hyperparameters":{"epochs":5}}""", "hyperparameters.epochs", "n-estimators", "not supported")]
    [InlineData("""{"symbols":["AAA"],"framework":"PyTorch","architecture":"lstm","windowSize":10,"horizon":1,"hyperparameters":{"heads":4}}""", "hyperparameters.heads", "hidden", "not supported")]
    [InlineData("""{"symbols":["AAA"],"framework":"LightGBM","architecture":"gbdt","windowSize":10,"horizon":1,"hyperparameters":{"n-estimators":0}}""", "hyperparameters.n-estimators", "n-estimators", ">=")]
    [InlineData("""{"symbols":["AAA"],"framework":"PyTorch","architecture":"attn_lstm","windowSize":10,"horizon":1,"hyperparameters":{"hidden":10,"heads":4}}""", "hyperparameters.heads", "heads", "divisible by heads")]
    [InlineData("""{"symbols":["AAA"],"framework":"PyTorch","architecture":"lstm","windowSize":10,"horizon":1,"hyperparameters":{"epochs":"5","--epochs":"6"}}""", "hyperparameters.epochs", "epochs", "Duplicate hyperparameter")]
    public async Task StartTraining_InvalidOrDisallowedHyperparameter_ReturnsInvalidArgumentWithAllowedHint(
        string argumentsJson,
        string expectedParam,
        string expectedAllowedKeyInHint,
        string expectedMessageFragment)
    {
        var gateway = new SpyExecutionGateway();
        using McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot),
            new LogSink(TextWriter.Null),
            enableExecution: true,
            executionGateway: gateway,
            gatewayWrapper: _ => new SingleSymbolGateway("AAA"));
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument resp = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_start_training", argumentsJson))!;
        Assert.True(McpTestSupport.IsError(resp));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(resp));
        JsonElement err = McpTestSupport.Structured(resp);
        Assert.Equal(expectedParam, err.GetProperty("param").GetString());
        Assert.Contains(expectedMessageFragment, err.GetProperty("message").GetString(), StringComparison.Ordinal);
        string hint = err.GetProperty("hint").GetString()!;
        Assert.Contains("Allowed hyperparameters", hint, StringComparison.Ordinal);
        Assert.Contains(expectedAllowedKeyInHint, hint, StringComparison.Ordinal);
        Assert.False(gateway.Started);
    }

    [Fact]
    public async Task StartTraining_UnknownArchitectureWithBadHyperparameter_RejectsArchitectureFirst()
    {
        var gateway = new SpyExecutionGateway();
        using McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot),
            new LogSink(TextWriter.Null),
            enableExecution: true,
            executionGateway: gateway,
            gatewayWrapper: _ => new SingleSymbolGateway("AAA"));
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        using JsonDocument resp = (await McpTestSupport.CallAsync(
            runtime.Dispatcher,
            "sa_start_training",
            """{"symbols":["AAA"],"framework":"PyTorch","architecture":"Lstm","windowSize":10,"horizon":1,"hyperparameters":{"heads":4}}"""))!;
        Assert.True(McpTestSupport.IsError(resp));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(resp));
        JsonElement err = McpTestSupport.Structured(resp);
        Assert.Equal("architecture", err.GetProperty("param").GetString());
        Assert.Contains("Allowed architectures for PyTorch", err.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.False(gateway.Started);
    }

    [Theory]
    [InlineData(@"Data/Daily/AAPL.parquet", "<path>")]
    [InlineData(@"Missing file at Data\Weekly\MSFT.parquet here", "Missing file at <path> here")]
    [InlineData(@"Code DATA_UNAVAILABLE for P/E and 7203.T/9984.T", @"Code DATA_UNAVAILABLE for P/E and 7203.T/9984.T")]
    public void ErrorMessageSanitizer_RedactsRelativeDataPathsWithoutBreakingMetricsOrTickers(string input, string expected)
    {
        var sanitizer = new ErrorMessageSanitizer(Array.Empty<string?>());
        Assert.Equal(expected, sanitizer.Sanitize(input));
    }

    [Fact]
    public void Tools_RequireNonNullErrorMessageSanitizer()
    {
        var symbols = new SymbolResolver(new EmptyReadOnlyStockAnalyzerGateway());
        Assert.Throws<ArgumentNullException>(() => new CheckPortfolioRlPreflightTool(symbols, sanitizer: null!));
        Assert.Throws<ArgumentNullException>(() => new CheckRlPlaybackPreflightTool(symbols, sanitizer: null!));
        Assert.Throws<ArgumentNullException>(() => new GetModelAttentionTool(new EmptyReadOnlyStockAnalyzerGateway(), symbols, sanitizer: null!));
    }

    [Fact]
    public void McpServerSettings_BuildLaunchArgumentList_MatchesBuildLaunchArguments()
    {
        var settings = new McpServerSettings
        {
            EnableDiscussion = true,
            Toolset = "core,screener",
            UseTextOnlyResponse = true,
            CompactDefaults = true,
            ProgressiveDiscovery = true,
        };

        var tokens = settings.BuildLaunchArgumentList();
        Assert.Equal(
            new[] { "--enable-discussion", "--toolset", "core,screener", "--response-format", "text-only", "--compact-defaults", "--progressive-discovery" },
            tokens);
        Assert.Equal(string.Join(" ", tokens), settings.BuildLaunchArguments());
    }

    private sealed class SpyExecutionGateway : ISandboxedExecutionGateway
    {
        public bool Started { get; private set; }
        public Task<TrainingJobTicket> StartTrainingAsync(EphemeralTrainingRequest request, CancellationToken cancellationToken)
        {
            Started = true;
            return Task.FromResult(new TrainingJobTicket("job", TrainingJobState.Running));
        }
        public TrainingJobStatus GetStatus(string jobId) => throw new JobNotFoundException(jobId);
        public IReadOnlyList<TrainingJobSummary> ListJobs() => Array.Empty<TrainingJobSummary>();
        public TrainingJobState Cancel(string jobId) => throw new JobNotFoundException(jobId);
        public EphemeralArtifact? FindArtifact(string artifactIdOrLatest) => null;
        public ArtifactLease LeaseArtifact(string artifactId) => throw new ArtifactNotFoundException(artifactId);
        public ArtifactLease LeaseRegisteredModel(string modelId) => throw new ArtifactNotFoundException(modelId);
        public Task<TrainingJobTicket> StartRlTrainingAsync(EphemeralRlRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TrainingJobTicket> StartPortfolioRlTrainingAsync(EphemeralPortfolioRlRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SingleSymbolGateway(string symbol) : EmptyReadOnlyStockAnalyzerGateway
    {
        public override Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { symbol });
    }
}
