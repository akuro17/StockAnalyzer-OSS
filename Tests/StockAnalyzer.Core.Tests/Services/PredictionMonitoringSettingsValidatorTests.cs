#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The three-tier model of the monitoring settings: a hard-limit violation is fatal, a value between the standard band and the hard
/// limit runs with a warning, and a standard value runs silently. The settings class holds the values only.
/// </summary>
public sealed class PredictionMonitoringSettingsValidatorTests : IDisposable
{
    private readonly SqliteMonitoringTestKit _kit = new();
    public void Dispose() => _kit.Dispose();

    private static object[] Row(string name, Action<PredictionMonitoringSettings, long> set, SettingBand band) => [name, set, band];

    public static IEnumerable<object[]> Bands() =>
    [
        Row(nameof(PredictionMonitoringSettings.SchedulerPollIntervalSeconds), (s, v) => s.SchedulerPollIntervalSeconds = (int)v, PredictionMonitoringLimits.SchedulerPollSeconds),
        Row(nameof(PredictionMonitoringSettings.LeaseRetryIntervalMilliseconds), (s, v) => s.LeaseRetryIntervalMilliseconds = (int)v, PredictionMonitoringLimits.LeaseRetryMilliseconds),
        Row(nameof(PredictionMonitoringSettings.HealthEvaluationIntervalSeconds), (s, v) => s.HealthEvaluationIntervalSeconds = (int)v, PredictionMonitoringLimits.HealthEvaluationSeconds),
        Row(nameof(PredictionMonitoringSettings.OutboxPollIntervalSeconds), (s, v) => s.OutboxPollIntervalSeconds = (int)v, PredictionMonitoringLimits.OutboxPollSeconds),
        Row(nameof(PredictionMonitoringSettings.BusyTimeoutSeconds), (s, v) => s.BusyTimeoutSeconds = (int)v, PredictionMonitoringLimits.BusyTimeoutSeconds),
        Row(nameof(PredictionMonitoringSettings.AttestedWorkingCopyRetentionMinutes), (s, v) => s.AttestedWorkingCopyRetentionMinutes = (int)v, PredictionMonitoringLimits.AttestedWorkingCopyRetentionMinutes),
        Row(nameof(PredictionMonitoringSettings.AttestedManifestMaxBytes), (s, v) => s.AttestedManifestMaxBytes = v, PredictionMonitoringLimits.AttestedManifestBytes),
    ];

