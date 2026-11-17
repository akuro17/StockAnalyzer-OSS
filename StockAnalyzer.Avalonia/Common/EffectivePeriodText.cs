using System;
using System.Collections.Generic;
using System.Globalization;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Localised text of the effective dynamic period shown in the Data Window and the chart information tooltip.
/// The number comes from <see cref="EffectiveDynamicPeriodDisplay"/> (Core); only the row label and the unit word are localised here.
/// </summary>
internal static class EffectivePeriodText
{
    /// <summary>Locale key of the row label ("Effective Period").</summary>
    public const string RowLabelKey = "DataWindow_Row_EffectivePeriod";

    /// <summary>Locale key of the unit word after the number ("bars").</summary>
    public const string BarsUnitKey = "DataWindow_Unit_Bars";

    /// <summary>The localised row label.</summary>
    public static string Label(Func<string, string> localize) => localize(RowLabelKey);

    /// <summary>"N bars" (unit localised) for the period at <paramref name="index"/>, or <see cref="EffectiveDynamicPeriodDisplay.UnavailableText"/> when unavailable.</summary>
    public static string Format(IReadOnlyList<decimal?> periods, int index, Func<string, string> localize)
    {
        int? period = EffectiveDynamicPeriodDisplay.GetEffectivePeriod(periods, index);
        return period.HasValue
            ? string.Concat(period.Value.ToString(CultureInfo.InvariantCulture), " ", localize(BarsUnitKey))
            : EffectiveDynamicPeriodDisplay.UnavailableText;
    }
}
