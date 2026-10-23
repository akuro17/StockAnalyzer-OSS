using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.ReadOnly;

/// <summary>
/// The read-only composition root, resolved through a real container. Lives here (not in Core.Tests) because this project
/// has the container implementation and is the target of the standard unit-test command.
/// </summary>
public sealed class ReadOnlyCoreCompositionTests : IDisposable
{
    private static readonly string[] ForbiddenServiceTypeNames =
    {
        "IPythonService", "IMarketDataProvider", "ITemplateService", "IChartSettingsManager", "IBacktestConfigurationManager",
        "IWatchlistManager", "ITrainingOrchestrator", "ITickerSyncService",
    };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(20, "msft", "7203.T");

    public void Dispose() => _fixture.Dispose();

    private static IConfiguration ConfigurationOf(MarketDataSettings settings) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = settings.TickerListPath,
        })
        .Build();

    private ServiceProvider BuildProvider()
        => new ServiceCollection().AddStockAnalyzerReadOnlyCore(ConfigurationOf(_fixture.Settings)).BuildServiceProvider();

    private static HashSet<int> PythonProcessIds()
        => Process.GetProcesses().Where(p => p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();

    private static IReadOnlyList<string> SnapshotOfParentOf(string filePath) => ReadOnlyDataTreeFixture.SnapshotOf(Path.GetDirectoryName(filePath)!);

    [Fact]
    public async Task ASessionThroughTheContainerChangesNoFileAndStartsNoPython()
    {
        string sourceFile = PathDiscovery.ResolveSourceIndicatorsPathWithoutCreating(SourceIndicatorService.FileName);
        string driverFile = PathDiscovery.ResolveDynamicPeriodDriversPathWithoutCreating(DynamicPeriodDriverService.FileName);
        var fixtureBefore = _fixture.Snapshot();
        var sourceBefore = SnapshotOfParentOf(sourceFile);
        var driverBefore = SnapshotOfParentOf(driverFile);
        var pythonBefore = PythonProcessIds();

        using (var provider = BuildProvider())
        {
            var gateway = provider.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
            var tickers = await gateway.GetAvailableTickersAsync();
            var candles = await gateway.GetCandlesAsync("MSFT", TimeFrame.D1, 20);
            await gateway.GetStoredTickerMetadataAsync("MSFT");
            await gateway.GetTimeSeriesLastUpdatedAsync("7203.T");
            var results = await gateway.CalculateIndicatorsAsync(
                candles.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
                new[] { new CoreIndicatorSettings { Id = "sma", IsEnabled = true, TypeEnum = IndicatorType.SMA, ParameterObject = new CoreSmaParameter { Period = 5 } } });

            Assert.Equal(new[] { "MSFT", "7203-T" }, tickers);
            Assert.Equal(20, candles.Count);
            Assert.True(results["sma"].IsSuccessful);
        }

        Assert.Equal(fixtureBefore, _fixture.Snapshot());
        Assert.Equal(sourceBefore, SnapshotOfParentOf(sourceFile));
        Assert.Equal(driverBefore, SnapshotOfParentOf(driverFile));
        Assert.Empty(PythonProcessIds().Except(pythonBefore));
    }

    [Fact]
    public async Task AnIncompleteDataTreeGivesEmptyResultsAndCreatesNothing()
    {
        Directory.Delete(_fixture.Settings.WeeklyDataPath!, recursive: true);
        File.Delete(_fixture.TickerListPath);
        var before = _fixture.Snapshot();

        using (var provider = BuildProvider())
        {
            Assert.False(provider.GetRequiredService<ReadOnlyDataTree>().IsComplete);
            var gateway = provider.GetRequiredService<IReadOnlyStockAnalyzerGateway>();

            Assert.Empty(await gateway.GetAvailableTickersAsync());
            Assert.Empty(await gateway.GetCandlesAsync("MSFT", TimeFrame.D1, 5));
            Assert.Null(await gateway.GetStoredTickerMetadataAsync("MSFT"));
            Assert.Null(await gateway.GetTimeSeriesLastUpdatedAsync("MSFT"));
        }

        Assert.Equal(before, _fixture.Snapshot());
        Assert.False(Directory.Exists(_fixture.Settings.WeeklyDataPath));
        Assert.False(File.Exists(_fixture.TickerListPath));
    }

    [Fact]
    public void TheRegistrationsContainNoServiceThatWritesOrStartsAProcess()
    {
        var services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(ConfigurationOf(_fixture.Settings));

        string[] registered = services.Select(d => d.ServiceType.Name).ToArray();

        Assert.Empty(registered.Intersect(ForbiddenServiceTypeNames));
        Assert.DoesNotContain(registered, name => name.EndsWith("Exporter", StringComparison.Ordinal));
        Assert.DoesNotContain(registered, name => name.EndsWith("SettingsManager", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettingsAreBuiltFromCoreTypesOnly()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IStockAnalyzerSettings>());
    }

    [Fact]
    public void TheGuiAndTheReadOnlyRootsBindTheSameValuesFromTheShippedConfiguration()
    {
        string appsettings = Path.Combine(TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "appsettings.json");
        IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(appsettings, optional: false).Build();

        var gui = new ServiceCollection();
        StockAnalyzer.Avalonia.ServiceCollectionExtensions.AddCommonServices(gui, configuration);
        using var guiProvider = gui.BuildServiceProvider();
        using var readOnlyProvider = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();

        Type[] optionTypes =
        {
            typeof(PythonSettings), typeof(ChartDefaultSettings), typeof(PredictionSettings), typeof(MarketDataSettings), typeof(ScreenerSettings),
            typeof(ScatterMetricSettings), typeof(SmartScreenerSettings), typeof(InfrastructureSettings), typeof(MarketStructureSettings),
            typeof(PatternRecognitionSettings), typeof(ResilienceSettings), typeof(LocalizationSettings), typeof(BacktestSettings),
            typeof(PythonIpcSettings), typeof(DrawingInteractionSettings),
        };
        foreach (Type optionType in optionTypes)
        {
            Type optionsInterface = typeof(IOptions<>).MakeGenericType(optionType);
            object guiValue = guiProvider.GetRequiredService(optionsInterface);
            object readOnlyValue = readOnlyProvider.GetRequiredService(optionsInterface);
            string guiJson = JsonSerializer.Serialize(optionsInterface.GetProperty("Value")!.GetValue(guiValue), optionType);
            string readOnlyJson = JsonSerializer.Serialize(optionsInterface.GetProperty("Value")!.GetValue(readOnlyValue), optionType);

            Assert.True(guiJson == readOnlyJson, $"{optionType.Name} binds differently in the two roots.");
        }
    }
}
