using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan task T4: a WaveformMatching (DTW) evaluation that fails must say so, and the call's cancellation token must reach the DTW scan. The tool is built
/// directly with a DTW service the test supplies (a real implementation of the service interface, not a mock of project data).
/// </summary>
public sealed class DetectChartPatternsDtwDiagnosticsTests : IDisposable
{
    private const string Arguments = """{"symbol":"MSFT","timeFrame":"D1","families":["WaveformMatching"]}""";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(120, "MSFT");
    private readonly ServiceProvider _services;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;

    public DetectChartPatternsDtwDiagnosticsTests()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();
        _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
        _gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
    }

    public void Dispose()
    {
        _services.Dispose();
        _fixture.Dispose();
    }

    private sealed class ScriptedDtwService : IPatternRecognitionService
    {
        private readonly PatternRecognitionResult _result;

        public ScriptedDtwService(PatternRecognitionResult result) => _result = result;

        public CancellationToken ReceivedToken { get; private set; }

        public Task<PatternRecognitionResult> DetectAsync(
            IReadOnlyList<CandleData> candles, int minWindow, int maxWindow, int windowStep, double threshold,
            int warpingRadius, double shortSpanPenaltyAlpha, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            return Task.FromResult(_result);
        }
    }

    private async Task<JsonDocument> InvokeAsync(IPatternRecognitionService dtw, CancellationToken token)
    {
        var tool = new DetectChartPatternsTool(_gateway, new SymbolResolver(_gateway), new McpLimits(), dtw);
        using JsonDocument arguments = JsonDocument.Parse(Arguments);
        return JsonDocument.Parse(await tool.InvokeAsync(arguments.RootElement, token));
    }

    [Fact]
    public async Task AFailedWaveformEvaluation_IsReportedInWarnings_NotAsAnEmptyAnswer()
    {
        using JsonDocument answer = await InvokeAsync(new ScriptedDtwService(PatternRecognitionResult.Failure("warpingRadius must be >= -1")), CancellationToken.None);

        Assert.Equal(0, answer.RootElement.GetProperty("patterns").GetArrayLength());
        string[] warnings = answer.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
        Assert.Contains(warnings, w => w.StartsWith("WaveformMatching could not be evaluated", StringComparison.Ordinal) && w.Contains("warpingRadius must be >= -1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASuccessfulEmptyWaveformEvaluation_HasNoWarnings()
    {
        using JsonDocument answer = await InvokeAsync(new ScriptedDtwService(PatternRecognitionResult.Success(Array.Empty<DetectedPattern>())), CancellationToken.None);

        Assert.Equal(0, answer.RootElement.GetProperty("patterns").GetArrayLength());
        Assert.False(answer.RootElement.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task TheCallsCancellationTokenReachesTheWaveformService()
    {
        using var source = new CancellationTokenSource();
        var dtw = new ScriptedDtwService(PatternRecognitionResult.Success(Array.Empty<DetectedPattern>()));

        using JsonDocument answer = await InvokeAsync(dtw, source.Token);

        Assert.Equal(source.Token, dtw.ReceivedToken);
    }
}
