#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// A registered metric is saved as JSON and read back at the next start. After the read the metric must be the same metric: the same label, the same
/// content key and the same value. The parameters are built the way the registration screen builds them (every public readable property of the
/// indicator's default parameter object, as the Screener's <c>BuildParameterDictionary</c> does).
/// </summary>
public class ScatterMetricParameterRoundTripTests
{
    private readonly ITestOutputHelper _output;
    private readonly ScreenerCatalogProvider _catalog = new();

    public ScatterMetricParameterRoundTripTests(ITestOutputHelper output) => _output = output;

    private static Dictionary<string, object> ParametersOf(CoreIndicatorParameterBase parameterObject)
    {
        var dictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo property in parameterObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            object? value = property.GetValue(parameterObject);
            if (value != null) dictionary[property.Name] = value;
        }

        return dictionary;
    }

    private ScatterMetricDefinition Build(IndicatorType type, Dictionary<string, object> parameters) => new()
    {
        Source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = type,
            OutputName = _catalog.GetOutputSeriesNames(type, IndicatorFactory.Default).FirstOrDefault() ?? IndicatorResult.MainSeriesName,
            CategoryType = ScreenerItemCategoryType.Indicator,
            TimeFrame = TimeFrame.D1,
            Offset = 2,
            Parameters = parameters,
        },
    };

    private static ScatterMetricDefinition RoundTrip(ScatterMetricDefinition metric)
    {
        string json = JsonSerializer.Serialize(new ScatterMetricFile { Metrics = { metric } }, new JsonSerializerOptions { WriteIndented = true });
        ScatterMetricDefinition restored = JsonSerializer.Deserialize<ScatterMetricFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Metrics.Single();
        ScatterMetricParameterNormalizer.Normalize(restored, IndicatorFactory.Default);
        return restored;
    }

    [Fact]
    public void EveryRegisteredIndicator_KeepsItsLabelAndContentKey_ThroughSaveAndReload()
    {
        var problems = new List<string>();
        var table = new StringBuilder();
        int checkedCount = 0;

        foreach (IndicatorType type in IndicatorFactory.Default.GetRegisteredTypes().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            CoreIndicatorParameterBase? parameterObject = IndicatorFactory.Default.Create(type)?.GetDefaultSettings()?.ParameterObject;
            if (parameterObject is null) continue;

            ScatterMetricDefinition original = Build(type, ParametersOf(parameterObject));
            checkedCount++;
            try
            {
                ScatterMetricDefinition restored = RoundTrip(original);
                if (original.ContentKey != restored.ContentKey)
                {
                    problems.Add($"{type}: ContentKey differs\n   {original.ContentKey}\n   {restored.ContentKey}");
                }

                if (original.Label != restored.Label)
                {
                    problems.Add($"{type}: Label differs '{original.Label}' vs '{restored.Label}'");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add($"{type}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        table.AppendLine($"Indicator types checked: {checkedCount}; problems: {problems.Count}");
        foreach (string problem in problems) table.AppendLine(problem);
        _output.WriteLine(table.ToString());

        Assert.True(checkedCount > 100, "Fewer indicator types than expected were checked.");
        Assert.True(problems.Count == 0, table.ToString());
    }

    [Theory]
    [InlineData(IndicatorType.RSI)]
    [InlineData(IndicatorType.SMA)]
    [InlineData(IndicatorType.MACD)]
    [InlineData(IndicatorType.BB)]
    [InlineData(IndicatorType.Stoch)]
    [InlineData(IndicatorType.ATR)]
    public void ARegisteredIndicator_GivesTheSameValue_BeforeAndAfterSaveAndReload(IndicatorType type)
    {
        CoreIndicatorParameterBase parameterObject = IndicatorFactory.Default.Create(type)!.GetDefaultSettings()!.ParameterObject!;
        ScatterMetricDefinition original = Build(type, ParametersOf(parameterObject));
        original.Source.Offset = 0;
        ScatterMetricDefinition restored = RoundTrip(original);
        var bars = ScatterMetricTestKit.Bars(400);

        decimal? before = ScreenerValueExtractor.Default.ExtractValueNullable(original.Source, bars);
        decimal? after = ScreenerValueExtractor.Default.ExtractValueNullable(restored.Source, bars);

        Assert.NotNull(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Normalize_TypesAnEnumFromItsNumber_AndLeavesTypedValuesAlone()
    {
        // MovingAverageCross-like shapes are not needed: any indicator whose parameter object has an enum property proves the rule.
        IndicatorType? withEnum = IndicatorFactory.Default.GetRegisteredTypes().Cast<IndicatorType?>().FirstOrDefault(t =>
            IndicatorFactory.Default.Create(t!.Value)?.GetDefaultSettings()?.ParameterObject?.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.PropertyType.IsEnum && p.CanWrite) == true);
        Assert.NotNull(withEnum);

        CoreIndicatorParameterBase parameterObject = IndicatorFactory.Default.Create(withEnum!.Value)!.GetDefaultSettings()!.ParameterObject!;
        PropertyInfo enumProperty = parameterObject.GetType().GetProperties().First(p => p.PropertyType.IsEnum && p.CanWrite);
        ScatterMetricDefinition restored = RoundTrip(Build(withEnum.Value, ParametersOf(parameterObject)));

        object value = restored.Source.Parameters[enumProperty.Name];
        Assert.IsType(enumProperty.PropertyType, value);

        object[] typed = { 14, 2.5, true, "x" };
        var untouched = new ScatterMetricDefinition
        {
            Source = { Parameters = { ["a"] = typed[0], ["b"] = typed[1], ["c"] = typed[2], ["d"] = typed[3] } },
        };
        ScatterMetricParameterNormalizer.Normalize(untouched, IndicatorFactory.Default);
        Assert.Equal(typed, new[] { untouched.Source.Parameters["a"], untouched.Source.Parameters["b"], untouched.Source.Parameters["c"], untouched.Source.Parameters["d"] });
    }
}
