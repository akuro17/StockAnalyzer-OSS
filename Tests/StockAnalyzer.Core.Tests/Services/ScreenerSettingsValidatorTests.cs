using System;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The startup validation phase of the Screener settings: hard band = fatal (names the key), above the standard band = one warning, standard = silent.
/// </summary>
public class ScreenerSettingsValidatorTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(2501)]
    [InlineData(int.MaxValue)]
    public void MaxConditionOffset_OutsideTheHardBand_ThrowsNamingTheKey(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScreenerSettingsValidator.Validate(value));

        Assert.Contains("Screener:MaxConditionOffset", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(500)]
    public void MaxConditionOffset_InsideTheStandardBand_IsSilent(int value)
    {
        Assert.Empty(ScreenerSettingsValidator.Validate(value));
    }

    [Theory]
    [InlineData(501)]
    [InlineData(2500)]
    public void MaxConditionOffset_BetweenTheBands_RunsWithOneWarningNamingTheKey(int value)
    {
        string warning = Assert.Single(ScreenerSettingsValidator.Validate(value));

        Assert.Contains("Screener:MaxConditionOffset", warning);
        Assert.Contains(value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), warning);
    }

    [Fact]
    public void ShippedDefault_IsInsideTheStandardBand()
    {
        var limits = ScreenerConditionOffsetLimits.MaxConditionOffset;

        Assert.True(limits.IsWithinStandard(ScreenerConditionOffsetLimits.DefaultMaxConditionOffset));
    }

    // Filters tree size: hard band = nodes 2..256, depth 1..16, and depth <= nodes (owner decision 2026-10-16).
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(257)]
    [InlineData(int.MaxValue)]
    public void MaxFilterTreeNodes_OutsideTheHardBand_ThrowsNamingTheKey(int nodes)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScreenerSettingsValidator.ValidateFilterTree(nodes, 1));

        Assert.Contains("Screener:MaxFilterTreeNodes", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(int.MaxValue)]
    public void MaxFilterTreeDepth_OutsideTheHardBand_ThrowsNamingTheKey(int depth)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScreenerSettingsValidator.ValidateFilterTree(256, depth));

        Assert.Contains("Screener:MaxFilterTreeDepth", ex.Message);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(10, 11)]
    [InlineData(15, 16)]
    public void MaxFilterTreeDepth_AboveTheNodeCount_ThrowsNamingBothKeys(int nodes, int depth)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScreenerSettingsValidator.ValidateFilterTree(nodes, depth));

        Assert.Contains("Screener:MaxFilterTreeDepth", ex.Message);
        Assert.Contains("Screener:MaxFilterTreeNodes", ex.Message);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(16, 16)]
    [InlineData(64, 8)]
    [InlineData(256, 16)]
    public void FilterTreeBounds_InsideTheContract_AreAcceptedSilently(int nodes, int depth)
    {
        Assert.Empty(ScreenerSettingsValidator.ValidateFilterTree(nodes, depth));
    }

    [Fact]
    public void ShippedFilterTreeDefaults_AreInsideTheContract()
    {
        Assert.Empty(ScreenerSettingsValidator.ValidateFilterTree(ScreenerFilterTreeLimits.DefaultMaxFilterTreeNodes, ScreenerFilterTreeLimits.DefaultMaxFilterTreeDepth));
        Assert.Equal(64, ScreenerFilterTreeLimits.DefaultMaxFilterTreeNodes);
        Assert.Equal(8, ScreenerFilterTreeLimits.DefaultMaxFilterTreeDepth);
    }
}
