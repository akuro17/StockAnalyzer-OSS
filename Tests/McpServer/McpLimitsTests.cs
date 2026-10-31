using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.McpServer.Limits;

namespace StockAnalyzer.Tests.McpServer;

public class McpLimitsTests
{
    [Fact]
    public void TheStandardValuesAreInsideTheirBands()
    {
        McpLimitsContract.Validate(new McpLimits());
    }

    [Fact]
    public void TheStandardValuesAreThoseOfTheDecisionRegister()
    {
        var limits = new McpLimits();

        Assert.Equal(10000, limits.MaxCandlesPerRequest);
        Assert.Equal(200, limits.DefaultCandleCount);
        Assert.Equal(16, limits.MaxIndicatorsPerCall);
        Assert.Equal(16, limits.MaxDerivedSeriesPerCall);
        Assert.Equal(20, limits.DefaultSummaryTrendBars);
        Assert.Equal(32, limits.MaxLookbackOffsets);
        Assert.Equal(100, limits.MaxSymbolsPerMetadataCall);
        Assert.Equal(200, limits.MaxReturnedTrades);
        Assert.Equal(20, limits.MaxReturnedWarnings);
        Assert.Equal(5000, limits.MaxOutputTailBars);
        Assert.Equal(200, limits.DefaultOutputTailBars);
        Assert.Equal(60, limits.ToolTimeoutSeconds);
        Assert.Equal(180, limits.HeavyToolTimeoutSeconds);
        Assert.Equal(60, limits.QueueWaitSeconds);
        Assert.Equal(500, limits.MaxScreenerSymbols);
        Assert.Equal(200, limits.MaxReturnedMatches);
        Assert.Equal(4, limits.MaxSecondarySymbolsPerCall);
        Assert.Equal(32, limits.MaxInlineFilterRules);
        Assert.Equal(5000, limits.MaxEquityCurvePoints);
        Assert.Equal(5, limits.MaxDrawdownEpisodes);
        Assert.Equal(500, limits.MaxOptimizationCombinations);
        Assert.Equal(10, limits.MaxOptimizationTopK);
        Assert.Equal(4, limits.MaxOptimizationParameters);
        Assert.Equal(4, limits.OptimizationDegreeOfParallelism);
        Assert.Equal(8, limits.MaxWalkForwardWindows);
        Assert.Equal(1, limits.DefaultMinInSampleTrades);
        Assert.Equal(200, limits.MaxVolumeProfileBins);
        Assert.Equal(20, limits.MaxSupportResistanceLevels);
        Assert.Equal(50, limits.MaxCycleBins);
        Assert.Equal(100, limits.MaxReturnedPatterns);
        Assert.Equal(100, limits.MaxProjectionBars);
        Assert.Equal(50, limits.MaxSeasonalityYears);
        Assert.Equal(WatchlistConstants.MaxRelativePerformanceTargets, limits.MaxComparisonTargets);
        Assert.Equal(1, McpLimitsContract.MaxConcurrentToolCalls);
    }

