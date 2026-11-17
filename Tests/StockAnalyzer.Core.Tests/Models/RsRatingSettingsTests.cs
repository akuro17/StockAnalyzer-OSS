#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// The RS Rating key of <c>MarketDataSettings</c> (plan 09/10 peer review, Run 1): the shipped default works without any entry in appsettings.json,
/// a configured value overrides it, and the startup validation follows <see cref="RsRatingLimits"/>.
/// </summary>
public class RsRatingSettingsTests
{
    private static MarketDataSettings Bind(IReadOnlyDictionary<string, string?> entries)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
        // The production registration, applied the way the options system does it (this test project has no service provider package).
        IServiceCollection services = new ServiceCollection().AddStockAnalyzerSettingsOptions(configuration);
        IConfigureOptions<MarketDataSettings>[] setups = services
            .Where(d => d.ServiceType == typeof(IConfigureOptions<MarketDataSettings>))
            .Select(d => Assert.IsAssignableFrom<IConfigureOptions<MarketDataSettings>>(d.ImplementationInstance))
            .ToArray();
        Assert.NotEmpty(setups);
        return new OptionsFactory<MarketDataSettings>(setups, Array.Empty<IPostConfigureOptions<MarketDataSettings>>()).Create(Options.DefaultName);
    }

    private static StockAnalyzerSettings CreateSettings(MarketDataSettings? marketData)
        => new(
            new ConfigurationBuilder().Build(),
            Options.Create(new PythonSettings()),
            Options.Create(new ChartDefaultSettings()),
            Options.Create(new PredictionSettings()),
            Options.Create(new ScreenerSettings()),
            Options.Create(new SmartScreenerSettings()),
            Options.Create(new InfrastructureSettings()),
            Options.Create(new MarketStructureSettings()),
            Options.Create(new PatternRecognitionSettings()),
            Options.Create(new ResilienceSettings()),
            Options.Create(new LocalizationSettings()),
            marketDataOptions: marketData is null ? null : Options.Create(marketData));

    [Fact]
    public void WithoutAnyConfigurationEntry_TheShippedDefaultsApplyAndValidateSilently()
    {
        MarketDataSettings settings = Bind(new Dictionary<string, string?>());

        Assert.Equal(RsRatingLimits.DefaultMaxStaleCalendarDays, settings.RsRatingMaxStaleCalendarDays);
        Assert.Equal(10, settings.RsRatingMaxStaleCalendarDays);
        Assert.Equal(RsRatingLimits.DefaultRefreshDebounceMilliseconds, settings.RsRatingRefreshDebounceMilliseconds);
        Assert.Equal(500, settings.RsRatingRefreshDebounceMilliseconds);
        Assert.Empty(RsRatingSettingsValidator.Validate(settings.RsRatingMaxStaleCalendarDays, settings.RsRatingRefreshDebounceMilliseconds));
    }

    [Fact]
    public void WithOtherMarketDataKeysOnly_TheRsRatingKeysStayAtTheDefaults()
    {
        MarketDataSettings settings = Bind(new Dictionary<string, string?> { ["MarketDataSettings:DailyDataPath"] = "Data/Daily" });

        Assert.Equal(RsRatingLimits.DefaultMaxStaleCalendarDays, settings.RsRatingMaxStaleCalendarDays);
        Assert.Equal(RsRatingLimits.DefaultRefreshDebounceMilliseconds, settings.RsRatingRefreshDebounceMilliseconds);
    }

    [Fact]
    public void AConfiguredValue_OverridesTheDefault()
    {
        MarketDataSettings settings = Bind(new Dictionary<string, string?>
        {
            [RsRatingSettingsValidator.MaxStaleCalendarDaysKey] = "21",
            [RsRatingSettingsValidator.RefreshDebounceMillisecondsKey] = "1500"
        });

        Assert.Equal(21, settings.RsRatingMaxStaleCalendarDays);
        Assert.Equal(1500, settings.RsRatingRefreshDebounceMilliseconds);
    }

    [Fact]
    public void TheDefaults_LieInsideTheStandardBands()
    {
        Assert.True(RsRatingLimits.MaxStaleCalendarDays.IsWithinStandard(RsRatingLimits.DefaultMaxStaleCalendarDays));
        Assert.True(RsRatingLimits.RefreshDebounceMilliseconds.IsWithinStandard(RsRatingLimits.DefaultRefreshDebounceMilliseconds));
    }

    [Theory]
    [InlineData(-1, 500, RsRatingSettingsValidator.MaxStaleCalendarDaysKey)]
    [InlineData(367, 500, RsRatingSettingsValidator.MaxStaleCalendarDaysKey)]
    [InlineData(10, -1, RsRatingSettingsValidator.RefreshDebounceMillisecondsKey)]
    [InlineData(10, 5001, RsRatingSettingsValidator.RefreshDebounceMillisecondsKey)]
    public void AValueOutsideTheHardBand_StopsTheStartupAndNamesTheKey(int days, int debounceMs, string key)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => RsRatingSettingsValidator.Validate(days, debounceMs));

        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData(0, 500, RsRatingSettingsValidator.MaxStaleCalendarDaysKey)]
    [InlineData(2, 500, RsRatingSettingsValidator.MaxStaleCalendarDaysKey)]
    [InlineData(32, 500, RsRatingSettingsValidator.MaxStaleCalendarDaysKey)]
    [InlineData(366, 500, RsRatingSettingsValidator.MaxStaleCalendarDaysKey)]
    [InlineData(10, 0, RsRatingSettingsValidator.RefreshDebounceMillisecondsKey)]
    [InlineData(10, 99, RsRatingSettingsValidator.RefreshDebounceMillisecondsKey)]
    [InlineData(10, 2001, RsRatingSettingsValidator.RefreshDebounceMillisecondsKey)]
    [InlineData(10, 5000, RsRatingSettingsValidator.RefreshDebounceMillisecondsKey)]
    public void AValueBetweenTheStandardAndTheHardBand_RunsWithOneWarning(int days, int debounceMs, string key)
    {
        string[] warnings = RsRatingSettingsValidator.Validate(days, debounceMs).ToArray();

        Assert.Single(warnings);
        Assert.Contains(key, warnings[0]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(367)]
    public void TheToleranceAlone_StopsOutsideTheHardBandWithTheSameKey(int days)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => RsRatingSettingsValidator.ValidateMaxStaleCalendarDays(days));

        Assert.Contains(RsRatingSettingsValidator.MaxStaleCalendarDaysKey, error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(32)]
    [InlineData(366)]
    public void TheToleranceAlone_GivesTheSameWarningsAsTheFullValidation(int days)
        => Assert.Equal(
            RsRatingSettingsValidator.Validate(days, RsRatingLimits.DefaultRefreshDebounceMilliseconds),
            RsRatingSettingsValidator.ValidateMaxStaleCalendarDays(days));

    [Theory]
    [InlineData(3, 100)]
    [InlineData(10, 500)]
    [InlineData(31, 2000)]
    public void AValueInsideTheStandardBand_RunsWithoutAWarning(int days, int debounceMs)
        => Assert.Empty(RsRatingSettingsValidator.Validate(days, debounceMs));

    [Fact]
    public void TheSettingsFacade_StartsWithoutTheMarketDataOptions()
        => Assert.NotNull(CreateSettings(null));

    [Fact]
    public void TheSettingsFacade_StartsWithTheShippedDefaults()
        => Assert.NotNull(CreateSettings(new MarketDataSettings()));

    [Fact]
    public void TheSettingsInterface_GivesTheConfiguredDebounce_AndTheShippedDefaultWithoutTheOptions()
    {
        // A default interface member is not visible through the class type: the interface type is used (SA_ARCHITECTURE_RULES).
        IStockAnalyzerSettings configured = CreateSettings(new MarketDataSettings { RsRatingRefreshDebounceMilliseconds = 1500 });
        IStockAnalyzerSettings withoutOptions = CreateSettings(null);

        Assert.Equal(1500, configured.RsRatingRefreshDebounceMilliseconds);
        Assert.Equal(RsRatingLimits.DefaultRefreshDebounceMilliseconds, withoutOptions.RsRatingRefreshDebounceMilliseconds);
    }

    [Fact]
    public void TheDefaultInterfaceMember_FollowsThePocoDefault_ForAnImplementerThatDoesNotOverrideIt()
    {
        var implementer = new Moq.Mock<IStockAnalyzerSettings> { CallBase = true };

        Assert.Equal(new MarketDataSettings().RsRatingRefreshDebounceMilliseconds, implementer.Object.RsRatingRefreshDebounceMilliseconds);
    }

    [Fact]
    public void TheSettingsFacade_RefusesAValueOutsideTheHardBand()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSettings(new MarketDataSettings { RsRatingMaxStaleCalendarDays = 367 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSettings(new MarketDataSettings { RsRatingRefreshDebounceMilliseconds = 5001 }));
    }

    [Fact]
    public void TheShippedAppSettingsExample_MatchesTheDefaults()
    {
        string example = System.IO.File.ReadAllText(System.IO.Path.Combine(StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "appsettings.example.json"));
        using var document = System.Text.Json.JsonDocument.Parse(example);
        var section = document.RootElement.GetProperty(RsRatingSettingsValidator.SectionName);

        Assert.Equal(RsRatingLimits.DefaultMaxStaleCalendarDays, section.GetProperty("RsRatingMaxStaleCalendarDays").GetInt32());
        Assert.Equal(RsRatingLimits.DefaultRefreshDebounceMilliseconds, section.GetProperty("RsRatingRefreshDebounceMilliseconds").GetInt32());
    }
}
