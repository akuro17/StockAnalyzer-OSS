using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_check_rl_playback_preflight reads the anchor range of the symbol from the configured MarketData folders (not only the fixed Data folders),
/// like the other data tools of the same server.
/// </summary>
public sealed class CheckRlPlaybackPreflightToolSourceFoldersTests : IDisposable
{
    private const int StoredDays = 1_400;
    private const string FirstDay = "2000-01-01";
    private const string Symbol = "PBK";
    private static readonly string ModelId = new('e', 64);
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(1, Symbol);
    private readonly string _generations = Path.Combine(Path.GetTempPath(), "PlaybackPreflightGenerations_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _fixture.Dispose();
        try { if (Directory.Exists(_generations)) Directory.Delete(_generations, recursive: true); } catch (IOException) { }
    }

    /// <summary>A registry that knows one generation whose folder holds only the saved job (the anchor range needs nothing else).</summary>
    private sealed class OneGenerationRegistry(string modelId, string symbol, string directory) : IRlModelGenerationRegistry
    {
        public string? RlActiveId => null;
        public IReadOnlyList<RlGeneration> ListRl() => Array.Empty<RlGeneration>();
        public ModelGenerationLease? AcquireRl(string id)
            => id == modelId ? new ModelGenerationLease(id, Path.Combine(directory, RlFiles.Model), () => { }) : null;
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string id)
            => id == modelId ? new RlGeneration(id, symbol, "revision", RlContract.ContractHash, "evaluation", 0m, 0m, new Dictionary<string, string>(), DateTimeOffset.UtcNow) : null;
        public Task<bool> ActivateRlAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task TheAnchorRangeIsReadFromTheConfiguredFoldersNotOnlyTheFixedDataFolders()
    {
        McpTestSupport.WriteEquity(_fixture, Symbol, FirstDay, StoredDays);
        string generationDirectory = Path.Combine(_generations, ModelId);
        Directory.CreateDirectory(generationDirectory);
        File.WriteAllText(Path.Combine(generationDirectory, RlFiles.Job),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["resource_limits"] = RlTrainingValidation.FreezeLimits(continuous: false) }, RlFiles.Json));
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build();

        using McpServerRuntime runtime = McpServerComposition.Create(configuration, new LogSink(new StringWriter()),
            rlRegistry: new OneGenerationRegistry(ModelId, Symbol, generationDirectory));
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        using JsonDocument response = (await McpTestSupport.CallAsync(runtime.Dispatcher, "sa_check_rl_playback_preflight",
            $$"""{"modelId":"{{ModelId}}","symbol":"{{Symbol}}"}"""))!;

        Assert.False(McpTestSupport.IsError(response));
        JsonElement result = McpTestSupport.Structured(response);
        Assert.True(result.GetProperty("canReplay").GetBoolean(), result.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() : "no reason");
        Assert.True(result.TryGetProperty("earliestAnchorDate", out _));
        Assert.True(result.TryGetProperty("latestAnchorDate", out _));
    }
}
