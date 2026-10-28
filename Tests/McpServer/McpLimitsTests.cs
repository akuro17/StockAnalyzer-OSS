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
