#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Services.StoredPeriod;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The optional source folders of an RL run: a caller can name the folders (and nothing is created for them), while a caller that names none
/// keeps the fixed Data folders (pinned by <see cref="RlLocalSnapshotFactoryCharacterizationTests"/>, which passes unchanged).
/// </summary>
public sealed class RlLocalSourceFoldersTests : IDisposable
{
    private const int SmokeBars = 1_278;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_folders_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static RlTrainingJob Job(string symbol) => new(symbol, RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(SmokeBars), new BacktestConfiguration(), null, true);

    /// <summary>Writes a symbol under the usual names, then moves the two folders to names the application never uses.</summary>
    private RlLocalSourceFolders OddlyNamedFolders(string symbol)
    {
        string tree = Path.Combine(_root, "tree");
        RlSourceFixture.WriteLocalSymbol(tree, symbol, RlSourceFixture.FirstDay, SmokeBars);
        string prices = Path.Combine(_root, "elsewhere", "Prices"), meta = Path.Combine(_root, "elsewhere", "Meta");
        Directory.CreateDirectory(Path.GetDirectoryName(prices)!);
        Directory.Move(Path.Combine(tree, "Daily"), prices);
        Directory.Move(Path.Combine(tree, "Metadata"), meta);
        return new RlLocalSourceFolders(prices, meta);
    }

    [Fact]
    public async Task NamedFoldersAreReadAndTheirBytesCopiedUnchanged()
    {
        var folders = OddlyNamedFolders("ABC");
        string snapshot = Path.Combine(_root, "snapshot");

        var source = await RlLocalSnapshotFactory.PrepareAsync(Job("ABC"), snapshot, RlSourceFixture.Limits, CancellationToken.None, folders);

        Assert.Equal(ContentHash.OfFile(Path.Combine(folders.Daily, "ABC.parquet")), ContentHash.OfFile(Path.Combine(snapshot, RlFiles.RawParquet)));
        Assert.Equal(ContentHash.OfFile(Path.Combine(folders.Metadata, "ABC.meta.parquet")), ContentHash.OfFile(Path.Combine(snapshot, RlFiles.MetadataParquet)));
        Assert.Equal(SmokeBars, source.Bars.Length);
        Assert.Equal("Daily/ABC.parquet", source.Manifest.SourcePathIdentity);          // the approved logical identity, not the odd folder name
        Assert.Equal("Metadata/ABC.meta.parquet", source.Manifest.MetadataPathIdentity);
    }

    [Fact]
    public async Task NamedFoldersThatDoNotExistAreNotCreated()
    {
        var folders = new RlLocalSourceFolders(Path.Combine(_root, "nope", "Daily"), Path.Combine(_root, "nope", "Metadata"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("ABC"), Path.Combine(_root, "snapshot"), RlSourceFixture.Limits, CancellationToken.None, folders));

        Assert.False(Directory.Exists(Path.Combine(_root, "nope")));
    }