    public static TheoryData<string, int> OutOfBand => new()
    {
        { nameof(McpLimits.MaxCandlesPerRequest), 0 },
        { nameof(McpLimits.MaxCandlesPerRequest), 50001 },
        { nameof(McpLimits.DefaultCandleCount), 0 },
        { nameof(McpLimits.DefaultOutputTailBars), 0 },
        { nameof(McpLimits.MaxIndicatorsPerCall), 65 },
        { nameof(McpLimits.MaxDerivedSeriesPerCall), 0 },
        { nameof(McpLimits.MaxDerivedSeriesPerCall), 65 },
        { nameof(McpLimits.DefaultSummaryTrendBars), 1 },
        { nameof(McpLimits.DefaultSummaryTrendBars), 50001 },
        { nameof(McpLimits.MaxLookbackOffsets), 0 },
        { nameof(McpLimits.MaxLookbackOffsets), 1001 },
        { nameof(McpLimits.MaxSymbolsPerMetadataCall), 501 },
        { nameof(McpLimits.MaxReturnedTrades), -1 },
        { nameof(McpLimits.MaxReturnedTrades), 1001 },
        { nameof(McpLimits.MaxReturnedWarnings), -1 },
        { nameof(McpLimits.MaxReturnedWarnings), 201 },
        { nameof(McpLimits.ToolTimeoutSeconds), 0 },
        { nameof(McpLimits.ToolTimeoutSeconds), 901 },
        { nameof(McpLimits.HeavyToolTimeoutSeconds), 1801 },
        { nameof(McpLimits.QueueWaitSeconds), -1 },
        { nameof(McpLimits.QueueWaitSeconds), 601 },
        { nameof(McpLimits.MaxResponseBytes), 65535 },
        { nameof(McpLimits.MaxResponseBytes), 8 * 1024 * 1024 + 1 },
        { nameof(McpLimits.MaxRequestBytes), 4095 },
        { nameof(McpLimits.MaxScreenerSymbols), 5001 },
        { nameof(McpLimits.MaxReturnedMatches), 1001 },
        { nameof(McpLimits.MaxSecondarySymbolsPerCall), 9 },
        { nameof(McpLimits.MaxComparisonTargets), 0 },
        { nameof(McpLimits.MaxComparisonTargets), WatchlistConstants.MaxRelativePerformanceTargets + 1 },
        { nameof(McpLimits.MaxTickersPerPage), 5001 },
        { nameof(McpLimits.DefaultTickerPageSize), 0 },
        { nameof(McpLimits.MaxInlineFilterRules), 0 },
        { nameof(McpLimits.MaxInlineFilterRules), 129 },
        { nameof(McpLimits.MaxEquityCurvePoints), 0 },
        { nameof(McpLimits.MaxEquityCurvePoints), 50001 },
        { nameof(McpLimits.MaxDrawdownEpisodes), 0 },
        { nameof(McpLimits.MaxDrawdownEpisodes), 51 },
        { nameof(McpLimits.MaxOptimizationCombinations), 0 },
        { nameof(McpLimits.MaxOptimizationCombinations), 20001 },
        { nameof(McpLimits.MaxOptimizationTopK), 0 },
        { nameof(McpLimits.MaxOptimizationTopK), 101 },
        { nameof(McpLimits.MaxOptimizationParameters), 0 },
        { nameof(McpLimits.MaxOptimizationParameters), 9 },
        { nameof(McpLimits.OptimizationDegreeOfParallelism), 0 },
        { nameof(McpLimits.OptimizationDegreeOfParallelism), 65 },
        { nameof(McpLimits.MaxWalkForwardWindows), 0 },
        { nameof(McpLimits.MaxWalkForwardWindows), 61 },
        { nameof(McpLimits.DefaultMinInSampleTrades), -1 },
        { nameof(McpLimits.DefaultMinInSampleTrades), 1001 },
        { nameof(McpLimits.MaxVolumeProfileBins), 1 },
        { nameof(McpLimits.MaxVolumeProfileBins), 501 },
        { nameof(McpLimits.MaxSupportResistanceLevels), 0 },
        { nameof(McpLimits.MaxSupportResistanceLevels), 101 },
        { nameof(McpLimits.MaxCycleBins), 0 },
        { nameof(McpLimits.MaxCycleBins), 501 },
        { nameof(McpLimits.MaxReturnedPatterns), 0 },
        { nameof(McpLimits.MaxReturnedPatterns), 1001 },
        { nameof(McpLimits.MaxProjectionBars), 0 },
        { nameof(McpLimits.MaxProjectionBars), 101 },
        { nameof(McpLimits.MaxSeasonalityYears), 0 },
        { nameof(McpLimits.MaxSeasonalityYears), 101 },
    };

