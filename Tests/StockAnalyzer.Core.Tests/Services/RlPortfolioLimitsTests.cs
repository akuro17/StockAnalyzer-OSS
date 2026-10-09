using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Portfolio resource arithmetic (hand-computed) and the settings contract.</summary>
public sealed class RlPortfolioLimitsTests
{
    private static readonly TrainingResourceLimits Limits = RlTrainingValidation.FreezeLimits(continuous: true);

    [Theory]
    [InlineData(1, 326)]    // 1 * (64*5 + 2) + 4
    [InlineData(2, 648)]
    [InlineData(50, 16_104)]
    public void ObservationLengthFollowsTheSpecificationFormula(int symbols, long expected)
        => Assert.Equal(expected, RlPortfolioResources.ObservationLength(symbols));

    [Fact]
    public void RolloutBytesAreTheHandComputedBufferRule()
    {
        // (512 + 64) rows * ((L + (N + 1) + 6 auxiliary) * 4 bytes) + two 1 MiB IPC frames, for N = 50: L = 16104 -> 16161 values.
        Assert.Equal(576L * (16_104 + 51 + 6) * 4 + 2L * 1024 * 1024, RlPortfolioResources.RolloutBytes(50));
        Assert.Equal(576L * (326 + 2 + 6) * 4 + 2L * 1024 * 1024, RlPortfolioResources.RolloutBytes(1));
    }

    [Fact]
    public void MaximumSymbolsIsTheLargestUniverseThatFitsAndNoMore()
    {
        int maximum = RlPortfolioResources.MaximumSymbols(Limits);
        Assert.InRange(maximum, 150, 200); // hand estimate in the options document: about 178
        Assert.True(RlPortfolioResources.Fits(maximum, Limits));
        Assert.False(RlPortfolioResources.Fits(maximum + 1, Limits));
        Assert.True(RlPortfolioResources.RolloutBytes(maximum) <= RlContract.BufferByteLimit);
        Assert.True(RlPortfolioResources.RolloutBytes(maximum + 1) > RlContract.BufferByteLimit);
    }

    [Fact]
    public void ASmallerTensorLimitLowersTheMaximum()
    {
        var small = RlTrainingValidation.FreezeLimits(new TrainingResourceOverrides(MaxTensorSizeMiB: 32), continuous: true);
        int smaller = RlPortfolioResources.MaximumSymbols(small);
        Assert.True(smaller < RlPortfolioResources.MaximumSymbols(Limits));
        Assert.True(RlPortfolioResources.RolloutBytes(smaller) <= 32L * TrainingResourceOverrides.MiB);
        Assert.True(RlPortfolioResources.RolloutBytes(smaller + 1) > 32L * TrainingResourceOverrides.MiB);
    }

    [Fact]
    public void TheSingleSymbolShapeMatchesTheExistingContinuousRule()
    {
        // The portfolio buffer rule and FreezeLimits(continuous) share one function: with the single-symbol shapes they give the same bytes.
        long single = RlTrainingValidation.ContinuousReplayBytes(RlContract.ObservationLength, RlContinuousContract.ActionCount);
        Assert.Equal((512L + 64) * (RlContract.ObservationLength * 4L + (2 + 6) * 4L) + 2L * 1024 * 1024, single);
    }

    [Fact]
    public void ObservationFrameBytesAreTheWorstCaseJsonTextPerValue()
        => Assert.Equal(16_104L * 16, RlPortfolioResources.ObservationFrameBytes(50)); // sign + 9 digits + point + "E-38" + separator = 16 characters

    [Fact]
    public void DefaultSettingsAreWithinTheStandardBands()
    {
        var warnings = RlPortfolioSettingsValidator.Validate(new RlPortfolioSettings(), Limits);
        Assert.Empty(warnings);
        Assert.Equal(50, new RlPortfolioSettings().MaxSymbols);
        Assert.Equal(25, new RlPortfolioSettings().HoldoutPercent);
    }

    [Theory]
    [InlineData(1, 25)]
    [InlineData(0, 25)]
    [InlineData(50, 0)]
    [InlineData(50, 51)]
    public void HardBandViolationsThrow(int maxSymbols, int holdoutPercent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSettingsValidator.Validate(
            new RlPortfolioSettings { MaxSymbols = maxSymbols, HoldoutPercent = holdoutPercent }, Limits));

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void TheWeightCapMustBeAPercentOfTheAccount(int percent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSettingsValidator.Validate(
            new RlPortfolioSettings { MaxWeightPercent = percent }, Limits));

    [Fact]
    public void AWeightCapBelowTheStandardBandOnlyWarns()
    {
        Assert.Equal(100, new RlPortfolioSettings().MaxWeightPercent);
        var warnings = RlPortfolioSettingsValidator.Validate(new RlPortfolioSettings { MaxWeightPercent = 5 }, Limits);
        Assert.Contains(warnings, w => w.Contains("RlPortfolio:MaxWeightPercent"));
        Assert.Empty(RlPortfolioSettingsValidator.Validate(new RlPortfolioSettings { MaxWeightPercent = 10 }, Limits));
    }

