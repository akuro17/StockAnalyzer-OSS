using System;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Time limits (seconds) of the package checks the embedded Python runs at start-up, bound from the "PythonPackageCheck" section of
/// appsettings.json (via <c>IStockAnalyzerSettings</c>). The defaults live here only; the interface defaults and the configuration file follow them.
/// The accepted bands are code (<see cref="PythonPackageCheckLimits"/>), never configuration.
/// </summary>
public class PythonPackageCheckSettings
{
    public const string SectionName = "PythonPackageCheck";

    /// <summary>Longest wait for the import check of one package. Heavy packages (tensorflow imports in about 5 s) must not be reported as missing because the check timed out.</summary>
    public int ImportProbeTimeoutSeconds { get; set; } = 60;

    /// <summary>Longest wait for the whole update check process of one package. A check that does not finish is a failed check, never an update.</summary>
    public int UpdateCheckTimeoutSeconds { get; set; } = 5;

    /// <summary>Longest wait for the PyPI request inside the update check script; it must end before <see cref="UpdateCheckTimeoutSeconds"/>.</summary>
    public int UpdateCheckRequestTimeoutSeconds { get; set; } = 3;

    public void Validate()
    {
        Require(nameof(ImportProbeTimeoutSeconds), ImportProbeTimeoutSeconds, PythonPackageCheckLimits.ImportProbeMinSeconds, PythonPackageCheckLimits.ImportProbeMaxSeconds);
        Require(nameof(UpdateCheckTimeoutSeconds), UpdateCheckTimeoutSeconds, PythonPackageCheckLimits.UpdateCheckMinSeconds, PythonPackageCheckLimits.UpdateCheckMaxSeconds);
        Require(nameof(UpdateCheckRequestTimeoutSeconds), UpdateCheckRequestTimeoutSeconds, PythonPackageCheckLimits.UpdateRequestMinSeconds, PythonPackageCheckLimits.UpdateRequestMaxSeconds);
        if (UpdateCheckRequestTimeoutSeconds >= UpdateCheckTimeoutSeconds)
        {
            throw new InvalidOperationException($"PythonPackageCheckSettings: {nameof(UpdateCheckRequestTimeoutSeconds)} must be smaller than {nameof(UpdateCheckTimeoutSeconds)}.");
        }
    }

    private static void Require(string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw new InvalidOperationException($"PythonPackageCheckSettings: {name} must be {min} to {max} seconds.");
        }
    }
}

/// <summary>System contract of <see cref="PythonPackageCheckSettings"/>: a configuration file cannot widen these bands.</summary>
public static class PythonPackageCheckLimits
{
    public const int ImportProbeMinSeconds = 5;
    public const int ImportProbeMaxSeconds = 600;
    public const int UpdateCheckMinSeconds = 2;
    public const int UpdateCheckMaxSeconds = 60;
    public const int UpdateRequestMinSeconds = 1;
    public const int UpdateRequestMaxSeconds = 30;
}
