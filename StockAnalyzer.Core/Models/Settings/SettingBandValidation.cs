using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// The one comparison of an operational value with a <see cref="SettingBand"/> used by the startup validators: a value outside the hard band throws
/// (the exception names the key and the limits); a value between the standard and the hard band adds one warning for the operator.
/// </summary>
internal static class SettingBandValidation
{
    public static void Check(string key, string parameterName, SettingBand band, long value, string unit, string consequence, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        if (!band.IsWithinHardLimits(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{key} must be {Format(band.HardMin)} to {Format(band.HardMax)} {unit}; the value {Format(value)} is outside the system limits.");
        }

        if (!band.IsWithinStandard(value))
        {
            warnings.Add($"{key} = {Format(value)} {unit} is outside the standard range {Format(band.StandardMin)} to {Format(band.StandardMax)}; "
                + $"the application runs but {consequence}.");
        }
    }

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
