using System;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Watchlist;
using Microsoft.Extensions.DependencyInjection;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public enum ColumnCategory
{
    Active,
    All,
    Basic,
    PriceVolume,
    Valuation,
    Ratio,
    Financial,
    Templates
}

/// <summary>
/// ViewModel representing a single column item in the Column Chooser dialog.
/// </summary>
public partial class ColumnItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isActive;

    public string MemberName { get; }
    public string HeaderKey { get; }
    public string EnglishName { get; }
    public string Description { get; }
    public string Formula { get; }
    public ColumnCategory Category { get; }

    public bool IsSymbol => MemberName == "Symbol";
    public bool IsSelect => MemberName == "IsChecked";

    private readonly string? _localizedHeader;
    public string LocalizedHeader
    {
        get
        {
            if (_localizedHeader != null) return _localizedHeader;
            if (!string.IsNullOrEmpty(HeaderKey) && App.Current?.Services?.GetService<ILocalizationService>() is ILocalizationService loc)
            {
                var val = loc.GetString(HeaderKey);
                if (!string.IsNullOrEmpty(val) && val != HeaderKey) return val;
            }
            return EnglishName;
        }
    }

    public string LocalizedCategory
    {
        get
        {
            if (App.Current?.Services?.GetService<ILocalizationService>() is ILocalizationService loc)
            {
                return StockAnalyzer.Avalonia.Common.ColumnCategoryExtensions.GetLocalizedName(Category, loc);
            }
            return Category.ToString();
        }
    }

    public ColumnItemViewModel(
        string memberName,
        string headerKey,
        bool isActive,
        ColumnCategory category,
        string englishName,
        string description,
        string formula,
        string? localizedHeader = null)
    {
        MemberName = memberName;
        HeaderKey = headerKey;
        _isActive = isActive;
        Category = category;
        EnglishName = englishName;
        Description = description;
        Formula = formula;
        _localizedHeader = localizedHeader;
    }

    public static ColumnItemViewModel Create(WatchlistColumnMetadata metadata, bool isActive, string? localizedHeader = null)
    {
        var member = metadata.MemberName;

        // If metadata passed in is partial (e.g. from unit test mocks), lookup full metadata from WatchlistColumnRegistry
        var fullMeta = metadata;
        if (metadata.DisplayName == null || metadata.Category == null)
        {
            foreach (var col in WatchlistColumnRegistry.AllColumns)
            {
                if (string.Equals(col.MemberName, member, StringComparison.OrdinalIgnoreCase))
                {
                    fullMeta = col;
                    break;
                }
            }
        }

        var categoryStr = fullMeta.Category ?? metadata.Category;
        var category = Enum.TryParse<ColumnCategory>(categoryStr, true, out var cat) ? cat : ColumnCategory.Basic;
        var englishName = fullMeta.DisplayName ?? metadata.DisplayName ?? member;
        var description = fullMeta.Description ?? metadata.Description ?? $"Description for {member}";
        var formula = fullMeta.Formula ?? metadata.Formula ?? "N/A";

        if (localizedHeader == null && !string.IsNullOrEmpty(metadata.HeaderKey) && App.Current?.Services?.GetService<ILocalizationService>() is ILocalizationService locService)
        {
            var loc = locService.GetString(metadata.HeaderKey);
            if (!string.IsNullOrEmpty(loc) && loc != metadata.HeaderKey)
            {
                localizedHeader = loc;
            }
        }

        return new ColumnItemViewModel(
            member,
            metadata.HeaderKey,
            isActive,
            category,
            englishName,
            description,
            formula,
            localizedHeader
        );
    }
}