    [Theory]
    [MemberData(nameof(OutOfBand))]
    public void AValueOutsideItsBand_FailsAndNamesTheKey(string key, int value)
    {
        var limits = new McpLimits();
        typeof(McpLimits).GetProperty(key)!.SetValue(limits, value);

        var error = Assert.Throws<McpLimitsException>(() => McpLimitsContract.Validate(limits));

        Assert.Equal(key, error.Key);
        Assert.Contains($"{McpLimits.SectionName}:{key}", error.Message);
    }

    [Fact]
    public void TheRelationsBetweenLimitsAreEnforced()
    {
        Assert.Equal(nameof(McpLimits.HeavyToolTimeoutSeconds),
            Assert.Throws<McpLimitsException>(() => McpLimitsContract.Validate(new McpLimits { ToolTimeoutSeconds = 100, HeavyToolTimeoutSeconds = 50 })).Key);
        Assert.Equal(nameof(McpLimits.DefaultCandleCount),
            Assert.Throws<McpLimitsException>(() => McpLimitsContract.Validate(new McpLimits { MaxCandlesPerRequest = 100, DefaultCandleCount = 101 })).Key);
        Assert.Equal(nameof(McpLimits.MaxOutputTailBars),
            Assert.Throws<McpLimitsException>(() => McpLimitsContract.Validate(new McpLimits { MaxCandlesPerRequest = 100, DefaultCandleCount = 10, MaxOutputTailBars = 101 })).Key);
        Assert.Equal(nameof(McpLimits.DefaultOutputTailBars),
            Assert.Throws<McpLimitsException>(() => McpLimitsContract.Validate(new McpLimits { MaxOutputTailBars = 20, DefaultOutputTailBars = 21 })).Key);
        Assert.Equal(nameof(McpLimits.DefaultTickerPageSize),
            Assert.Throws<McpLimitsException>(() => McpLimitsContract.Validate(new McpLimits { MaxTickersPerPage = 50, DefaultTickerPageSize = 51 })).Key);
    }

    [Fact]
    public void TheCurveLimitInForceNeverExceedsWhatTheResponseSizeCanCarry_AndAConfigurationIsNotRefusedForIt()
    {
        // 4 MiB / 160 bytes per point = 26214 points fit; a response limit at its band minimum (64 KiB) still starts, with 409 points in force.
        Assert.Equal(5000, McpLimitsContract.EffectiveMaxEquityCurvePoints(new McpLimits()));
        Assert.Equal(26214, McpLimitsContract.EffectiveMaxEquityCurvePoints(new McpLimits { MaxEquityCurvePoints = 30000 }));
        Assert.Equal(30000, McpLimitsContract.EffectiveMaxEquityCurvePoints(new McpLimits { MaxEquityCurvePoints = 30000, MaxResponseBytes = 8 * 1024 * 1024 }));
        var smallResponse = new McpLimits { MaxResponseBytes = 64 * 1024 };
        McpLimitsContract.Validate(smallResponse);
        Assert.Equal(409, McpLimitsContract.EffectiveMaxEquityCurvePoints(smallResponse));
    }

    [Fact]
    public void TheEdgesOfEveryBandAreAccepted()
    {
        McpLimitsContract.Validate(new McpLimits
        {
            MaxCandlesPerRequest = 1, DefaultCandleCount = 1, MaxOutputTailBars = 1, DefaultOutputTailBars = 1, ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1,
            QueueWaitSeconds = 0, MaxReturnedTrades = 0, MaxReturnedWarnings = 0, MaxTickersPerPage = 1, DefaultTickerPageSize = 1,
        });
        McpLimitsContract.Validate(new McpLimits
        {
            ToolTimeoutSeconds = 900, HeavyToolTimeoutSeconds = 1800, QueueWaitSeconds = 600, MaxReturnedTrades = 1000, MaxReturnedWarnings = 200, MaxResponseBytes = 8 * 1024 * 1024,
        });
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(50000, 1000)]
    public void TheEdgesOfTheSummaryBandsAreAccepted(int trendBars, int offsets)
    {
        McpLimitsContract.Validate(new McpLimits { DefaultSummaryTrendBars = trendBars, MaxLookbackOffsets = offsets });
    }

