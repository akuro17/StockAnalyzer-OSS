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
        string? selectedDriverId = null,
        IEnumerable<CoreIndicatorSettings>? registeredSourceIndicators = null,
        IEnumerable<CoreIndicatorSettings>? registeredDrivers = null)
    {
        sourceCollection.Clear();
        driverCollection.Clear();

        var defaultSourceLabel = LocalizationManager.Instance["Label_DefaultPriceSource"] ?? "(Default / Price Type)";
        var defaultDriverLabel = LocalizationManager.Instance["Label_DefaultStaticPeriod"] ?? "(None / Static Parameter)";
        var disabledSuffix = LocalizationManager.Instance["Suffix_Disabled"] ?? " (Disabled)";
        var periodNativeSuffix = LocalizationManager.Instance["Suffix_PeriodNative"] ?? " [Period-native]";

        sourceCollection.Add(new IndicatorReferenceOption { Id = null, DisplayName = defaultSourceLabel, IsOverlay = true });
        driverCollection.Add(new IndicatorReferenceOption { Id = null, DisplayName = defaultDriverLabel, IsOverlay = true });

        var sourceAddedIds = new HashSet<string>();
        var driverAddedIds = new HashSet<string>();

        // 1. Upper tier: Active chart indicators
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
                        DisplayName = $"{ind.DisplayName}{suffix}",
                        IsOverlay = ind.IsOverlay,
                        OverlayPanelId = ind.OverlayPanelId
                    });
                    driverCollection.Add(new IndicatorReferenceOption
                    {
                        Id = ind.Id,
                        DisplayName = $"{ind.DisplayName}{suffix}{nativeTag}",
                        IsOverlay = ind.IsOverlay,
                        OverlayPanelId = ind.OverlayPanelId
                    });

                    if (!string.IsNullOrEmpty(ind.Id))
                    {
                        sourceAddedIds.Add(ind.Id);
                        driverAddedIds.Add(ind.Id);
                    }
                }
            }
        }

        // 2. Lower tier for sourceCollection: Registered Source Indicators
        if (registeredSourceIndicators != null)
        {
            foreach (var ind in registeredSourceIndicators)
            {
                if (string.IsNullOrEmpty(ind.Id) || ind.Id == currentIndicatorId || sourceAddedIds.Contains(ind.Id))
                {
                    continue;
                }

                string menuDisplayName = ind.GetFormattedDisplayName();
                if (string.IsNullOrEmpty(menuDisplayName))
                {
                    menuDisplayName = ind.DisplayName;
                }

                if (!string.IsNullOrEmpty(menuDisplayName))
                {
                    sourceCollection.Add(new IndicatorReferenceOption
                    {
                        Id = ind.Id,
                        DisplayName = menuDisplayName,
                        IsOverlay = ind.IsOverlay,
                        OverlayPanelId = ind.OverlayPanelId
                    });

                    sourceAddedIds.Add(ind.Id);

                    // Backward compatibility: If registeredDrivers was omitted, also populate driverCollection as before
                    if (registeredDrivers == null && !driverAddedIds.Contains(ind.Id))
                    {
                        bool isPeriodNative = AdaptiveSmoothingHelper.IsPeriodNativeDriverType(ind.TypeEnum);
                        string nativeTag = isPeriodNative ? periodNativeSuffix : string.Empty;

                        driverCollection.Add(new IndicatorReferenceOption
                        {
                            Id = ind.Id,
                            DisplayName = $"{menuDisplayName}{nativeTag}",
                            IsOverlay = ind.IsOverlay,
                            OverlayPanelId = ind.OverlayPanelId
                        });

                        driverAddedIds.Add(ind.Id);
                    }
                }
            }
        }

        // 3. Lower tier for driverCollection: Registered Dynamic Period Drivers
        if (registeredDrivers != null)
        {
            foreach (var ind in registeredDrivers)
            {
                if (string.IsNullOrEmpty(ind.Id) || ind.Id == currentIndicatorId || driverAddedIds.Contains(ind.Id))
                {
                    continue;
                }

                string menuDisplayName = ind.GetFormattedDisplayName();
                if (string.IsNullOrEmpty(menuDisplayName))
                {
                    menuDisplayName = ind.DisplayName;
                }

                if (!string.IsNullOrEmpty(menuDisplayName))
                {
                    bool isPeriodNative = AdaptiveSmoothingHelper.IsPeriodNativeDriverType(ind.TypeEnum);
                    string nativeTag = isPeriodNative ? periodNativeSuffix : string.Empty;

                    driverCollection.Add(new IndicatorReferenceOption
                    {
                        Id = ind.Id,
                        DisplayName = $"{menuDisplayName}{nativeTag}",
                        IsOverlay = ind.IsOverlay,
                        OverlayPanelId = ind.OverlayPanelId
                    });

                    driverAddedIds.Add(ind.Id);
                }
            }
        }
    }
}