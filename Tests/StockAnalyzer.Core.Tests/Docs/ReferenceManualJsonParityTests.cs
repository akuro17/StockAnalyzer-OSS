using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Docs;

/// <summary>
/// Machine verification ensuring all JSON reference examples in docs/OSS/*.md are syntactically valid
/// and deserialize cleanly into their respective domain classes using production serializers.
/// </summary>
public sealed class ReferenceManualJsonParityTests
{
    private static readonly Lazy<string> RepoRoot = new(TestRepositoryRoot.Find);

    private static string GetDocPath(string filename)
    {
        string path = Path.Combine(RepoRoot.Value, "docs", "OSS", filename);
        if (!File.Exists(path))
        {
            path = Path.Combine(RepoRoot.Value, "docs", "ReferenceManual", filename);
        }
        return path;
    }

    private static string ReadDoc(string filename)
    {
        return File.ReadAllText(GetDocPath(filename));
    }

    private static List<string> ExtractJsonBlocks(string markdown)
    {
        var blocks = new List<string>();
        var regex = new Regex(@"```json\r?\n([\s\S]*?)\r?\n```", RegexOptions.Multiline);
        foreach (Match match in regex.Matches(markdown))
        {
            blocks.Add(match.Groups[1].Value.Trim());
        }
        return blocks;
    }

