using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>
/// Verifies that externalized settings are correctly read from IConfiguration / IOptions
/// and fall back to sensible defaults when keys are missing.
/// </summary>
public class SettingsExternalizationTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static StockAnalyzerSettings CreateSettings(
        Dictionary<string, string?> configValues,
        PythonSettings? pythonOverride = null,
        ChartDefaultSettings? chartOverride = null,
        StockAnalyzer.Core.Models.Settings.BacktestSettings? backtestOverride = null,
        StockAnalyzer.Core.Models.Settings.DrawingInteractionSettings? drawingInteractionOverride = null)
    {
        var config = BuildConfig(configValues);

        // Bind POCOs from configuration (mimics services.Configure<T> behavior)
        var pythonSettings = pythonOverride ?? new PythonSettings();
        if (pythonOverride == null)
            config.GetSection("Python").Bind(pythonSettings);

        var chartSettings = chartOverride ?? new ChartDefaultSettings();
        if (chartOverride == null)
            config.GetSection("Chart").Bind(chartSettings);

        return new StockAnalyzerSettings(
            config,
            Options.Create(pythonSettings),
            Options.Create(chartSettings),
            Options.Create(new PredictionSettings()),
            Options.Create(new ScreenerSettings()),
            Options.Create(new SmartScreenerSettings()),
            Options.Create(new InfrastructureSettings()),
            Options.Create(new MarketStructureSettings()),
            Options.Create(new PatternRecognitionSettings()),
            Options.Create(new ResilienceSettings()),
            Options.Create(new LocalizationSettings()),
            backtestOptions: Options.Create(backtestOverride ?? new StockAnalyzer.Core.Models.Settings.BacktestSettings()),
            drawingInteractionOptions: Options.Create(drawingInteractionOverride ?? new StockAnalyzer.Core.Models.Settings.DrawingInteractionSettings())
        );
    }

    [Fact]
    public void DrawingInteraction_ReadsTheBoundSetting_AndRejectsNonPositiveBounds()
    {
        var settings = CreateSettings(new Dictionary<string, string?>(),
            drawingInteractionOverride: new StockAnalyzer.Core.Models.Settings.DrawingInteractionSettings { MaxStrokeSamples = 1234 });

        Assert.Equal(1234, settings.DrawingInteraction.MaxStrokeSamples);
        Assert.Throws<InvalidOperationException>(() => CreateSettings(new Dictionary<string, string?>(),
            drawingInteractionOverride: new StockAnalyzer.Core.Models.Settings.DrawingInteractionSettings { MaxHistoryEntriesPerContext = 0 }));
    }

    [Fact]
    public void BacktestMaxConditionOffset_ReadsTheBoundSetting()
    {
        var settings = CreateSettings(new Dictionary<string, string?>(),
            backtestOverride: new StockAnalyzer.Core.Models.Settings.BacktestSettings { MaxConditionOffset = 77 });

        Assert.Equal(77, settings.BacktestMaxConditionOffset);
    }

    [Fact]
    public void BacktestMaxConditionOffset_DefaultsToThePocoDefault_AndTheShippedAppsettingsAgree()
    {
        int pocoDefault = new StockAnalyzer.Core.Models.Settings.BacktestSettings().MaxConditionOffset;
        var config = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
        var bound = new StockAnalyzer.Core.Models.Settings.BacktestSettings();
        config.GetSection("Backtest").Bind(bound);

        Assert.Equal(pocoDefault, CreateSettings(new Dictionary<string, string?>()).BacktestMaxConditionOffset);
        Assert.Equal(pocoDefault, ((StockAnalyzer.Core.Services.IStockAnalyzerSettings)new MockStockAnalyzerSettings()).BacktestMaxConditionOffset);
        Assert.Equal(pocoDefault, bound.MaxConditionOffset);
    }

    [Fact]
    public void BacktestMaxConditionOffset_BelowTheCausalityMinimum_IsRejectedAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => CreateSettings(new Dictionary<string, string?>(),
            backtestOverride: new StockAnalyzer.Core.Models.Settings.BacktestSettings { MaxConditionOffset = -1 }));
    }

    [Fact]
    public void PythonMaxRetries_ReadsFromConfig()
    {
        var settings = CreateSettings(new Dictionary<string, string?>
        {
            ["Python:MaxRetries"] = "5"
        });

        Assert.Equal(5, settings.PythonMaxRetries);
    }

    [Fact]
    public void PythonMaxRetries_FallsBackToDefault_WhenKeyMissing()
    {
        var settings = CreateSettings(new Dictionary<string, string?>());

        Assert.Equal(3, settings.PythonMaxRetries);
    }

    [Fact]
    public void PythonBackoffMs_ReadsFromConfig()
    {
        var settings = CreateSettings(new Dictionary<string, string?>
        {
            ["Python:BackoffMs"] = "2000"
        });

        Assert.Equal(2000, settings.PythonBackoffMs);
    }

    [Fact]
    public void PythonBackoffMs_FallsBackToDefault_WhenKeyMissing()
    {
        var settings = CreateSettings(new Dictionary<string, string?>());

        Assert.Equal(1000, settings.PythonBackoffMs);
    }

    [Fact]
    public void PythonHealthCheckIntervalMs_ReadsFromConfig()
    {
        var settings = CreateSettings(new Dictionary<string, string?>
        {
            ["Python:HealthCheckIntervalMs"] = "10000"
        });

        Assert.Equal(10000, settings.PythonHealthCheckIntervalMs);
    }

    [Fact]
    public void PythonHealthCheckIntervalMs_FallsBackToDefault_WhenKeyMissing()
    {
        var settings = CreateSettings(new Dictionary<string, string?>());

        Assert.Equal(5000, settings.PythonHealthCheckIntervalMs);
    }

    [Fact]
    public void DefaultSymbol_ReadsFromConfig()
    {
        var settings = CreateSettings(new Dictionary<string, string?>
        {
            ["Chart:DefaultSymbol"] = "AAPL"
        });

        Assert.Equal("AAPL", settings.DefaultSymbol);
    }

    [Fact]
    public void DefaultSymbol_FallsBackToChartConstant_WhenKeyMissing()
    {
        var settings = CreateSettings(new Dictionary<string, string?>());

        Assert.Equal(StockAnalyzer.Core.ChartConstants.DefaultSymbol, settings.DefaultSymbol);
    }

    [Fact]
    public void DefaultSymbol_EmptyConfig_MeansNoInitialSymbol()
    {
        var settings = CreateSettings(new Dictionary<string, string?>
        {
            ["Chart:DefaultSymbol"] = ""
        });

        Assert.Equal(string.Empty, settings.DefaultSymbol);
    }

    [Fact]
    public void DefaultSymbol_FallbackConstant_IsEmpty()
    {
        Assert.Equal(string.Empty, StockAnalyzer.Core.ChartConstants.DefaultSymbol);
        new ChartDefaultSettings().Validate();
    }

    [Fact]
    public void AllPythonSettings_ReadCorrectly_WhenFullSectionProvided()
    {
        var settings = CreateSettings(new Dictionary<string, string?>
        {
            ["Python:MaxRetries"] = "7",
            ["Python:BackoffMs"] = "3000",
            ["Python:HealthCheckIntervalMs"] = "15000"
        });

        Assert.Equal(7, settings.PythonMaxRetries);
        Assert.Equal(3000, settings.PythonBackoffMs);
        Assert.Equal(15000, settings.PythonHealthCheckIntervalMs);
    }

    [Fact]
    public void PythonSettings_UsesPocoDefaults_WhenConfigSectionMissing()
    {
        // Verify that the POCO class defaults are the correct fallback values
        var pythonDefaults = new PythonSettings();
        Assert.Equal(3, pythonDefaults.MaxRetries);
        Assert.Equal(1000, pythonDefaults.BackoffMs);
        Assert.Equal(5000, pythonDefaults.HealthCheckIntervalMs);
        Assert.Equal("Scripts", pythonDefaults.ScriptDirectory);
        Assert.Equal("server.py", pythonDefaults.ServerScriptName);
    }
}
