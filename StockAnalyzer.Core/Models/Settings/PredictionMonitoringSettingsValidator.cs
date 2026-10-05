using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The startup validation phase of <see cref="PredictionMonitoringSettings"/>: operational values are compared with the system
/// contract <see cref="PredictionMonitoringLimits"/>. A hard-limit violation is fatal (an exception stops the startup); a value
/// outside the standard band only produces a warning for the operator.
/// </summary>
public static class PredictionMonitoringSettingsValidator
{
    /// <summary>
    /// Throws on any hard-limit or structural violation. Returns the warnings of values that run but leave the standard band
    /// (empty when every value is standard); the caller with a logger reports them.
    /// </summary>
    public static IReadOnlyList<string> Validate(PredictionMonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Backend)) throw new ArgumentOutOfRangeException(nameof(settings.Backend));
        var warnings = new List<string>();
        Check(warnings, nameof(settings.BusyTimeoutSeconds), settings.BusyTimeoutSeconds, PredictionMonitoringLimits.BusyTimeoutSeconds, "seconds");
        Check(warnings, nameof(settings.OutboxPollIntervalSeconds), settings.OutboxPollIntervalSeconds, PredictionMonitoringLimits.OutboxPollSeconds, "seconds");
        Check(warnings, nameof(settings.SchedulerPollIntervalSeconds), settings.SchedulerPollIntervalSeconds, PredictionMonitoringLimits.SchedulerPollSeconds, "seconds");
        Check(warnings, nameof(settings.HealthEvaluationIntervalSeconds), settings.HealthEvaluationIntervalSeconds, PredictionMonitoringLimits.HealthEvaluationSeconds, "seconds");
        Check(warnings, nameof(settings.LeaseRetryIntervalMilliseconds), settings.LeaseRetryIntervalMilliseconds, PredictionMonitoringLimits.LeaseRetryMilliseconds, "milliseconds");
        Check(warnings, nameof(settings.AttestedWorkingCopyRetentionMinutes), settings.AttestedWorkingCopyRetentionMinutes, PredictionMonitoringLimits.AttestedWorkingCopyRetentionMinutes, "minutes");
        Check(warnings, nameof(settings.AttestedManifestMaxBytes), settings.AttestedManifestMaxBytes, PredictionMonitoringLimits.AttestedManifestBytes, "bytes");
        ValidateSources(settings);
        return warnings;
    }

    /// <summary>The hard limits of the loop cadences only, for a component that is built without the factory (it never reports warnings).</summary>
    public static void ValidateIntervals(PredictionMonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Check(null, nameof(settings.SchedulerPollIntervalSeconds), settings.SchedulerPollIntervalSeconds, PredictionMonitoringLimits.SchedulerPollSeconds, "seconds");
        Check(null, nameof(settings.HealthEvaluationIntervalSeconds), settings.HealthEvaluationIntervalSeconds, PredictionMonitoringLimits.HealthEvaluationSeconds, "seconds");
        Check(null, nameof(settings.LeaseRetryIntervalMilliseconds), settings.LeaseRetryIntervalMilliseconds, PredictionMonitoringLimits.LeaseRetryMilliseconds, "milliseconds");
    }

    private static void Check(List<string>? warnings, string name, long value, SettingBand band, string unit)
    {
        if (!band.IsWithinHardLimits(value))
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be {Format(band.HardMin)} to {Format(band.HardMax)} {unit}; the value is outside the system limits.");
        if (!band.IsWithinStandard(value))
            warnings?.Add($"{PredictionMonitoringSettings.SectionName}:{name} = {Format(value)} {unit} is outside the standard range "
                + $"{Format(band.StandardMin)} to {Format(band.StandardMax)}; the application runs but detection delays or resource use may suffer.");
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static void ValidateSources(PredictionMonitoringSettings settings)
    {
        if (settings.AttestedSources is null) throw new ArgumentNullException(nameof(settings.AttestedSources));
        foreach (var source in settings.AttestedSources)
            if (source is null || string.IsNullOrWhiteSpace(source.Symbol) || string.IsNullOrWhiteSpace(source.ProviderId)
                || string.IsNullOrWhiteSpace(source.InstrumentId) || !MonitoringTimeframes.IsSupported(source.Timeframe))
                throw new ArgumentException("Every attested source needs a symbol, provider, instrument and a daily, weekly or monthly timeframe.", nameof(settings.AttestedSources));
        if (settings.AttestedSources.Select(s => (s.Symbol, s.Timeframe)).Distinct().Count() != settings.AttestedSources.Count)
            throw new ArgumentException("A symbol and timeframe can be enrolled in only one attested source.", nameof(settings.AttestedSources));
    }
}