    [Fact]
    public void Doc01_Appsettings_JsonExample_IsValidJson()
    {
        var doc = ReadDoc("01_appsettings_configuration.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.NotEmpty(blocks);

        // Verify the complete reference example parses cleanly
        string json = blocks.Last();
        using var parsed = JsonDocument.Parse(json);
        Assert.NotNull(parsed.RootElement);
        Assert.True(parsed.RootElement.TryGetProperty("PredictionMonitoring", out var pm));
        Assert.True(pm.TryGetProperty("AttestedSources", out var sources));
        Assert.Equal(JsonValueKind.Array, sources.ValueKind);
    }

    [Fact]
    public void Doc02_Workspace_JsonExample_DeserializesViaWorkspaceSerializationService()
    {
        var doc = ReadDoc("02_workspace_configuration.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.NotEmpty(blocks);

        // The final block is the complete default_workspace.json example
        string json = blocks.Last();
        var service = new WorkspaceSerializationService();
        var workspace = service.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.NotNull(workspace);
        Assert.Equal(2, workspace.SchemaVersion);
        Assert.Equal("Default Profile", workspace.Name);
        Assert.Equal(2, workspace.Indicators.Count);
        Assert.Single(workspace.WatchlistProfiles);
        Assert.Equal(2, workspace.WatchlistProfiles[0].Items.Count);
        Assert.Equal("AAPL", workspace.WatchlistProfiles[0].Items[0].Ticker);
    }

    [Fact]
    public void Doc03_ChartAndVisual_JsonExamples_DeserializeCleanly()
    {
        var doc = ReadDoc("03_chart_and_visual_settings.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.True(blocks.Count >= 2);

        // 1. user_chart_settings.json (camelCase)
        string chartSettingsJson = blocks[0];
        var chartSettings = JsonSerializer.Deserialize<GlobalChartSettings>(chartSettingsJson, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        });
        Assert.NotNull(chartSettings);
        Assert.Equal(1.5, chartSettings.DefaultStrokeThickness);

        // 2. user_theme.json ({ Mode, Colors })
        string themeJson = blocks[1];
        var themeDoc = JsonDocument.Parse(themeJson);
        Assert.True(themeDoc.RootElement.TryGetProperty("Colors", out var colorsProp));
        var themeColors = JsonSerializer.Deserialize<ThemeColors>(colorsProp.GetRawText(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(themeColors);
        Assert.True(themeColors.IsDark);
    }

    [Fact]
    public void Doc04_TickersAndScatter_JsonExamples_DeserializeCleanly()
    {
        var doc = ReadDoc("04_ticker_and_screener_settings.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.True(blocks.Count >= 2);

        // 1. user_tickers_settings.json
        string tickersJson = blocks[0];
        using var parsedTickers = JsonDocument.Parse(tickersJson);
        Assert.True(parsedTickers.RootElement.TryGetProperty("ColumnSelectionScope", out var scope));
        Assert.Equal(0, scope.GetInt32());

        // 2. scatter_metrics.json
        string scatterJson = blocks[1];
        var scatterFile = JsonSerializer.Deserialize<ScatterMetricFile>(scatterJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(scatterFile);
        Assert.Equal(1, scatterFile.FormatVersion);
        Assert.Equal(2, scatterFile.Metrics.Count);
        Assert.Equal(34, (int)scatterFile.Metrics[0].Source.IndicatorType);
    }

    [Fact]
    public void Doc05_Ensemble_JsonExample_DeserializesAndValidates()
    {
        var doc = ReadDoc("05_ai_prediction_and_ml_settings.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.True(blocks.Count >= 3);

        // Ensemble spec is block index 2
        string ensembleJson = blocks[2];
        var spec = JsonSerializer.Deserialize<EnsembleSpec>(ensembleJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(spec);
        Assert.Equal(2, spec.Members.Count);
        spec.Validate(); // Should pass validation without exception
    }

    [Fact]
    public void Doc06_BacktestConfiguration_JsonExamples_DeserializeCleanly()
    {
        var doc = ReadDoc("06_backtest_configuration.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.True(blocks.Count >= 2);

        var jsonOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            PropertyNameCaseInsensitive = true
        };

        // Example A: SchemaVersion 1 (Flat conditions)
        string schema1Json = blocks[0];
        var config1 = JsonSerializer.Deserialize<BacktestConfigurationDto>(schema1Json, jsonOptions);
        Assert.NotNull(config1);
        Assert.Equal(1, config1.SchemaVersion);
        Assert.Single(config1.ConditionEntries);
        Assert.Null(config1.ConditionTree);

        // Example B: SchemaVersion 2 (Condition tree)
        string schema2Json = blocks[1];
        var config2 = JsonSerializer.Deserialize<BacktestConfigurationDto>(schema2Json, jsonOptions);
        Assert.NotNull(config2);
        Assert.Equal(2, config2.SchemaVersion);
        Assert.Empty(config2.ConditionEntries);
        Assert.NotNull(config2.ConditionTree);
        Assert.Single(config2.ConditionTree.EntryLong.Children);
    }

    [Fact]
    public void Doc07_IndicatorDefaults_JsonExample_DeserializesCleanly()
    {
        var doc = ReadDoc("07_indicator_defaults_and_drivers.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.NotEmpty(blocks);

        var jsonOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            PropertyNameCaseInsensitive = true
        };

        // user_indicator_defaults.json (JSON array of CoreIndicatorSettings)
        string defaultsJson = blocks[0];
        var list = JsonSerializer.Deserialize<List<CoreIndicatorSettings>>(defaultsJson, jsonOptions);
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Equal(IndicatorType.SMA, list[0].TypeEnum);
        Assert.Equal(IndicatorType.RSI, list[1].TypeEnum);
    }

    [Fact]
    public void Doc08_TemplatesReference_JsonExamples_DeserializeCleanly()
    {
        var doc = ReadDoc("08_templates_reference.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.True(blocks.Count >= 7);

        var jsonOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            PropertyNameCaseInsensitive = true,
            IncludeFields = true
        };

        // 1. IndicatorTemplate
        var indTemplate = JsonSerializer.Deserialize<IndicatorTemplate>(blocks[0], jsonOptions);
        Assert.NotNull(indTemplate);
        Assert.Equal(2, indTemplate.Indicators.Count);

        // 2. ColumnTemplate
        var colTemplate = JsonSerializer.Deserialize<ColumnTemplate>(blocks[1], jsonOptions);
        Assert.NotNull(colTemplate);
        Assert.Equal(5, colTemplate.ColumnNames.Count);

        // 3. FilterTemplate
        var filterTemplate = JsonSerializer.Deserialize<FilterTemplate>(blocks[2], jsonOptions);
        Assert.NotNull(filterTemplate);
        Assert.Single(filterTemplate.RootSettings.Rules);

        // 4. ScreenerIndicatorTemplate
        var screenerTemplate = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(blocks[3], jsonOptions);
        Assert.NotNull(screenerTemplate);
        Assert.Single(screenerTemplate.Entries);

        // 5. ThemeTemplate
        var themeTemplate = JsonSerializer.Deserialize<ThemeTemplate>(blocks[4], jsonOptions);
        Assert.NotNull(themeTemplate);
        Assert.True(themeTemplate.Colors.IsDark);

        // 6. FeatureSpecTemplate
        var featureTemplate = JsonSerializer.Deserialize<FeatureSpecTemplate>(blocks[5], jsonOptions);
        Assert.NotNull(featureTemplate);
        Assert.Equal(2, featureTemplate.Spec.Channels.Count);

        // 7. BacktestConditionTemplate
        var btTemplate = JsonSerializer.Deserialize<BacktestConditionTemplate>(blocks[6], jsonOptions);
        Assert.NotNull(btTemplate);
        Assert.NotNull(btTemplate.ConditionTree.EntryLong);
    }

    [Fact]
    public void Doc09_IndicatorCatalog_MinimalExample_DeserializesCleanly()
    {
        var doc = ReadDoc("09_indicator_catalog.md");
        var blocks = ExtractJsonBlocks(doc);
        Assert.NotEmpty(blocks);

        var jsonOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            PropertyNameCaseInsensitive = true
        };

        var setting = JsonSerializer.Deserialize<CoreIndicatorSettings>(blocks[0], jsonOptions);
        Assert.NotNull(setting);
        Assert.Equal(IndicatorType.MACD, setting.TypeEnum);
        Assert.IsType<StockAnalyzer.Core.Models.Parameters.CoreMacdParameter>(setting.ParameterObject);
        var macdParam = (StockAnalyzer.Core.Models.Parameters.CoreMacdParameter)setting.ParameterObject;
        Assert.Equal(12, macdParam.ShortPeriod);
        Assert.Equal(26, macdParam.LongPeriod);
        Assert.Equal(9, macdParam.SignalPeriod);
    }

    [Fact]
    public void Doc09_IndicatorCatalog_AllTableOrdinalsMatchEnum()
    {
        string docPath = GetDocPath("09_indicator_catalog.md");
        var doc = File.ReadAllText(docPath);
        var tableRowRegex = new System.Text.RegularExpressions.Regex(@"\|\s*`?([A-Za-z0-9_]+)`?\s*\|\s*`?([0-9]+)`?\s*\|\s*`?([A-Za-z0-9_]+)`?\s*\|");

        var mismatches = new List<string>();
        int testedCount = 0;
        foreach (System.Text.RegularExpressions.Match match in tableRowRegex.Matches(doc))
        {
            string typeName = match.Groups[1].Value;
            if (typeName == "IndicatorType") continue;
            int statedOrdinal = int.Parse(match.Groups[2].Value);

            if (Enum.TryParse<IndicatorType>(typeName, out var parsedEnum))
            {
                int actualOrdinal = (int)parsedEnum;
                if (statedOrdinal != actualOrdinal)
                {
                    mismatches.Add($"{typeName}: stated {statedOrdinal} -> actual {actualOrdinal}");
                }
                testedCount++;
            }
        }

        Assert.Empty(mismatches);
        Assert.True(testedCount > 10, $"Expected to test multiple indicators, tested {testedCount}");
    }
}
