using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>sa_get_capabilities and sa_get_settings: catalogue content against the registry, filters, errors, and the settings allow-list.</summary>
public sealed class CatalogToolsTests
{
    private static readonly IndicatorFactory Factory = new();
    private static readonly IndicatorCatalog Catalog = new(Factory, new ScreenerCatalogProvider());
    private static readonly ScreenerCatalog ScreenerCatalogForTests = new(new ScreenerCatalogProvider(), Factory);

    private static async Task<JsonDocument> CallCapabilitiesAsync(string argumentsJson = "{}", McpLimits? limits = null)
    {
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { new GetCapabilitiesTool(Catalog, ScreenerCatalogForTests) }, limits);
        return (await McpTestSupport.CallAsync(dispatcher, "sa_get_capabilities", argumentsJson))!;
    }

    private static JsonElement Indicator(JsonDocument response, string type)
        => McpTestSupport.Structured(response).GetProperty("indicators").EnumerateArray().Single(i => i.GetProperty("type").GetString() == type);

    private static JsonElement Parameter(JsonElement indicator, string name)
        => indicator.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);

    // ---- sa_get_capabilities --------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutArgumentsTheListIsCompactAndOrderedByTypeName()
    {
        using var response = await CallCapabilitiesAsync();

        JsonElement result = McpTestSupport.Structured(response);
        JsonElement[] indicators = result.GetProperty("indicators").EnumerateArray().ToArray();
        Assert.Equal(indicators.Length, result.GetProperty("indicatorCount").GetInt32());
        Assert.Equal(Enum.GetValues<IndicatorType>().Distinct().Count(), indicators.Length);
        string[] names = indicators.Select(i => i.GetProperty("type").GetString()!).ToArray();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.All(indicators, i =>
        {
            Assert.False(i.TryGetProperty("parameters", out _));
            Assert.False(i.TryGetProperty("outputSeriesNames", out _));
        });
        Assert.Equal("bars", result.GetProperty("periodUnit").GetString());
        Assert.Equal(new[] { "D1", "W1", "MN1" }, result.GetProperty("supportedTimeFrames").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal("Oscillator", Indicator(response, "RSI").GetProperty("category").GetString());
    }

    [Fact]
    public async Task TheRegisteredFlagFollowsTheIndicatorRegistryExactly()
    {
        using var response = await CallCapabilitiesAsync();

        var registered = Factory.GetRegisteredTypes().Select(t => t.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (JsonElement indicator in McpTestSupport.Structured(response).GetProperty("indicators").EnumerateArray())
        {
            string type = indicator.GetProperty("type").GetString()!;
            Assert.Equal(registered.Contains(type), indicator.GetProperty("isRegistered").GetBoolean());
            if (!registered.Contains(type))
            {
                Assert.False(indicator.TryGetProperty("category", out _));
            }
        }

        Assert.NotEmpty(registered);
    }

    [Fact]
    public async Task AFilterIsCaseInsensitiveAndTurnsTheDetailsOn()
    {
        using var response = await CallCapabilitiesAsync("""{"indicatorTypeFilter":["rsi","MACD"]}""");

        JsonElement result = McpTestSupport.Structured(response);
        Assert.Equal(2, result.GetProperty("indicatorCount").GetInt32());

        JsonElement rsi = Indicator(response, "RSI");
        JsonElement period = Parameter(rsi, "Period");
        Assert.Equal("integer", period.GetProperty("type").GetString());
        Assert.Equal(14, period.GetProperty("default").GetInt32());
        Assert.Equal(1, period.GetProperty("min").GetInt32());
        Assert.Equal(10000, period.GetProperty("max").GetInt32());
        Assert.Equal(1000, period.GetProperty("guiMax").GetInt32());
        Assert.DoesNotContain("Simple Moving Average", period.GetProperty("description").GetString()); // the shared Period text must stay generic
        Assert.Equal(new[] { "RSI" }, rsi.GetProperty("outputSeriesNames").EnumerateArray().Select(n => n.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, rsi.GetProperty("unit").ValueKind);
        Assert.False(rsi.GetProperty("isCrossTicker").GetBoolean());
        Assert.Equal("Allowed", rsi.GetProperty("backtestEligibility").GetString());

        JsonElement macd = Indicator(response, "MACD");
        Assert.Equal(new[] { "MacdLine", "Signal", "Histogram" }, macd.GetProperty("outputSeriesNames").EnumerateArray().Select(n => n.GetString()).ToArray());
        Assert.Equal(new[] { 12, 26, 9 }, new[] { "ShortPeriod", "LongPeriod", "SignalPeriod" }.Select(n => Parameter(macd, n).GetProperty("default").GetInt32()).ToArray());
        JsonElement shortPeriod = Parameter(macd, "ShortPeriod");
        Assert.Equal(10000, shortPeriod.GetProperty("max").GetInt32());
        Assert.Equal(1000, shortPeriod.GetProperty("guiMax").GetInt32()); // the chart input is narrower than the hard range

        Assert.Equal(new[] { "MACD", "RSI" }, result.GetProperty("unitUndefined").EnumerateArray().Select(n => n.GetString()).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task IncludeDetailsFalseKeepsAFilteredListCompact()
    {
        using var response = await CallCapabilitiesAsync("""{"indicatorTypeFilter":["RSI"],"includeDetails":false}""");

        Assert.False(Indicator(response, "RSI").TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task ACategoryKeepsOnlyThatCategory()
    {
        using var response = await CallCapabilitiesAsync("""{"category":"Oscillator"}""");

        JsonElement[] indicators = McpTestSupport.Structured(response).GetProperty("indicators").EnumerateArray().ToArray();
        Assert.All(indicators, i => Assert.Equal("Oscillator", i.GetProperty("category").GetString()));
        Assert.Contains(indicators, i => i.GetProperty("type").GetString() == "RSI");
        Assert.DoesNotContain(indicators, i => i.GetProperty("type").GetString() == "SMA");
    }

    [Theory]
    [InlineData("""{"indicatorTypeFilter":["RSI","NotAnIndicator"]}""", "indicatorTypeFilter[1]")]
    [InlineData("""{"indicatorTypeFilter":["5"]}""", "indicatorTypeFilter[0]")]
    public async Task AnUnknownTypeNameIsUnknownIndicatorNamingTheEntry(string argumentsJson, string parameter)
    {
        using var response = await CallCapabilitiesAsync(argumentsJson);

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal(ToolErrorCodes.UnknownIndicator, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData("""{"category":"Nonsense"}""", "category")]
    [InlineData("""{"indicatorTypeFilter":[]}""", "indicatorTypeFilter")]
    [InlineData("""{"surprise":1}""", "surprise")]
    public async Task ABadShapeIsInvalidArgument(string argumentsJson, string parameter)
    {
        using var response = await CallCapabilitiesAsync(argumentsJson);

        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(response));
        Assert.Equal(parameter, McpTestSupport.Structured(response).GetProperty("param").GetString());
    }

    [Fact]
    public async Task NonCausalAndUnsupportedIndicatorsAreFlaggedFromTheBacktestRules()
    {
        // Several types the backtest rule names are not registered; the check needs one that is.
        IndicatorType unsupported = BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes.First(Factory.IsRegistered);
        using var response = await CallCapabilitiesAsync($$"""{"indicatorTypeFilter":["Ichimoku","ZigZag","{{unsupported}}"]}""");

        JsonElement ichimoku = Indicator(response, "Ichimoku");
        Assert.Equal("Allowed", ichimoku.GetProperty("backtestEligibility").GetString());
        Assert.Equal(new[] { "ChikouSpan" }, ichimoku.GetProperty("nonCausalSeries").EnumerateArray().Select(n => n.GetString()).ToArray());

        JsonElement zigZag = Indicator(response, "ZigZag");
        Assert.Equal("NonCausal", zigZag.GetProperty("backtestEligibility").GetString());
        Assert.Equal(
            zigZag.GetProperty("outputSeriesNames").EnumerateArray().Select(n => n.GetString()).ToArray(),
            zigZag.GetProperty("nonCausalSeries").EnumerateArray().Select(n => n.GetString()).ToArray());
        Assert.NotEmpty(zigZag.GetProperty("outputSeriesNames").EnumerateArray());

        Assert.Equal("SynchronousCalculationUnsupported", Indicator(response, unsupported.ToString()).GetProperty("backtestEligibility").GetString());
    }

    [Fact]
    public void EveryRegisteredIndicatorHasSeriesAndWellFormedParameters()
    {
        var knownTypes = new[]
        {
            IndicatorParameterReader.IntegerType, IndicatorParameterReader.NumberType, IndicatorParameterReader.BooleanType,
            IndicatorParameterReader.StringType, IndicatorParameterReader.EnumType, IndicatorParameterReader.ColorType,
        };

        foreach (IndicatorCatalogEntry entry in Catalog.Entries.Where(e => e.IsRegistered))
        {
            Assert.NotEmpty(entry.OutputSeriesNames);
            Assert.NotNull(entry.Category);
            Assert.Null(entry.Unit);
            Assert.Equal(entry.Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count(), entry.Parameters.Count);
            foreach (IndicatorParameterDescriptor parameter in entry.Parameters)
            {
                string where = $"{entry.Type}.{parameter.Name}";
                Assert.True(knownTypes.Contains(parameter.Type), $"{where} has the unhandled type {parameter.Type}.");
                if (parameter.Type is IndicatorParameterReader.IntegerType or IndicatorParameterReader.NumberType)
                {
                    Assert.True(parameter.Min is not null && parameter.Max is not null, $"{where} has no [CoreParameterRange].");
                    Assert.True(parameter.Min <= parameter.Max, $"{where} has an inverted range.");
                }

                if (parameter.Type == IndicatorParameterReader.EnumType)
                {
                    Assert.NotEmpty(parameter.AllowedValues!);
                    Assert.Contains(parameter.Default as string, parameter.AllowedValues!);
                }
            }
        }
    }

    [Fact]
    public void TheParameterReaderUsesTheSelectionRuleOfTheChartForm()
    {
        IReadOnlyList<IndicatorParameterDescriptor> parameters = IndicatorParameterReader.Read(new CoreMacdParameter());

        Assert.Equal(new[] { "ShortPeriod", "LongPeriod", "SignalPeriod" }, parameters.Select(p => p.Name).ToArray());
        Assert.All(parameters, p => Assert.NotNull(p.Description));
    }

    [Fact]
    public async Task AParameterlessIndicatorListsNoParameters()
    {
        using var response = await CallCapabilitiesAsync("""{"indicatorTypeFilter":["OBV"]}""");

        JsonElement obv = Indicator(response, "OBV");
        Assert.Equal(0, obv.GetProperty("parameters").GetArrayLength());
    }

    [Fact]
    public async Task TheFullDetailedCatalogueFitsTheResponseLimit()
    {
        using var response = await CallCapabilitiesAsync("""{"includeDetails":true}""");

        Assert.False(McpTestSupport.IsError(response));
        int bytes = JsonSerializer.SerializeToUtf8Bytes(McpTestSupport.Structured(response)).Length;
        Assert.True(bytes < new McpLimits().MaxResponseBytes, $"{bytes} bytes");
    }

    // ---- sa_get_settings ------------------------------------------------------------------------------------------

    private static IStockAnalyzerSettings CreateSettings(params (string Key, string Value)[] overrides)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides.ToDictionary(o => o.Key, o => (string?)o.Value))
            .Build();
        return new ServiceCollection()
            .AddSingleton(configuration)
            .AddStockAnalyzerSettingsOptions(configuration)
            .AddSingleton<IStockAnalyzerSettings, StockAnalyzerSettings>()
            .BuildServiceProvider()
            .GetRequiredService<IStockAnalyzerSettings>();
    }

    private static async Task<JsonDocument> CallSettingsAsync(IStockAnalyzerSettings settings, string argumentsJson = "{}")
    {
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { new GetSettingsTool(settings) });
        return (await McpTestSupport.CallAsync(dispatcher, "sa_get_settings", argumentsJson))!;
    }

    [Fact]
    public void EverySettingIsEitherAllowedOrIntentionallyOmittedNeverBoth()
    {
        string[] all = McpSettingsExposure.InterfaceProperties.Select(p => p.Name).ToArray();
        string[] allowed = McpSettingsExposure.Allowed.Select(e => e.Property).ToArray();
        string[] omitted = McpSettingsExposure.IntentionallyOmitted.ToArray();

        Assert.Empty(allowed.Intersect(omitted, StringComparer.Ordinal));
        Assert.Equal(all.Order(StringComparer.Ordinal), allowed.Concat(omitted).Order(StringComparer.Ordinal));
        Assert.Equal(allowed.Length, allowed.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NoAllowedSettingIsAStringWhoseNameSuggestsALocationOrACredential()
    {
        foreach (McpSettingsExposure.Entry entry in McpSettingsExposure.Allowed)
        {
            var property = typeof(IStockAnalyzerSettings).GetProperty(entry.Property)!;
            Assert.False(
                property.PropertyType == typeof(string) && McpSettingsExposure.ForbiddenNameRule.IsMatch(entry.Property),
                $"{entry.Property} is a string that looks like a path or a secret.");
        }

        // The rule really catches the names it exists for.
        string[] flagged = McpSettingsExposure.InterfaceProperties
            .Where(p => p.PropertyType == typeof(string) && McpSettingsExposure.ForbiddenNameRule.IsMatch(p.Name))
            .Select(p => p.Name)
            .ToArray();
        Assert.Contains(nameof(IStockAnalyzerSettings.PythonPath), flagged);
        Assert.Contains(nameof(IStockAnalyzerSettings.PredictionModelPath), flagged);
    }

    [Fact]
    public async Task TheReturnedValuesAreTheLiveSettingValues()
    {
        IStockAnalyzerSettings settings = CreateSettings();
        using var response = await CallSettingsAsync(settings);

        JsonElement sections = McpTestSupport.Structured(response).GetProperty("settings");
        Assert.Equal(McpSettingsExposure.Sections, sections.EnumerateObject().Select(s => s.Name).ToArray());
        Assert.Equal(settings.DefaultSymbol, sections.GetProperty("chart").GetProperty("defaultSymbol").GetString());
        Assert.Equal(settings.ScreenerMaxParallelism, sections.GetProperty("screener").GetProperty("maxParallelism").GetInt32());
        Assert.Equal(settings.ScreenerMaxConditionOffset, sections.GetProperty("screener").GetProperty("maxConditionOffset").GetInt32());
        Assert.Equal(settings.BacktestMaxConditionTreeDepth, sections.GetProperty("backtest").GetProperty("maxConditionTreeDepth").GetInt32());
        Assert.Equal(settings.BacktestMaxConditionTreeNodes, sections.GetProperty("backtest").GetProperty("maxConditionTreeNodes").GetInt32());
        Assert.Equal(settings.ZigzagThresholdPercent, sections.GetProperty("marketStructure").GetProperty("zigzagThresholdPercent").GetDecimal());
        Assert.Equal(settings.PredictionWindowSize, sections.GetProperty("prediction").GetProperty("windowSize").GetInt32());
        Assert.Equal(settings.PredictionFeatureMode.ToString(), sections.GetProperty("prediction").GetProperty("featureMode").GetString());
    }

    [Fact]
    public async Task AConfiguredValueReachesTheClient()
    {
        IStockAnalyzerSettings settings = CreateSettings(("Chart:DefaultSymbol", "7203.T"));
        using var response = await CallSettingsAsync(settings, """{"section":"chart"}""");

        Assert.Equal("7203.T", McpTestSupport.Structured(response).GetProperty("settings").GetProperty("chart").GetProperty("defaultSymbol").GetString());
    }

    [Fact]
    public async Task ASectionArgumentReturnsOnlyThatSectionAndAnUnknownOneIsRejected()
    {
        IStockAnalyzerSettings settings = CreateSettings();
        using var only = await CallSettingsAsync(settings, """{"section":"backtest"}""");
        using var unknown = await CallSettingsAsync(settings, """{"section":"python"}""");

        Assert.Equal(new[] { "backtest" }, McpTestSupport.Structured(only).GetProperty("settings").EnumerateObject().Select(s => s.Name).ToArray());
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(unknown));
        Assert.Equal("section", McpTestSupport.Structured(unknown).GetProperty("param").GetString());
    }

    [Fact]
    public async Task NothingThatLooksLikeALocationOrACredentialIsReturned()
    {
        IStockAnalyzerSettings settings = CreateSettings();
        using var response = await CallSettingsAsync(settings);

        string text = McpTestSupport.Structured(response).GetRawText();
        foreach (string omitted in McpSettingsExposure.IntentionallyOmitted)
        {
            Assert.DoesNotContain($"\"{TickerMetadataJson.JsonName(omitted)}\"", text);
        }

        Assert.DoesNotContain(":\\", text);
        Assert.DoesNotContain(AppContext.BaseDirectory, text, StringComparison.OrdinalIgnoreCase);
    }
}
