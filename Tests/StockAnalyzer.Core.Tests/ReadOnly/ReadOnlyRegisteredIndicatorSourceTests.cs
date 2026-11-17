using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>
/// <see cref="FileReadOnlyRegisteredIndicatorSource"/> reads what the application's own services saved, and never quarantines, creates or rewrites a file.
/// </summary>
public sealed class ReadOnlyRegisteredIndicatorSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_ro_registered_" + Guid.NewGuid().ToString("N"));

    public ReadOnlyRegisteredIndicatorSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string SourcesPath => Path.Combine(_root, "source_indicators.json");

    private string DriversPath => Path.Combine(_root, "dynamic_period_drivers.json");

    [Fact]
    public async Task MissingFiles_AreEmptyLists_AndNothingIsCreated()
    {
        var source = new FileReadOnlyRegisteredIndicatorSource(SourcesPath, DriversPath);

        Assert.Empty(await source.GetSourceIndicatorsAsync());
        Assert.Empty(await source.GetDynamicPeriodDriversAsync());
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task SavedIndicators_AreReadAsTheApplicationLoadsThem()
    {
        CoreIndicatorSettings rsi = new ScreenerCatalogProvider().GetDefaultSettings(IndicatorType.RSI, new IndicatorFactory())!;
        CoreIndicatorSettings sma = new ScreenerCatalogProvider().GetDefaultSettings(IndicatorType.SMA, new IndicatorFactory())!;
        using (var sources = new SourceIndicatorService(SourcesPath))
        {
            await sources.SaveSourceIndicatorAsync(rsi);
        }

        using (var drivers = new DynamicPeriodDriverService(DriversPath))
        {
            await drivers.SaveDynamicPeriodDriverAsync(sma);
        }

        var source = new FileReadOnlyRegisteredIndicatorSource(SourcesPath, DriversPath);

        CoreIndicatorSettings readSource = Assert.Single(await source.GetSourceIndicatorsAsync());
        CoreIndicatorSettings readDriver = Assert.Single(await source.GetDynamicPeriodDriversAsync());

        Assert.Equal(rsi.Id, readSource.Id);
        Assert.Equal(IndicatorType.RSI, readSource.TypeEnum);
        Assert.NotNull(readSource.ParameterObject);
        Assert.Equal(sma.Id, readDriver.Id);
        Assert.Equal(IndicatorType.SMA, readDriver.TypeEnum);
    }

    [Fact]
    public async Task ADamagedFile_IsDataUnavailable_AndIsLeftExactlyAsItWas()
    {
        File.WriteAllText(SourcesPath, "{ not a list");
        string[] before = Directory.GetFiles(_root);
        var source = new FileReadOnlyRegisteredIndicatorSource(SourcesPath, DriversPath);

        await Assert.ThrowsAsync<DataUnavailableException>(() => source.GetSourceIndicatorsAsync());

        Assert.Equal(before, Directory.GetFiles(_root)); // the application's store would have copied it to a .corrupted file
        Assert.Equal("{ not a list", File.ReadAllText(SourcesPath));
    }
}