    [Fact]
    public async Task NamedFoldersWithoutTheSymbolAreRefusedAndNothingIsCopied()
    {
        var folders = OddlyNamedFolders("ABC");
        string snapshot = Path.Combine(_root, "snapshot");

        await Assert.ThrowsAsync<FileNotFoundException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("XYZ"), snapshot, RlSourceFixture.Limits, CancellationToken.None, folders));

        Assert.False(Directory.Exists(snapshot));
    }

    [Fact]
    public void FromSettingsResolvesTheConfiguredFoldersWithoutCreatingThem()
    {
        string daily = Path.Combine(_root, "cfg", "D"), meta = Path.Combine(_root, "cfg", "M");

        var folders = RlLocalSourceFolders.FromSettings(new MarketDataSettings { DailyDataPath = daily, MetadataPath = meta });

        Assert.Equal(Path.GetFullPath(daily), folders.Daily);
        Assert.Equal(Path.GetFullPath(meta), folders.Metadata);
        Assert.False(Directory.Exists(Path.Combine(_root, "cfg")));
    }

    [Fact]
    public void TrainingJobGateDefaultConstructorDoesNotCreateDataFolder()
    {
        string dataRoot = PathDiscovery.ResolveDataPathWithoutCreating(null, "Data");
        bool existedBefore = Directory.Exists(dataRoot);

        var gate = new TrainingJobGate();
        Assert.NotNull(gate);

        if (!existedBefore)
        {
            Assert.False(Directory.Exists(dataRoot));
        }
    }

    [Fact]
    public async Task StoredPeriodAdvisorWithMissingFoldersDoesNotCreateDirectories()
    {
        string missingDaily = Path.Combine(_root, "missing", "Daily");
        string missingMeta = Path.Combine(_root, "missing", "Metadata");
        var settings = Options.Create(new MarketDataSettings { DailyDataPath = missingDaily, MetadataPath = missingMeta });
        var advisor = new StoredPeriodAdvisor(settings);

        var trainingProposal = await advisor.ProposeForTrainingAsync(new[] { "ABC" }, TrainingTimeframe.Daily, 10, null, CancellationToken.None);
        var rlProposal = await advisor.ProposeForRlAsync("ABC", 10, null, CancellationToken.None);
        var latestDate = await advisor.LatestFinalizedDateAsync(new[] { "ABC" }, CancellationToken.None);

        Assert.Equal(StoredPeriodStatus.NoData, trainingProposal.Status);
        Assert.Equal(StoredPeriodStatus.NoData, rlProposal.Status);
        Assert.Null(latestDate);
        Assert.False(Directory.Exists(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task StoredPeriodAdvisorProposeForRlAsyncWithNamedFoldersReadsSuccessfully()
    {
        var folders = OddlyNamedFolders("ABC");
        var advisor = new StoredPeriodAdvisor();

        var proposal = await advisor.ProposeForRlAsync("ABC", 100, null, CancellationToken.None, folders);

        Assert.Equal(StoredPeriodStatus.Found, proposal.Status);
        Assert.Equal(100, proposal.RequiredBars);
    }

    [Fact]
    public async Task RlPanelPeriodAdvisorSuggestAsyncWithMissingFoldersDoesNotCreateDirectories()
    {
        var missingFolders = new RlLocalSourceFolders(Path.Combine(_root, "panel_missing", "Daily"), Path.Combine(_root, "panel_missing", "Metadata"));

        var suggestion = await RlPanelPeriodAdvisor.SuggestAsync(
            new[] { "ABC", "XYZ" }, DateOnly.FromDateTime(DateTime.UtcNow), RlPanelEvaluationMode.Standard,
            RlSourceFixture.Limits, folders: missingFolders);

        Assert.Equal(RlStartSuggestionStatus.NoUsableData, suggestion.Status);
        Assert.False(Directory.Exists(Path.Combine(_root, "panel_missing")));
    }

    [Fact]
    public async Task RlPanelPreflightEvaluatorEvaluateAsyncWithMissingFoldersDoesNotCreateDirectories()
    {
        var missingFolders = new RlLocalSourceFolders(Path.Combine(_root, "preflight_missing", "Daily"), Path.Combine(_root, "preflight_missing", "Metadata"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var preflight = await RlPanelPreflightEvaluator.EvaluateAsync(
            new[] { "ABC", "XYZ" }, today.AddDays(-100), today, false, RlPanelEvaluationMode.Standard,
            new RlPortfolioSettings(), RlSourceFixture.Limits, folders: missingFolders);

        Assert.False(preflight.Feasible);
        Assert.False(Directory.Exists(Path.Combine(_root, "preflight_missing")));
    }

    private const int PanelBars = 1_400;
    private static readonly string[] PanelSymbols = { "PRLA", "PRLB", "PRLC", "PRLD" };

    /// <summary>Several symbols under the usual names, then both folders moved to names the application never uses.</summary>
    private RlLocalSourceFolders OddlyNamedPanelFolders()
    {
        string tree = Path.Combine(_root, "panel_tree");
        foreach (string symbol in PanelSymbols) RlSourceFixture.WriteLocalSymbol(tree, symbol, RlSourceFixture.FirstDay, PanelBars);
        string prices = Path.Combine(_root, "panel_elsewhere", "Prices"), meta = Path.Combine(_root, "panel_elsewhere", "Meta");
        Directory.CreateDirectory(Path.GetDirectoryName(prices)!);
        Directory.Move(Path.Combine(tree, "Daily"), prices);
        Directory.Move(Path.Combine(tree, "Metadata"), meta);
        return new RlLocalSourceFolders(prices, meta);
    }

    [Fact]
    public async Task PortfolioServiceWithoutFoldersReadsOnlyTheFixedDataFolders()
    {
        OddlyNamedPanelFolders();
        var service = new RlPortfolioService(new RlPortfolioSettings());

        var preflight = await service.PreflightAsync(PanelSymbols, RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(PanelBars),
            true, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);

        Assert.All(preflight.Symbols, s => Assert.Equal(RlPanelSymbolStatus.MissingFile, s.Status));
    }

    [Fact]
    public async Task PortfolioServiceWithNamedFoldersPreflightsAndSuggestsFromThem()
    {
        var folders = OddlyNamedPanelFolders();
        var service = new RlPortfolioService(new RlPortfolioSettings(), sourceFolders: folders);
        var end = RlSourceFixture.FirstDay.AddDays(PanelBars);

        var preflight = await service.PreflightAsync(PanelSymbols, RlSourceFixture.FirstDay, end, true, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);
        var suggestion = await service.SuggestStartAsync(PanelSymbols, end, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);

        Assert.All(preflight.Symbols, s => Assert.Equal(RlPanelSymbolStatus.Ready, s.Status));
        Assert.Equal(PanelSymbols.Length, preflight.ReadyCount);
        Assert.NotNull(suggestion);
        Assert.NotEqual(RlStartSuggestionStatus.NoUsableData, suggestion.Status);
    }

    [Fact]
    public async Task RlExternalEpisodeFactoryWithNamedFoldersReadsSuccessfully()
    {
        var folders = OddlyNamedFolders("ABC");
        using var workspace = RlReplayWorkspace.Create(_root);

        var range = await RlExternalEpisodeFactory.ReadAnchorRangeAsync("ABC", RlSourceFixture.Limits, workspace, CancellationToken.None, folders);

        Assert.NotNull(range);
        Assert.True(range.EarliestAnchor <= range.LatestAnchor);

        var episode = await RlExternalEpisodeFactory.PrepareAsync("ABC", range.EarliestAnchor, RlSourceFixture.Limits, workspace, CancellationToken.None, folders: folders);
        Assert.NotNull(episode);
        Assert.Equal("ABC", episode.Manifest.Symbol);
    }

    [Fact]
    public async Task CaptureAsync_WithNullFolders_ThrowsWithoutCreatingDestination()
    {
        string destination = Path.Combine(_root, "dest_null_folders");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            RlLocalSourceFiles.CaptureAsync("NONEXISTENT_SYMBOL_XYZ", destination, RlSourceFixture.Limits, CancellationToken.None, folders: null));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void ResolveModelGenerationsDirectoryWithoutCreating_ReturnsValidPath()
    {
        string dir = PathDiscovery.ResolveModelGenerationsDirectoryWithoutCreating();
        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.EndsWith("Generations", dir, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Daily", "ABC.parquet", "Daily/ABC.parquet")]
    [InlineData("Daily/", "ABC.parquet", "Daily/ABC.parquet")]
    [InlineData("Data/Daily\\", "ABC.parquet", "Daily/ABC.parquet")]
    public void LogicalIdentity_HandlesTrailingSeparators(string folder, string file, string expected)
    {
        string result = RlLocalSourceFiles.LogicalIdentity(folder, file);
        Assert.Equal(expected, result);
    }
}
