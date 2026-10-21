#nullable enable
using System;
using System.Linq;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class ScatterMetricSettingsValidatorTests
{
    private static string[] ValidateDefaults() => ScatterMetricSettingsValidator.Validate(
        ScatterMetricLimits.DefaultMaxRegisteredMetrics,
        ScatterMetricLimits.DefaultMaxOffset,
        ScatterMetricLimits.DefaultMaxParallelism,
        ScatterMetricLimits.DefaultInputDebounceMilliseconds).ToArray();

    [Fact]
    public void TheShippedDefaults_RunWithoutAWarning()
    {
        Assert.Empty(ValidateDefaults());
    }

    [Fact]
    public void TheOffsetBand_IsTheScreenersBand_NotACopy()
    {
        Assert.Equal(ScreenerConditionOffsetLimits.MaxConditionOffset, ScatterMetricLimits.MaxOffset);
        Assert.Equal(ScreenerConditionOffsetLimits.DefaultMaxConditionOffset, ScatterMetricLimits.DefaultMaxOffset);
    }

    [Fact]
    public void TheShippedAppSettingsExample_MatchesTheDefaults()
    {
        string example = System.IO.File.ReadAllText(System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "appsettings.example.json"));
        using var document = System.Text.Json.JsonDocument.Parse(example);
        var section = document.RootElement.GetProperty(ScatterMetricSettingsValidator.SectionName);

        Assert.Equal(ScatterMetricLimits.DefaultMaxRegisteredMetrics, section.GetProperty("MaxRegisteredMetrics").GetInt32());
        Assert.Equal(ScatterMetricLimits.DefaultMaxOffset, section.GetProperty("MaxOffset").GetInt32());
        Assert.Equal(ScatterMetricLimits.DefaultMaxParallelism, section.GetProperty("MaxParallelism").GetInt32());
        Assert.Equal(ScatterMetricLimits.DefaultInputDebounceMilliseconds, section.GetProperty("InputDebounceMilliseconds").GetInt32());
    }

    [Theory]
    [InlineData(0, 500, 10, 300, ScatterMetricSettingsValidator.MaxRegisteredMetricsKey)]
    [InlineData(129, 500, 10, 300, ScatterMetricSettingsValidator.MaxRegisteredMetricsKey)]
    [InlineData(32, -1, 10, 300, ScatterMetricSettingsValidator.MaxOffsetKey)]
    [InlineData(32, 2501, 10, 300, ScatterMetricSettingsValidator.MaxOffsetKey)]
    [InlineData(32, 500, 0, 300, ScatterMetricSettingsValidator.MaxParallelismKey)]
    [InlineData(32, 500, 65, 300, ScatterMetricSettingsValidator.MaxParallelismKey)]
    [InlineData(32, 500, 10, -1, ScatterMetricSettingsValidator.InputDebounceMillisecondsKey)]
    [InlineData(32, 500, 10, 2001, ScatterMetricSettingsValidator.InputDebounceMillisecondsKey)]
    public void AValueOutsideItsHardBand_StopsStartup_NamingTheKey(int metrics, int offset, int parallelism, int debounce, string key)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => ScatterMetricSettingsValidator.Validate(metrics, offset, parallelism, debounce));

        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData(33, 500, 10, 300, ScatterMetricSettingsValidator.MaxRegisteredMetricsKey)]
    [InlineData(32, 501, 10, 300, ScatterMetricSettingsValidator.MaxOffsetKey)]
    [InlineData(32, 500, 17, 300, ScatterMetricSettingsValidator.MaxParallelismKey)]
    [InlineData(32, 500, 10, 99, ScatterMetricSettingsValidator.InputDebounceMillisecondsKey)]
    [InlineData(32, 500, 10, 1001, ScatterMetricSettingsValidator.InputDebounceMillisecondsKey)]
    public void AValueBetweenTheBands_RunsWithExactlyOneWarning_NamingTheKey(int metrics, int offset, int parallelism, int debounce, string key)
    {
        var warnings = ScatterMetricSettingsValidator.Validate(metrics, offset, parallelism, debounce);

        var warning = Assert.Single(warnings);
        Assert.Contains(key, warning);
    }

    [Fact]
    public void TheBandEdges_AreAccepted()
    {
        Assert.Empty(ScatterMetricSettingsValidator.Validate(1, 0, 1, 100));
        ScatterMetricSettingsValidator.Validate(128, 2500, 64, 0);
    }
}
