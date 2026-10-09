using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>The "RlPortfolio" configuration section binds the partition method by name and the three minimum-bars values as integers.</summary>
public class RlPortfolioSettingsBindingTests
{
    private static RlPortfolioSettings Bind(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(RlPortfolioSettings.SectionName).Get<RlPortfolioSettings>() ?? new RlPortfolioSettings();

    [Fact]
    public void ThePartitionModeBindsByEnumName()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["RlPortfolio:PartitionMode"] = "BlockCount",
            ["RlPortfolio:MinimumTrainBars"] = "500",
            ["RlPortfolio:MinimumValidationBars"] = "193",
            ["RlPortfolio:MinimumTestBars"] = "384",
        });
        Assert.Equal(RlPortfolioPartitionMode.BlockCount, settings.PartitionMode);
        Assert.Equal((500, 193, 384), (settings.MinimumTrainBars, settings.MinimumValidationBars, settings.MinimumTestBars));
        Assert.Equal(new RlPanelBlockRequirement(3, 2, 2), RlPanelPartitionPlan.FromSettings(settings).Requirement);
    }

    [Fact]
    public void MissingKeysKeepTheDefaultRatioRule()
    {
        var settings = Bind(new Dictionary<string, string?> { ["RlPortfolio:MaxSymbols"] = "50" });
        Assert.True(RlPanelPartitionPlan.FromSettings(settings).IsDefault);
    }

    [Fact]
    public void TheShippedExampleSettingsAreTheDefaults()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "StockAnalyzer.sln"))) directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(directory!, "StockAnalyzer.Avalonia", "appsettings.example.json")).Build();
        var settings = config.GetSection(RlPortfolioSettings.SectionName).Get<RlPortfolioSettings>()!;
        Assert.True(RlPanelPartitionPlan.FromSettings(settings).IsDefault);
        Assert.Equal(RlPortfolioSettings.DefaultMinimumBars, settings.MinimumTrainBars);
        Assert.Empty(RlPortfolioSettingsValidator.Validate(settings, RlTrainingValidation.FreezeLimits(continuous: true)));
    }
}