    [Fact]
    public void TheHardMaximumFollowsTheResourceRuleNotALiteral()
    {
        int maximum = RlPortfolioResources.MaximumSymbols(Limits);
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSettingsValidator.Validate(new RlPortfolioSettings { MaxSymbols = maximum + 1 }, Limits));
        var warnings = RlPortfolioSettingsValidator.Validate(new RlPortfolioSettings { MaxSymbols = maximum }, Limits);
        Assert.Contains(warnings, w => w.Contains("RlPortfolio:MaxSymbols"));
    }

    [Fact]
    public void OutsideTheStandardBandOnlyWarns()
    {
        var warnings = RlPortfolioSettingsValidator.Validate(new RlPortfolioSettings { MaxSymbols = 80, HoldoutPercent = 45 }, Limits);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void ThePartitionDefaultsKeepTheExistingShortHistoryRule()
    {
        var settings = new RlPortfolioSettings();
        Assert.Equal(RlPortfolioPartitionMode.Ratio, settings.PartitionMode);
        Assert.Equal((192, 192, 192), (settings.MinimumTrainBars, settings.MinimumValidationBars, settings.MinimumTestBars));
        Assert.Equal(RlContract.SupportLength, RlPortfolioSettings.DefaultMinimumBars);
        Assert.True(RlPanelPartitionPlan.FromSettings(settings).IsDefault);
    }

    [Fact]
    public void TheMinimumBarsBandIsDerivedFromTheContractsNotTyped()
    {
        // 10240 training transitions per seed / 128 decisions per block = 80 blocks of 192 bars; the standard band ends at the 5 blocks Standard demands.
        Assert.Equal(80, RlPortfolioLimits.MaximumBlocks);
        Assert.Equal(new SettingBand(192, 15_360, 192, 960), RlPortfolioLimits.MinimumBars);
    }

    [Theory]
    [InlineData(nameof(RlPortfolioSettings.MinimumTrainBars))]
    [InlineData(nameof(RlPortfolioSettings.MinimumValidationBars))]
    [InlineData(nameof(RlPortfolioSettings.MinimumTestBars))]
    public void TheMinimumBarsOfEveryPartitionAreCheckedAgainstTheBand(string key)
    {
        RlPortfolioSettings With(int value)
        {
            var settings = new RlPortfolioSettings();
            typeof(RlPortfolioSettings).GetProperty(key)!.SetValue(settings, value);
            return settings;
        }
        foreach (int outside in new[] { 191, 0, -1, 15_361 })
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSettingsValidator.Validate(With(outside), Limits));
            Assert.Equal(key, error.ParamName);
        }
        foreach (int inside in new[] { 192, 193, 384, 960 })
            Assert.Empty(RlPortfolioSettingsValidator.Validate(With(inside), Limits));
        foreach (int between in new[] { 961, 5_000, 15_360 })
        {
            var warning = Assert.Single(RlPortfolioSettingsValidator.Validate(With(between), Limits));
            Assert.Contains($"RlPortfolio:{key}", warning);
        }
    }

    [Fact]
    public void AnUnknownPartitionModeIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioSettingsValidator.Validate(
            new RlPortfolioSettings { PartitionMode = (RlPortfolioPartitionMode)7 }, Limits));

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => RlPortfolioSettingsValidator.Validate(null!, Limits));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioSettingsValidator.Validate(new RlPortfolioSettings(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioResources.ObservationLength(0));
    }

    [Fact]
    public void RunOverridesCopyTheSettingsAndNeverTouchTheConfiguredOnes()
    {
        var configured = new RlPortfolioSettings
        { MaxSymbols = 30, HoldoutPercent = 25, MaxWeightPercent = 100, PartitionMode = RlPortfolioPartitionMode.BlockCount, MinimumTrainBars = 576, MinimumValidationBars = 1920, MinimumTestBars = 1920 };
        Assert.Same(configured, configured.WithRunOverrides(null, null));                                  // nothing overridden: the very same settings
        var changed = configured.WithRunOverrides(40, 35);
        Assert.NotSame(configured, changed);
        Assert.Equal((30, 40, 35, RlPortfolioPartitionMode.BlockCount, 576, 1920, 1920),
            (changed.MaxSymbols, changed.HoldoutPercent, changed.MaxWeightPercent, changed.PartitionMode, changed.MinimumTrainBars, changed.MinimumValidationBars, changed.MinimumTestBars));
        Assert.Equal((25, 100), (configured.HoldoutPercent, configured.MaxWeightPercent));                // the configured settings are never modified
        Assert.Equal((40, 100), (configured.WithRunOverrides(40, null).HoldoutPercent, configured.WithRunOverrides(40, null).MaxWeightPercent));
        Assert.Equal((25, 35), (configured.WithRunOverrides(null, 35).HoldoutPercent, configured.WithRunOverrides(null, 35).MaxWeightPercent));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(51, null)]
    [InlineData(null, 0)]
    [InlineData(null, 101)]
    public async Task ARunWithAnOutOfBandOverrideIsRejectedBeforeAnythingIsCaptured(int? holdout, int? weight)
    {
        var service = new RlPortfolioService(new RlPortfolioSettings());
        var request = new RlPortfolioRunRequest(ImmutableArray.Create("AAA", "BBB"),
            new DateOnly(2000, 1, 1), new DateOnly(2005, 1, 1), true, RlPanelEvaluationMode.Standard,
            RlContract.CreateConfiguration(new BacktestConfiguration { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m }),
            true, HoldoutPercent: holdout, MaxWeightPercent: weight);
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunAsync(request, null, default));
        Assert.Equal(holdout is not null ? nameof(RlPortfolioSettings.HoldoutPercent) : nameof(RlPortfolioSettings.MaxWeightPercent), error.ParamName);   // the settings check, not another argument
    }
}
