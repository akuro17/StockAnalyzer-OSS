#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>The registered scatter metric settings: defaults, binding from configuration, exposure through the settings interface and startup validation.</summary>
public class ScatterMetricSettingsTests
{
    private static StockAnalyzerSettings Create(ScatterMetricSettings? scatter)
        => new(
            new ConfigurationBuilder().Build(),
            Options.Create(new PythonSettings()),
            Options.Create(new ChartDefaultSettings()),
            Options.Create(new PredictionSettings()),
            Options.Create(new ScreenerSettings()),
            Options.Create(new SmartScreenerSettings()),
            Options.Create(new InfrastructureSettings()),
            Options.Create(new MarketStructureSettings()),
            Options.Create(new PatternRecognitionSettings()),
            Options.Create(new ResilienceSettings()),
            Options.Create(new LocalizationSettings()),
            scatterMetricOptions: scatter is null ? null : Options.Create(scatter));

    [Fact]
    public void WithoutASection_TheShippedDefaultsAreUsed()
    {
        IStockAnalyzerSettings settings = Create(null);

        Assert.Equal(ScatterMetricLimits.DefaultMaxRegisteredMetrics, settings.ScatterMaxRegisteredMetrics);
        Assert.Equal(ScatterMetricLimits.DefaultMaxOffset, settings.ScatterMaxMetricOffset);
        Assert.Equal(ScatterMetricLimits.DefaultMaxParallelism, settings.ScatterMaxParallelism);
        Assert.Equal(ScatterMetricLimits.DefaultInputDebounceMilliseconds, settings.ScatterInputDebounceMilliseconds);
    }

    [Fact]
    public void ConfiguredValues_AreExposedThroughTheSettingsInterface()
    {
        IStockAnalyzerSettings settings = Create(new ScatterMetricSettings
        {
            MaxRegisteredMetrics = 40,
            MaxOffset = 800,
            MaxParallelism = 6,
            InputDebounceMilliseconds = 150,
        });

        Assert.Equal(40, settings.ScatterMaxRegisteredMetrics);
        Assert.Equal(800, settings.ScatterMaxMetricOffset);
        Assert.Equal(6, settings.ScatterMaxParallelism);
        Assert.Equal(150, settings.ScatterInputDebounceMilliseconds);
    }

    [Fact]
    public void AValueOutsideItsHardBand_StopsTheStartup_NamingTheKey()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Create(new ScatterMetricSettings { MaxRegisteredMetrics = 129 }));

        Assert.Contains(ScatterMetricSettingsValidator.MaxRegisteredMetricsKey, error.Message);
    }

    [Fact]
    public void TheSectionBindsFromConfiguration_ByItsKeys()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ScatterMetricSettingsValidator.MaxRegisteredMetricsKey] = "12",
            [ScatterMetricSettingsValidator.MaxOffsetKey] = "250",
            [ScatterMetricSettingsValidator.MaxParallelismKey] = "3",
            [ScatterMetricSettingsValidator.InputDebounceMillisecondsKey] = "200",
        }).Build();

        var bound = new ScatterMetricSettings();
        configuration.GetSection(ScatterMetricSettingsValidator.SectionName).Bind(bound);

        Assert.Equal(12, bound.MaxRegisteredMetrics);
        Assert.Equal(250, bound.MaxOffset);
        Assert.Equal(3, bound.MaxParallelism);
        Assert.Equal(200, bound.InputDebounceMilliseconds);
    }

    [Fact]
    public void TheShippedAppSettingsExample_BindsToTheDefaults()
    {
        string path = Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "appsettings.example.json");
        IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        var bound = new ScatterMetricSettings();
        configuration.GetSection(ScatterMetricSettingsValidator.SectionName).Bind(bound);

        Assert.Equal(ScatterMetricLimits.DefaultMaxRegisteredMetrics, bound.MaxRegisteredMetrics);
        Assert.Equal(ScatterMetricLimits.DefaultMaxOffset, bound.MaxOffset);
        Assert.Equal(ScatterMetricLimits.DefaultMaxParallelism, bound.MaxParallelism);
        Assert.Equal(ScatterMetricLimits.DefaultInputDebounceMilliseconds, bound.InputDebounceMilliseconds);
    }

    [Fact]
    public void TheCommonServices_BindTheSection_AndRegisterTheRegistryAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddCommonServices(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ScatterMetricSettingsValidator.MaxParallelismKey] = "5",
        }).Build());

        // The registry is only looked up, never built: building it would read the user's real Data\Config\scatter_metrics.json.
        ServiceDescriptor registry = Assert.Single(services, d => d.ServiceType == typeof(IScatterMetricRegistry));
        Assert.Equal(typeof(ScatterMetricRegistry), registry.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, registry.Lifetime);

        ServiceDescriptor evaluator = Assert.Single(services, d => d.ServiceType == typeof(IScatterMetricEvaluator));
        Assert.Equal(typeof(ScatterMetricEvaluator), evaluator.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, evaluator.Lifetime);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ScatterMetricSettings>>().Value;
        Assert.Equal(5, options.MaxParallelism);
        Assert.Equal(ScatterMetricLimits.DefaultMaxRegisteredMetrics, options.MaxRegisteredMetrics);
    }
}
