using System.Collections.Immutable;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Run V2-12 tests: MCP migration hints (<c>suggestedIndicator</c> on <c>sa_get_capabilities</c> /
/// <c>stockanalyzer://catalog/screener</c> and <c>legacyCriteria</c> on <c>sa_run_screener</c>) while keeping
/// saved Criteria templates evaluating identically and <c>sortBy.criteria</c> rejected.
/// </summary>
public sealed class LegacyCriteriaHintTests
{
    [Fact]
    public async Task CapabilitiesAndScreenerResource_IncludeSuggestedIndicatorOnEveryCriteria_AndOmitOnColumnsAndPrices()
    {
        using var world = ScreenerTestWorld.ZigZag();

        string json = await world.CallAsync("sa_get_capabilities", """{"indicatorTypeFilter":["RSI"],"includeScreenerCatalog":true}""");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement screener = doc.RootElement.GetProperty("screener");

        JsonElement[] criteria = screener.GetProperty("criteria").EnumerateArray().ToArray();
        Assert.True(criteria.Length >= 56, $"Expected at least 56 legacy Criteria entries, found {criteria.Length}.");

        foreach (JsonElement entry in criteria)
        {
            string name = entry.GetProperty("name").GetString()!;
            Assert.True(entry.TryGetProperty("suggestedIndicator", out JsonElement suggestedElement), $"Criteria '{name}' is missing 'suggestedIndicator'.");
            string suggested = suggestedElement.GetString()!;
            Assert.True(Enum.TryParse<IndicatorType>(suggested, ignoreCase: false, out IndicatorType parsed), $"Invalid IndicatorType '{suggested}' for Criteria '{name}'.");
            Assert.True(LegacyCriteriaNameMap.TryResolve(name, out IndicatorType expected));
            Assert.Equal(expected, parsed);
        }

        Assert.Equal("CandleBullishEngulfing", FindSuggested(criteria, "Bullish Engulfing"));
        Assert.Equal("PatternHeadAndShoulders", FindSuggested(criteria, "Head and Shoulders"));
        Assert.Equal("MarketStructureBos", FindSuggested(criteria, "Break of Structure (BOS)"));
        Assert.Equal("HarmonicGartley", FindSuggested(criteria, "Gartley Pattern"));
        Assert.Equal("GranvilleBuySignal", FindSuggested(criteria, "Granville's Law Buy Signal"));
        Assert.Equal("ElliottWaveImpulse", FindSuggested(criteria, "Elliott Wave Impulse 1-5"));

        Assert.All(screener.GetProperty("columns").EnumerateArray(), c => Assert.False(c.TryGetProperty("suggestedIndicator", out _)));
        Assert.All(screener.GetProperty("prices").EnumerateArray(), p => Assert.False(p.TryGetProperty("suggestedIndicator", out _)));
    }

    [Fact]
    public async Task RunScreener_WithCriteriaConditionTree_EmitsLegacyCriteriaInFirstUseOrder_AndDeduplicates()
    {
        using var world = ScreenerTestWorld.ZigZag();

        const string Tree = """
        {
          "conditionTree": {
            "operator": "Or",
            "children": [
              {"left": {"criteria": "bullish engulfing"}, "comparison": "Equal", "right": 0},
              {"left": {"criteria": "Head and Shoulders"}, "comparison": "Equal", "right": 0},
              {"left": {"criteria": "Bullish Engulfing"}, "comparison": "Equal", "right": {"criteria": "Hammer / Bullish Umbrella"}}
            ]
          }
        }
        """;

        string json = await world.CallAsync("sa_run_screener", Tree);
        using JsonDocument doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("legacyCriteria", out JsonElement legacyArray));
        JsonElement[] entries = legacyArray.EnumerateArray().ToArray();
        Assert.Equal(3, entries.Length);

        Assert.Equal("Bullish Engulfing", entries[0].GetProperty("name").GetString());
        Assert.Equal("CandleBullishEngulfing", entries[0].GetProperty("suggestedIndicator").GetString());

        Assert.Equal("Head and Shoulders", entries[1].GetProperty("name").GetString());
        Assert.Equal("PatternHeadAndShoulders", entries[1].GetProperty("suggestedIndicator").GetString());

        Assert.Equal("Hammer / Bullish Umbrella", entries[2].GetProperty("name").GetString());
        Assert.Equal("CandleHammer", entries[2].GetProperty("suggestedIndicator").GetString());
    }

    [Fact]
    public async Task RunScreener_WithSavedCriteriaTemplate_EmitsLegacyCriteria_AndPreservesEvaluation()
    {
        using var world = ScreenerTestWorld.ZigZag();

        var enabledCriteria = new ScreenerIndicatorEntry
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            IsEnabled = true,
            Operator = ComparisonOperator.Equal,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Criteria,
                OutputName = "Bullish Engulfing",
                CustomDisplayName = "Bullish Engulfing",
                TimeFrame = TimeFrame.D1,
            },
        };
        var disabledCriteria = new ScreenerIndicatorEntry
        {
            CategoryType = ScreenerItemCategoryType.Criteria,
            IsEnabled = false,
            Operator = ComparisonOperator.Equal,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 1m,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Criteria,
                OutputName = "Evening Star",
                CustomDisplayName = "Evening Star",
                TimeFrame = TimeFrame.D1,
            },
        };

        Guid templateId = world.SaveTemplate(enabledCriteria, disabledCriteria);
        string mcpJson = await world.CallAsync("sa_run_screener", $$"""{"templateId":"{{templateId}}"}""");
        using JsonDocument doc = JsonDocument.Parse(mcpJson);

        // Frozen Criteria evaluation still returns identical matches as Core screener condition evaluation.
        var frozenCondition = new ScreenerConditionTreeCondition(
            new ScreenerConditionGroup(
                LogicalOperator.And,
                ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(enabledCriteria))));
        IReadOnlyList<string> expectedMatched = await world.Gateway.RunScreenerAsync(
            new[] { "7203-T", "AAPL", "MSFT" },
            frozenCondition,
            TimeFrame.D1);
        string[] actualMatched = doc.RootElement.GetProperty("matchedSymbols").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(expectedMatched.OrderBy(s => s, StringComparer.Ordinal), actualMatched);

        // legacyCriteria emits only enabled Criteria entries and does not synthesize warnings.
        JsonElement[] legacy = doc.RootElement.GetProperty("legacyCriteria").EnumerateArray().ToArray();
        JsonElement item = Assert.Single(legacy);
        Assert.Equal("Bullish Engulfing", item.GetProperty("name").GetString());
        Assert.Equal("CandleBullishEngulfing", item.GetProperty("suggestedIndicator").GetString());
        Assert.False(doc.RootElement.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task RunScreener_WithoutCriteria_OmitsLegacyCriteria_AndSortByCriteriaStillRejects()
    {
        using var world = ScreenerTestWorld.ZigZag();

        string json = await world.CallAsync(
            "sa_run_screener",
            """{"conditionTree":{"left":{"indicator":"CandleBullishEngulfing"},"comparison":"Equal","right":0}}""");
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("legacyCriteria", out _));

        using JsonDocument invalidSort = await world.CallRawAsync(
            "sa_run_screener",
            """{"conditionTree":{"left":{"price":"Close"},"comparison":"GreaterThan","right":0},"sortBy":{"criteria":"Bullish Engulfing"}}""");
        Assert.True(McpTestSupport.IsError(invalidSort));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(invalidSort));
        Assert.Equal("sortBy.criteria", McpTestSupport.Structured(invalidSort).GetProperty("param").GetString());
        string mappedHint = McpTestSupport.Structured(invalidSort).GetProperty("hint").GetString()!;
        Assert.Contains("CandleBullishEngulfing", mappedHint);
        Assert.Contains("conditionTree", mappedHint);
        Assert.Contains("Direction", mappedHint);
        Assert.Contains("RuleCode", mappedHint);

        using JsonDocument unknownSort = await world.CallRawAsync(
            "sa_run_screener",
            """{"conditionTree":{"left":{"price":"Close"},"comparison":"GreaterThan","right":0},"sortBy":{"criteria":"Unknown Pattern Name"}}""");
        Assert.True(McpTestSupport.IsError(unknownSort));
        Assert.Equal(ToolErrorCodes.InvalidArgument, McpTestSupport.ErrorCodeOf(unknownSort));
        Assert.Equal("sortBy.criteria", McpTestSupport.Structured(unknownSort).GetProperty("param").GetString());
        Assert.Equal("Sort by an indicator, a price or a numeric column.", McpTestSupport.Structured(unknownSort).GetProperty("hint").GetString());
    }

    private static string FindSuggested(JsonElement[] criteria, string name)
        => criteria.Single(c => c.GetProperty("name").GetString() == name).GetProperty("suggestedIndicator").GetString()!;
}
