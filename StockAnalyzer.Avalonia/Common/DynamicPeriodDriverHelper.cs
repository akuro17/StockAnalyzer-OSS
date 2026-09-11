using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Single Source of Truth for the HasDynamicPeriodDriver / HiddenParameterTags computed-property pair,
/// shared by IndicatorSettingsDialogViewModel and IndicatorPropertiesViewModel.
/// </summary>
public static class DynamicPeriodDriverHelper
{
    /// <summary>
    /// Whether the given indicator currently has a dynamic period driver assigned.
    /// </summary>
    public static bool GetHasDynamicPeriodDriver(CoreIndicatorSettings? settings) =>
        settings != null && !string.IsNullOrEmpty(settings.DynamicPeriodIndicatorId);

    /// <summary>
    /// Whether the given parameter object exposes a period-related category or property name that can be
    /// dynamically modulated. SSoT for gating Dynamic Period Driver capability across setting dialogs.
    /// </summary>
    public static bool GetSupportsDynamicPeriod(object? parameterObject)
    {
        if (parameterObject == null) return false;

        var properties = parameterObject.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var p in properties)
        {
            var cat = p.GetCustomAttribute<CategoryAttribute>()?.Category?.Trim();
            if (!string.IsNullOrEmpty(cat))
            {
                if (string.Equals(cat, "Periods", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "ROC Periods", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "SMA Periods", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "Waveform", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "Smoothing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "Moving Average Cross", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "EMA", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cat, "MACD", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var name = p.Name;
            if (name.EndsWith("Period", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Sample", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Period", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "KPeriod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "DPeriod", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Slowing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parameter tags to hide in the reflection-based settings panel. Always empty: Period stays
    /// visible/editable even while a Dynamic Period Driver is active, because
    /// AdaptiveSmoothingHelper.CalculateAdaptiveSma falls back to the static Period value (as
    /// defaultPeriod) for bars where the driver has no value yet (e.g. its own warm-up window),
    /// so the static Period still affects calculation results and must stay user-editable. The
    /// hasDynamicPeriodDriver parameter is kept for call-site compatibility with
    /// IndicatorSettingsDialogViewModel/IndicatorPropertiesViewModel. ParameterViewBuilder's
    /// generic hiddenTags parameter is kept as an extensibility point for a possible future tag.
    /// </summary>
    public static IReadOnlyCollection<string> GetHiddenParameterTags(bool hasDynamicPeriodDriver) =>
        Array.Empty<string>();

    /// <summary>
    /// Applies a HasDynamicPeriodDriver checkbox toggle to settings.DynamicPeriodIndicatorId: turning it on
    /// auto-selects the first available driver (if any); turning it off clears the driver id. Returns false
    /// (leaving settings unchanged) when turning on but no driver candidate is available, so the caller can
    /// still raise PropertyChanged for the checkbox to re-sync its visual state with the unchanged getter value.
    /// </summary>
    public static bool TrySetHasDynamicPeriodDriver(
        CoreIndicatorSettings settings,
        ObservableCollection<IndicatorReferenceOption> availableDrivers,
        bool value)
    {
        if (value)
        {
            if (string.IsNullOrEmpty(settings.DynamicPeriodIndicatorId))
            {
                var firstDriver = availableDrivers.FirstOrDefault(o => o.Id != null);
                if (firstDriver == null) return false;
                settings.DynamicPeriodIndicatorId = firstDriver.Id;
            }
        }
        else
        {
            settings.DynamicPeriodIndicatorId = null;
        }

        return true;
    }
}
