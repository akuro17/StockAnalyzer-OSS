using System.Collections.Generic;
using System.Collections.ObjectModel;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Helper for constructing reference options in indicator chaining and dynamic driver selection dropdowns.
/// Single Source of Truth (SSoT) for indicator reference option lists.
/// </summary>
public static class IndicatorReferenceHelper
{
    /// <summary>
    /// Populates the source indicators and dynamic period drivers collections.
    /// </summary>
    public static void PopulateReferenceOptions(
        ObservableCollection<IndicatorReferenceOption> sourceCollection,
        ObservableCollection<IndicatorReferenceOption> driverCollection,
        IEnumerable<CoreIndicatorSettings>? allIndicators,
        string? currentIndicatorId,
        string? selectedSourceId = null,
        string? selectedDriverId = null)
    {
        sourceCollection.Clear();
        driverCollection.Clear();

        var defaultSourceLabel = LocalizationManager.Instance["Label_DefaultPriceSource"] ?? "(Default / Price Source)";
        var defaultDriverLabel = LocalizationManager.Instance["Label_DefaultStaticPeriod"] ?? "(None / Static Parameter)";
        var disabledSuffix = LocalizationManager.Instance["Suffix_Disabled"] ?? " (Disabled)";
        var periodNativeSuffix = LocalizationManager.Instance["Suffix_PeriodNative"] ?? " [Period-native]";

        sourceCollection.Add(new IndicatorReferenceOption { Id = null, DisplayName = defaultSourceLabel });
        driverCollection.Add(new IndicatorReferenceOption { Id = null, DisplayName = defaultDriverLabel });

        if (allIndicators != null)
        {
            foreach (var ind in allIndicators)
            {
                bool isSelf = ind.Id == currentIndicatorId;
                bool isReferenced = ind.Id == selectedSourceId || ind.Id == selectedDriverId;

                if (!isSelf && !string.IsNullOrEmpty(ind.DisplayName) && (ind.IsEnabled || isReferenced))
                {
                    string suffix = ind.IsEnabled ? string.Empty : disabledSuffix;
                    bool isPeriodNative = AdaptiveSmoothingHelper.IsPeriodNativeDriverType(ind.TypeEnum);
                    string nativeTag = isPeriodNative ? periodNativeSuffix : string.Empty;

                    sourceCollection.Add(new IndicatorReferenceOption
                    {
                        Id = ind.Id,
                        DisplayName = $"{ind.DisplayName}{suffix}"
                    });
                    driverCollection.Add(new IndicatorReferenceOption
                    {
                        Id = ind.Id,
                        DisplayName = $"{ind.DisplayName}{suffix}{nativeTag}"
                    });
                }
            }
        }
    }
}