    [Theory]
    [MemberData(nameof(Bands))]
    public void AValueOutsideTheHardLimits_IsFatal_AndNamesTheSetting(string name, Action<PredictionMonitoringSettings, long> set, SettingBand band)
    {
        foreach (var value in new[] { band.HardMin - 1, band.HardMax + 1 })
        {
            var settings = new PredictionMonitoringSettings();
            set(settings, value);
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.Validate(settings));
            Assert.Equal(name, ex.ParamName);
        }
    }

    [Theory]
    [MemberData(nameof(Bands))]
    public void AValueInsideTheHardLimitsButOutsideTheStandardBand_RunsWithAWarning(string name, Action<PredictionMonitoringSettings, long> set, SettingBand band)
    {
        var candidates = new[] { band.HardMin, band.StandardMin - 1, band.StandardMax + 1, band.HardMax }
            .Where(v => band.IsWithinHardLimits(v) && !band.IsWithinStandard(v)).ToArray();
        Assert.NotEmpty(candidates);
        foreach (var value in candidates)
        {
            var settings = new PredictionMonitoringSettings();
            set(settings, value);
            var warning = Assert.Single(PredictionMonitoringSettingsValidator.Validate(settings));
            Assert.Contains($"{PredictionMonitoringSettings.SectionName}:{name}", warning);
        }
    }

    [Theory]
    [MemberData(nameof(Bands))]
    public void AStandardValue_RunsWithoutAWarning(string name, Action<PredictionMonitoringSettings, long> set, SettingBand band)
    {
        foreach (var value in new[] { band.StandardMin, band.StandardMax })
        {
            var settings = new PredictionMonitoringSettings();
            set(settings, value);
            var warnings = PredictionMonitoringSettingsValidator.Validate(settings);
            Assert.True(warnings.Count == 0, $"{name} = {value} is standard but warned: {string.Join(" | ", warnings)}");
        }
    }

    [Fact]
    public void TheContractIsOrdered_AndEveryDefaultIsStandard()
    {
        foreach (var band in Bands().Select(row => (SettingBand)row[2]))
            Assert.True(band.HardMin <= band.StandardMin && band.StandardMin <= band.StandardMax && band.StandardMax <= band.HardMax);
        Assert.Empty(PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings()));
    }

    [Fact]
    public void TheSettingsClassHoldsValuesOnly_NoValidationAndNoBounds()
    {
        var members = typeof(PredictionMonitoringSettings).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(members, m => m.Name.StartsWith("Validate", StringComparison.Ordinal)
            || m.Name.StartsWith("Min", StringComparison.Ordinal) || m.Name.StartsWith("Max", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateIntervals_EnforcesTheHardLimitsOfTheCadencesOnly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.ValidateIntervals(new PredictionMonitoringSettings { SchedulerPollIntervalSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.ValidateIntervals(new PredictionMonitoringSettings { HealthEvaluationIntervalSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.ValidateIntervals(new PredictionMonitoringSettings { LeaseRetryIntervalMilliseconds = 0 }));
        PredictionMonitoringSettingsValidator.ValidateIntervals(new PredictionMonitoringSettings { SchedulerPollIntervalSeconds = 1, BusyTimeoutSeconds = 0 }); // not its concern
    }

    [Fact]
    public void StructuralViolations_AreFatal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings { Backend = (PredictionMonitoringBackendKind)99 }));
        Assert.Throws<ArgumentException>(() => PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings
        { AttestedSources = [new AttestedSourceEnrollment { Symbol = "S", ProviderId = "P", InstrumentId = "I", Timeframe = TimeframeType.H1 }] }));
        Assert.Throws<ArgumentException>(() => PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings
        { AttestedSources = [new AttestedSourceEnrollment { Symbol = "S", ProviderId = "", InstrumentId = "I" }] }));
        var source = new AttestedSourceEnrollment { Symbol = "S", ProviderId = "P", InstrumentId = "I" };
        Assert.Throws<ArgumentException>(() => PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings { AttestedSources = [source, source] }));
        Assert.Empty(PredictionMonitoringSettingsValidator.Validate(new PredictionMonitoringSettings { AttestedSources = [source] }));
    }

    private PredictionMonitoringBackendFactory FactoryOf(PredictionMonitoringSettings settings, ILogger<PredictionMonitoringBackendFactory> logger) =>
        new(_kit.RegistryOrMock, Options.Create(settings), logger, _kit.Clock, _kit.MonitorRoot);

    [Fact]
    public async Task TheFactory_ReportsEachStandardBandViolationAsAWarning_AndStillStarts()
    {
        var logger = new CapturingLogger<PredictionMonitoringBackendFactory>();
        var factory = FactoryOf(new PredictionMonitoringSettings { SchedulerPollIntervalSeconds = 5, AttestedWorkingCopyRetentionMinutes = 2000 }, logger);
        await factory.InitializeAsync();
        Assert.False(factory.UsesSqlite);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();
        Assert.Contains(warnings, m => m.Contains(nameof(PredictionMonitoringSettings.SchedulerPollIntervalSeconds), StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains(nameof(PredictionMonitoringSettings.AttestedWorkingCopyRetentionMinutes), StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFactory_StopsTheStartup_OnAHardLimitViolation_WithoutAFallback()
    {
        var factory = FactoryOf(new PredictionMonitoringSettings { SchedulerPollIntervalSeconds = 0 }, new CapturingLogger<PredictionMonitoringBackendFactory>());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => factory.InitializeAsync());
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.UsesSqlite);
    }
}