    [Fact]
    public void TheTrendWindowInForceFollowsTheCalculationWindow_AndASmallRequestLimitIsNotRefusedForIt()
    {
        Assert.Equal(20, McpLimitsContract.EffectiveDefaultTrendBars(new McpLimits()));
        Assert.Equal(10000, McpLimitsContract.EffectiveMaxTrendBars(new McpLimits()));
        var small = new McpLimits { MaxCandlesPerRequest = 10, DefaultCandleCount = 10, MaxOutputTailBars = 10, DefaultOutputTailBars = 10 };
        McpLimitsContract.Validate(small);
        Assert.Equal(10, McpLimitsContract.EffectiveDefaultTrendBars(small));
        Assert.Equal(10, McpLimitsContract.EffectiveMaxTrendBars(small));
        var one = new McpLimits { MaxCandlesPerRequest = 1, DefaultCandleCount = 1, MaxOutputTailBars = 1, DefaultOutputTailBars = 1 };
        McpLimitsContract.Validate(one);
        Assert.Equal(2, McpLimitsContract.EffectiveMaxTrendBars(one));
        Assert.Equal(2, McpLimitsContract.EffectiveDefaultTrendBars(one));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void TheEdgesOfTheDerivedSeriesBandAreAccepted(int value)
    {
        McpLimitsContract.Validate(new McpLimits { MaxDerivedSeriesPerCall = value });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public void TheEdgesOfTheInlineFilterRuleBandAreAccepted(int value)
    {
        McpLimitsContract.Validate(new McpLimits { MaxInlineFilterRules = value });
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(50000, 50)]
    public void TheEdgesOfTheBacktestDiagnosticsBandsAreAccepted(int points, int episodes)
    {
        McpLimitsContract.Validate(new McpLimits { MaxEquityCurvePoints = points, MaxDrawdownEpisodes = episodes });
    }

    [Theory]
    [InlineData(1, 1, 1, 1, 1, 0)]
    [InlineData(20000, 100, 8, 64, 60, 1000)]
    public void TheEdgesOfTheOptimizationBandsAreAccepted(int combinations, int topK, int parameters, int parallelism, int windows, int minTrades)
    {
        McpLimitsContract.Validate(new McpLimits
        {
            MaxOptimizationCombinations = combinations, MaxOptimizationTopK = topK, MaxOptimizationParameters = parameters, OptimizationDegreeOfParallelism = parallelism, MaxWalkForwardWindows = windows, DefaultMinInSampleTrades = minTrades,
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(WatchlistConstants.MaxRelativePerformanceTargets)]
    public void TheEdgesOfTheComparisonTargetBandAreAccepted(int value)
    {
        McpLimitsContract.Validate(new McpLimits { MaxComparisonTargets = value });
    }

    [Fact]
    public void ConfigurationBindsOverTheStandardValuesAndLeavesTheRestAlone()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{McpLimits.SectionName}:{nameof(McpLimits.MaxCandlesPerRequest)}"] = "6000",
                [$"{McpLimits.SectionName}:{nameof(McpLimits.QueueWaitSeconds)}"] = "0",
                [$"{McpLimits.SectionName}:{nameof(McpLimits.MaxComparisonTargets)}"] = "7",
            })
            .Build();

        McpLimits limits = configuration.GetSection(McpLimits.SectionName).Get<McpLimits>()!;

        Assert.Equal(6000, limits.MaxCandlesPerRequest);
        Assert.Equal(0, limits.QueueWaitSeconds);
        Assert.Equal(7, limits.MaxComparisonTargets);
        Assert.Equal(200, limits.DefaultCandleCount);
        McpLimitsContract.Validate(limits);
    }
}
