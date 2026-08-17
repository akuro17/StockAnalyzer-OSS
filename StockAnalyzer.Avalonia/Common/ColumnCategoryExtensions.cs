using System;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Extension methods for ColumnCategory enum.
/// </summary>
public static class ColumnCategoryExtensions
{
    public static string GetResourceKey(this ColumnCategory category)
    {
        return category switch
        {
            ColumnCategory.Active => "ColumnChooser_Category_Active",
            ColumnCategory.All => "ColumnChooser_Category_All",
            ColumnCategory.Basic => "ColumnChooser_Category_Basic",
            ColumnCategory.PriceVolume => "ColumnChooser_Category_PriceVolume",
            ColumnCategory.Valuation => "ColumnChooser_Category_Valuation",
            ColumnCategory.Ratio => "ColumnChooser_Category_Profitability",
            ColumnCategory.Financial => "ColumnChooser_Category_FinancialHealth",
            ColumnCategory.Templates => "ColumnChooser_Category_Templates",
            _ => $"ColumnChooser_Category_{category}"
        };
    }

    public static string GetLocalizedName(this ColumnCategory category, ILocalizationService localizationService)
    {
        if (localizationService == null) throw new ArgumentNullException(nameof(localizationService));
        var resourceKey = category.GetResourceKey();
        var localized = localizationService.GetString(resourceKey);
        return localized ?? category.ToString();
    }
}
