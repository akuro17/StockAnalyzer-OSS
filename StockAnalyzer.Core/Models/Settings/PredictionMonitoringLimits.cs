namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The accepted bands of one operational setting. The hard band is a system contract: a value outside it makes the runtime fail
/// (an unrepresentable delay, a spinning loop, an overflowing size), so startup stops. The standard band is the range in which the
/// feature runs without warning; a value between the standard and the hard band still runs, but the operator is warned.
/// </summary>
public readonly record struct SettingBand(long HardMin, long HardMax, long StandardMin, long StandardMax)
{
    public bool IsWithinHardLimits(long value) => value >= HardMin && value <= HardMax;
    public bool IsWithinStandard(long value) => value >= StandardMin && value <= StandardMax;
}

/// <summary>
/// System contract of the prediction monitoring settings (see SA_ARCHITECTURE_RULES, numeric classification). These bands are code,
/// never configuration: a configuration file cannot widen the limit that protects the system from itself. The settings class holds
/// the operational values only; <see cref="PredictionMonitoringSettingsValidator"/> compares them with this contract at startup.
/// </summary>
public static class PredictionMonitoringLimits
{
    private const long MillisecondsPerSecond = 1000;
    private const long BytesPerMebibyte = 1024 * 1024;

    /// <summary>The longest delay the runtime timers accept is uint.MaxValue - 1 milliseconds.</summary>
    private const long MaxDelaySeconds = (uint.MaxValue - 1) / MillisecondsPerSecond;

    /// <summary>The busy timeout is handed to SQLite as a 32-bit millisecond count.</summary>
    private const long MaxBusySeconds = int.MaxValue / MillisecondsPerSecond;

    /// <summary>A working copy older than one year cannot be dated: the age cutoff would leave the representable time range.</summary>
    private const long MaxRetentionMinutes = 365L * 24 * 60;

    /// <summary>
    /// Shutdown waits for an in-flight lease retry (the drain has no timeout), so one retry pause is bounded by the time a user
    /// accepts to wait for the application to close.
    /// </summary>
    private const long MaxLeaseRetryMilliseconds = 60 * MillisecondsPerSecond;

    public static readonly SettingBand SchedulerPollSeconds = new(1, MaxDelaySeconds, 10, 3600);
    public static readonly SettingBand LeaseRetryMilliseconds = new(1, MaxLeaseRetryMilliseconds, 10, 10_000);
    public static readonly SettingBand HealthEvaluationSeconds = new(1, MaxDelaySeconds, 10, 3600);
    public static readonly SettingBand OutboxPollSeconds = new(1, MaxDelaySeconds, 10, 3600);
    public static readonly SettingBand BusyTimeoutSeconds = new(1, MaxBusySeconds, 1, 60);
    public static readonly SettingBand AttestedWorkingCopyRetentionMinutes = new(1, MaxRetentionMinutes, 1, 1440);
    /// <summary>The attestation sidecar is read into one byte array, so its size cannot exceed the longest array.</summary>
    public static readonly SettingBand AttestedManifestBytes = new(1, System.Array.MaxLength, 1, 256 * BytesPerMebibyte);
}
