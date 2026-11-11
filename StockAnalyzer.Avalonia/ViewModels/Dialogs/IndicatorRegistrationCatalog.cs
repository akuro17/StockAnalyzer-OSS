using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// The pure parts shared by the two indicator registration dialogs (dynamic period driver and source indicator): building the
/// catalog items and formatting the preview name. Filtering, selection and default-settings creation stay in the view models, where
/// the two dialogs intentionally keep their own behaviour.
/// </summary>
internal static class IndicatorRegistrationCatalog
{
    /// <summary>
    /// One catalog item per registered type except <see cref="IndicatorType.Price"/> (the price list is separate), ordered by
    /// <see cref="IndicatorCatalogItem.ShortName"/> with the ordinal (code-unit) comparison, so the order does not depend on the user's
    /// culture (upper-case acronyms such as ADX sort before PascalCase names such as AccelerationOscillator). The category comes from the static default settings and is
    /// <see cref="CoreIndicatorCategory.Other"/> when the type has no static default.
    /// </summary>
    internal static List<IndicatorCatalogItem> BuildItems(IEnumerable<IndicatorType> registeredTypes, IReadOnlyList<CoreIndicatorSettings> staticDefaults)
    {
        var items = new List<IndicatorCatalogItem>();

        foreach (var type in registeredTypes)
        {
            if (type == IndicatorType.Price) continue;

            var defaultSetting = staticDefaults.FirstOrDefault(s => s.TypeEnum == type);
            var category = defaultSetting?.Category ?? CoreIndicatorCategory.Other;

            items.Add(new IndicatorCatalogItem
            {
                Type = type,
                DisplayName = type.GetDescription(),
                ShortName = type.ToString(),
                Category = category
            });
        }

        return items.OrderBy(i => i.ShortName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The preview name of the edited settings: empty without settings; the price label for a price source; otherwise the indicator name
    /// with its parameters (short form: type name, no space before the parenthesis), followed by the selected output in parentheses when
    /// the indicator has several outputs and the selection is neither the main series nor the first output.
    /// </summary>
    internal static string FormatPreviewName(
        CoreIndicatorSettings? settings,
        bool useShortName,
        bool hasMultipleOutputs,
        string? selectedOutput,
        IReadOnlyList<string> availableOutputs)
    {
        if (settings == null)
        {
            return string.Empty;
        }

        if (settings.TypeEnum == IndicatorType.Price)
        {
            return useShortName
                ? settings.PriceSource.ToString()
                : PriceDataHelper.FormatPriceTypeLabel(settings.PriceSource);
        }

        string baseName = useShortName
            ? (settings.TypeEnum?.ToString() ?? "Unknown")
            : (settings.TypeEnum?.GetDescription() ?? "Unknown");

        string formattedName;
        if (settings.ParameterObject != null)
        {
            string paramName = settings.ParameterObject.GetDisplayName(baseName);
            formattedName = useShortName ? paramName.Replace(" (", "(") : paramName;
        }
        else
        {
            formattedName = baseName;
        }

        if (hasMultipleOutputs &&
            !string.IsNullOrEmpty(selectedOutput) &&
            selectedOutput != IndicatorResult.MainSeriesName &&
            (availableOutputs.Count == 0 || selectedOutput != availableOutputs[0]))
        {
            return $"{formattedName} ({selectedOutput})";
        }

        return formattedName;
    }
}
