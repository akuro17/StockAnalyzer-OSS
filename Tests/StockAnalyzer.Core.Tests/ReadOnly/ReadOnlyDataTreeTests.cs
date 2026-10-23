using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

public sealed class ReadOnlyDataTreeTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(5, "MSFT");

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ACompleteTree_IsComplete()
    {
        var tree = ReadOnlyDataTree.Probe(_fixture.Settings);

        Assert.True(tree.IsComplete);
        Assert.Empty(tree.MissingItems);
    }

    [Theory]
    [InlineData(nameof(MarketDataSettings.DailyDataPath))]
    [InlineData(nameof(MarketDataSettings.WeeklyDataPath))]
    [InlineData(nameof(MarketDataSettings.MonthlyDataPath))]
    [InlineData(nameof(MarketDataSettings.MetadataPath))]
    public void AMissingFolder_IsNamedBySettingAndNothingIsCreated(string settingName)
    {
        string path = (string)typeof(MarketDataSettings).GetProperty(settingName)!.GetValue(_fixture.Settings)!;
        Directory.Delete(path, recursive: true);
        var before = _fixture.Snapshot();

        var tree = ReadOnlyDataTree.Probe(_fixture.Settings);

        Assert.False(tree.IsComplete);
        Assert.Equal(new[] { settingName }, tree.MissingItems);
        Assert.Equal(before, _fixture.Snapshot());
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void AMissingTickerList_IsNamedAndNotCreated()
    {
        File.Delete(_fixture.TickerListPath);

        var tree = ReadOnlyDataTree.Probe(_fixture.Settings);

        Assert.Equal(new[] { nameof(MarketDataSettings.TickerListPath) }, tree.MissingItems);
        Assert.False(File.Exists(_fixture.TickerListPath));
    }

    [Fact]
    public void MissingItemsNeverContainAPath()
    {
        Directory.Delete(_fixture.Settings.WeeklyDataPath!, recursive: true);
        File.Delete(_fixture.TickerListPath);

        var tree = ReadOnlyDataTree.Probe(_fixture.Settings);

        Assert.All(tree.MissingItems, item => Assert.DoesNotContain(_fixture.Root, item));
    }

    [Fact]
    public void ControlExperiment_TheRegularProviderCreatesAMissingFolder_WhichIsWhyTheProbeExists()
    {
        Directory.Delete(_fixture.Settings.WeeklyDataPath!, recursive: true);
        using var db = new StockAnalyzer.Core.Services.DuckDBConnectionManager();

        _ = new StockAnalyzer.Core.Services.ParquetMarketDataProvider(
            db, UnavailablePythonService.Instance, Microsoft.Extensions.Options.Options.Create(_fixture.Settings));

        Assert.True(Directory.Exists(_fixture.Settings.WeeklyDataPath));
    }

    [Fact]
    public void InvalidSettingsFailFastLikeTheProviderConstructor()
    {
        _fixture.Settings.DailyDataPath = "bad|path";

        Assert.Throws<ArgumentException>(() => ReadOnlyDataTree.Probe(_fixture.Settings));
    }

    [Fact]
    public async Task TheEmptySourcesReportNothingAndNeverThrow()
    {
        var source = new EmptyReadOnlyMarketDataSource();
        var data = new EmptyDataService();

        Assert.Empty(await source.GetAvailableTickersAsync());
        Assert.Null(await source.TryGetStoredMetadataAsync("MSFT"));
        Assert.Null(await source.GetTimeSeriesLastUpdatedAsync("MSFT"));
        Assert.Empty(await data.LoadCandlesAsync("MSFT", TimeFrame.D1, 5));
    }

    [Fact]
    public async Task TheUnavailablePythonServiceFailsEveryOperationAndStartsNothing()
    {
        var python = UnavailablePythonService.Instance;

        Assert.False(python.IsInitializing);
        await Assert.ThrowsAsync<NotSupportedException>(() => python.InitializeAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => python.InitializeExternalProcessAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => python.PingExternalProcessAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => python.RunUpdatePipelineAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => python.RunAsync(_ => 1));
    }
}
