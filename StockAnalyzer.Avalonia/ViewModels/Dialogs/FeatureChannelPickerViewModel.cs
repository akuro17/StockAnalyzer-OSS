using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// One row of the composed feature list: either a raw price channel or a configured indicator
/// channel, together with the per-channel <see cref="ChannelNormalization"/> the user picked for it.
/// </summary>
public partial class FeatureChannelRowViewModel : ObservableObject
{
    /// <summary>Discriminator, mirrored from <see cref="FeatureChannel.Kind"/>.</summary>
    public FeatureChannelKind Kind { get; init; }

    /// <summary>Price series for a <see cref="FeatureChannelKind.Price"/> row.</summary>
    public PriceType? Price { get; init; }

    /// <summary>Indicator type for a <see cref="FeatureChannelKind.Indicator"/> row.</summary>
    public IndicatorType? Indicator { get; init; }

    /// <summary>Human-readable label shown in the list. Mutable: refreshed in place when the row's
    /// indicator parameters are edited via the "Selected" nav entry's detail-column editor.</summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>Captured indicator parameters (invariant strings), empty for a price row. Mutable for
    /// the same reason as <see cref="Label"/> - kept in sync with live parameter edits.</summary>
    public IReadOnlyDictionary<string, string> Params { get; set; } = new Dictionary<string, string>();

    /// <summary>Per-channel normalization; editable in the list.</summary>
    [ObservableProperty]
    private ChannelNormalization _normalization = ChannelNormalization.None;

    /// <summary>Materializes this row as a wire <see cref="FeatureChannel"/>.</summary>
    public FeatureChannel ToChannel() => new()
    {
        Kind = Kind,
        Price = Price,
        Indicator = Indicator,
        Params = Params,
        Normalization = Normalization,
    };

    /// <summary>
    /// Rebuilds a row from a wire <see cref="FeatureChannel"/> (template load/append/preview). Label
    /// formatting mirrors <see cref="FeatureChannelPickerViewModel.AddIndicatorChannel"/> exactly so a
    /// loaded row looks identical to one added interactively - which requires reconstructing the live
    /// parameter object (registry defaults + <see cref="FeatureChannel.Params"/> applied) via
    /// <paramref name="factory"/>, since <see cref="FeatureChannelConverter.BuildIndicatorLabel"/> needs
    /// every current numeric value, not just the diff-only <see cref="FeatureChannel.Params"/>.
    /// </summary>
    public static FeatureChannelRowViewModel FromChannel(FeatureChannel channel, IIndicatorFactory factory)
    {
        string label;
        if (channel.Kind == FeatureChannelKind.Price && channel.Price is { } price)
        {
            label = FeatureChannelPickerViewModel.FormatPriceTypeLabel(price);
        }
        else if (channel.Kind == FeatureChannelKind.Indicator && channel.Indicator is { } indicator)
        {
            try
            {
                var settings = FeatureChannelConverter.BuildIndicatorSettings(channel, factory, out _);
                label = FeatureChannelConverter.BuildIndicatorLabel(indicator.ToString(), settings.ParameterObject);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                label = indicator.ToString();
            }
        }
        else
        {
            label = channel.Kind.ToString();
        }

        return new FeatureChannelRowViewModel
        {
            Kind = channel.Kind,
            Price = channel.Price,
            Indicator = channel.Indicator,
            Params = channel.Params,
            Label = label,
            Normalization = channel.Normalization,
        };
    }
}

/// <summary>
/// Which nav entry drives the catalog(middle)/detail(right) columns of the unified picker layout.
/// Mirrors <c>IndicatorSettingsDialogViewModel</c>'s Library-mode nav, extended with two extra
/// entries (<see cref="Price"/>, <see cref="Selected"/>) per the 2026-09 unification request.
/// </summary>
public enum FeaturePickerMode
{
    /// <summary>A <see cref="CoreIndicatorCategory"/> (or "All Categories") is selected; catalog/detail show the indicator library.</summary>
    Category,
    /// <summary>The "Price" nav entry is selected; catalog/detail show the addable O/H/L/C fields.</summary>
    Price,
    /// <summary>The "Selected" nav entry is selected; catalog/detail show the composed channel list and its per-row editor.</summary>
    Selected,
    /// <summary>The "Templates" nav entry is selected; catalog/detail show saved templates and a preview.</summary>
    Templates,
}

/// <summary>One selectable raw price field row in the Price-mode catalog list.</summary>
public sealed class PriceFieldCatalogItem
{
    public required PriceType Field { get; init; }
    public required string Label { get; init; }
}

/// <summary>The single formatting rule for the picker's "{channel name} added" confirmation toast,
/// shared by the indicator-add and price-field-add paths so the two cannot drift apart.</summary>
internal static class FeaturePickerToast
{
    public static string Added(string name) => $"{name} {LocalizationManager.Instance["Msg_Added"] ?? "Added"}";
}

/// <summary>
/// The raw O/H/L/C price-channel concern of <see cref="FeatureChannelPickerViewModel"/> (SRP
/// extraction, TemplateCrudConsolidation Task #5 / S5-1): the four per-field include flags, the
/// selected addable field, and the rules that keep each flag in lockstep with exactly one row in
/// the picker's shared <c>Channels</c> list. Every member is re-exposed unchanged by the parent
/// ViewModel, so bindings (<c>FeatureChannelPickerView.axaml</c>) and tests are unaffected. The
/// logic here is a verbatim move of the former inline implementation - no behavioral change.
/// </summary>
internal sealed partial class PriceChannelSection : ObservableObject
{
    private readonly ObservableCollection<FeatureChannelRowViewModel> _channels;
    private readonly Func<ChannelNormalization> _nextNormalization;
    private readonly Action _notifyHasChannels;
    private readonly Action<string> _onFieldAdded;

    /// <param name="channels">The parent's channel list; price rows are added to / removed from it here.</param>
    /// <param name="nextNormalization">Reads the parent's "normalization for the next added channel".</param>
    /// <param name="notifyHasChannels">Invoked after a price row is added or removed so the parent can raise <c>HasChannels</c>.</param>
    /// <param name="onFieldAdded">Invoked with the field label when <see cref="AddPriceChannel"/> turns a field on, for the parent's "X Added" toast.</param>
    public PriceChannelSection(
        ObservableCollection<FeatureChannelRowViewModel> channels,
        Func<ChannelNormalization> nextNormalization,
        Action notifyHasChannels,
        Action<string> onFieldAdded)
    {
        _channels = channels;
        _nextNormalization = nextNormalization;
        _notifyHasChannels = notifyHasChannels;
        _onFieldAdded = onFieldAdded;
    }

    [ObservableProperty] private bool _includeOpen;
    [ObservableProperty] private bool _includeHigh;
    [ObservableProperty] private bool _includeLow;
    [ObservableProperty] private bool _includeClose;

    /// <summary>Field currently selected in the Price-mode catalog list.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPriceChannelCommand))]
    private PriceFieldCatalogItem? _selectedPriceField;

    public bool IsPriceFieldIncluded(PriceType field) =>
        _channels.Any(c => c.Kind == FeatureChannelKind.Price && c.Price == field);

    private bool CanAddPriceChannel() => SelectedPriceField is not null && !IsPriceFieldIncluded(SelectedPriceField.Field);

    /// <summary>Adds the selected raw price field as a channel, same operation as <c>AddIndicatorChannel</c> for indicators.</summary>
    [RelayCommand(CanExecute = nameof(CanAddPriceChannel))]
    private void AddPriceChannel()
    {
        if (SelectedPriceField is not { } item)
        {
            return;
        }

        TogglePriceChannel(item.Field, true);
        _onFieldAdded(item.Label);
    }

    partial void OnIncludeOpenChanged(bool value) => TogglePriceChannel(PriceType.Open, value);
    partial void OnIncludeHighChanged(bool value) => TogglePriceChannel(PriceType.High, value);
    partial void OnIncludeLowChanged(bool value) => TogglePriceChannel(PriceType.Low, value);
    partial void OnIncludeCloseChanged(bool value) => TogglePriceChannel(PriceType.Close, value);

    private void TogglePriceChannel(PriceType field, bool include)
    {
        var existing = _channels.FirstOrDefault(c => c.Kind == FeatureChannelKind.Price && c.Price == field);
        if (include && existing is null)
        {
            _channels.Add(new FeatureChannelRowViewModel
            {
                Kind = FeatureChannelKind.Price,
                Price = field,
                Label = FeatureChannelPickerViewModel.FormatPriceTypeLabel(field),
                Normalization = _nextNormalization(),
            });
            SyncIncludeProperty(field, true);
            _notifyHasChannels();
        }
        else if (!include && existing is not null)
        {
            _channels.Remove(existing);
            SyncIncludeProperty(field, false);
            _notifyHasChannels();
        }

        AddPriceChannelCommand.NotifyCanExecuteChanged();
    }

    private void SyncIncludeProperty(PriceType field, bool value)
    {
        switch (field)
        {
            case PriceType.Open when _includeOpen != value:
                _includeOpen = value;
                OnPropertyChanged(nameof(IncludeOpen));
                break;
            case PriceType.High when _includeHigh != value:
                _includeHigh = value;
                OnPropertyChanged(nameof(IncludeHigh));
                break;
            case PriceType.Low when _includeLow != value:
                _includeLow = value;
                OnPropertyChanged(nameof(IncludeLow));
                break;
            case PriceType.Close when _includeClose != value:
                _includeClose = value;
                OnPropertyChanged(nameof(IncludeClose));
                break;
        }
    }

    /// <summary>
    /// Sets an include flag without going through <see cref="TogglePriceChannel"/>, so the caller
    /// (channel-row removal, template load/append) can rebuild the <c>Channels</c> list itself
    /// without this section's <c>OnIncludeXChanged</c> re-entrancy adding a duplicate row.
    /// </summary>
    public void SetPriceToggleSilently(PriceType field, bool value)
    {
        SyncIncludeProperty(field, value);
        AddPriceChannelCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// The indicator-catalog + filter concern of <see cref="FeatureChannelPickerViewModel"/> (SRP
/// extraction, TemplateCrudConsolidation Task #5 / S5-2): the full registered-indicator list, the
/// derived category list, the search / category / selection state, and the editable default
/// settings for the selected item. Every member is re-exposed unchanged by the parent ViewModel;
/// the logic is a verbatim move of the former inline implementation - no behavioral change. The
/// one cross-concern rule ("picking a category snaps the nav back to Category mode") is delegated
/// out through <paramref name="onCategoryPicked"/> so this section holds no nav state.
/// </summary>
internal sealed partial class IndicatorCatalogSection : ObservableObject
{
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly Action _onCategoryPicked;

    private ObservableCollection<IndicatorCatalogItem> SortedCatalogItems { get; } = new();

    public ObservableCollection<IndicatorCatalogItem> AllCatalogItems { get; } = new();
    public ObservableCollection<IndicatorCatalogItem> FilteredCatalogItems { get; } = new();
    public ObservableCollection<CoreIndicatorCategory> Categories { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CoreIndicatorCategory? _selectedCategory;

    [ObservableProperty]
    private IndicatorCatalogItem? _selectedCatalogItem;

    /// <summary>Editable default settings for the selected catalog item, bound to the parameter editor.</summary>
    [ObservableProperty]
    private CoreIndicatorSettings? _selectedIndicatorSettings;

    /// <param name="indicatorFactory">Source of the registered indicator types and their default settings.</param>
    /// <param name="onCategoryPicked">Invoked when a real category is selected, so the parent can force <c>Mode = Category</c>.</param>
    public IndicatorCatalogSection(IIndicatorFactory indicatorFactory, Action onCategoryPicked)
    {
        _indicatorFactory = indicatorFactory;
        _onCategoryPicked = onCategoryPicked;

        foreach (var type in _indicatorFactory.GetRegisteredTypes())
        {
            var settings = _indicatorFactory.Create(type)?.GetDefaultSettings();
            AllCatalogItems.Add(new IndicatorCatalogItem
            {
                Type = type,
                DisplayName = type.GetDescription(),
                ShortName = type.ToString(),
                Category = settings?.Category ?? CoreIndicatorCategory.Other,
            });
        }

        foreach (var item in AllCatalogItems.OrderBy(i => i.ShortName).ToList())
        {
            SortedCatalogItems.Add(item);
        }
        AllCatalogItems.Clear();
        foreach (var item in SortedCatalogItems)
        {
            AllCatalogItems.Add(item);
        }

        foreach (var category in AllCatalogItems.Select(i => i.Category).Distinct().OrderBy(c => c))
        {
            Categories.Add(category);
        }

        ApplyFilters();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    // Half of the asymmetric invariant that the parent's OnModeChanged completes:
    //   SelectedCategory != null  =>  Mode == Category   (enforced here via onCategoryPicked)
    //   Mode != Category          =>  SelectedCategory == null   (enforced by the parent)
    partial void OnSelectedCategoryChanged(CoreIndicatorCategory? value)
    {
        // Only force Category mode when a real category was picked. When value is null this may be
        // the parent's own side-effect clear (switching into Price/Selected/Templates) - forcing
        // Mode back to Category there would immediately undo that switch.
        if (value != null)
        {
            _onCategoryPicked();
        }
        ApplyFilters();
    }

    partial void OnSelectedCatalogItemChanged(IndicatorCatalogItem? value)
    {
        if (value is null)
        {
            SelectedIndicatorSettings = null;
            return;
        }

        var settings = _indicatorFactory.Create(value.Type)?.GetDefaultSettings()
                       ?? new CoreIndicatorSettings { TypeEnum = value.Type, IsEnabled = true };
        settings.TypeEnum = value.Type;
        SelectedIndicatorSettings = settings;
    }

    public void ApplyFilters()
    {
        FilteredCatalogItems.Clear();

        IEnumerable<IndicatorCatalogItem> filtered = AllCatalogItems;

        if (SelectedCategory.HasValue)
        {
            filtered = filtered.Where(i => i.Category == SelectedCategory.Value);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            filtered = filtered.Where(i =>
                i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                i.ShortName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in filtered)
        {
            FilteredCatalogItems.Add(item);
        }

        if (SelectedCatalogItem is null || !FilteredCatalogItems.Contains(SelectedCatalogItem))
        {
            SelectedCatalogItem = FilteredCatalogItems.FirstOrDefault();
        }
    }
}

/// <summary>
/// The nav-entry / mode concern of <see cref="FeatureChannelPickerViewModel"/> (SRP extraction,
/// TemplateCrudConsolidation Task #5 / S5-3): which of the four mutually exclusive nav entries
/// (a category / "All Categories", Price, Selected, Templates) drives the catalog and detail
/// columns, plus the per-mode visibility flags the view binds. Verbatim move of the former inline
/// implementation - the parent re-exposes every member under its original name. The single
/// cross-concern rule ("leaving Category mode clears the category filter") is delegated out through
/// <paramref name="onLeftCategoryMode"/> so this section holds no catalog state.
/// </summary>
internal sealed partial class NavigationSection : ObservableObject
{
    private readonly Action _onLeftCategoryMode;

    /// <param name="onLeftCategoryMode">
    /// Invoked whenever <see cref="Mode"/> settles on a non-Category entry (and by
    /// <see cref="ClearCategory"/>), so the parent can null out the category filter it owns.
    /// </param>
    public NavigationSection(Action onLeftCategoryMode) => _onLeftCategoryMode = onLeftCategoryMode;

    /// <summary>
    /// Which nav entry currently drives the catalog/detail columns. Setting this to anything other
    /// than <see cref="FeaturePickerMode.Category"/> clears the owning picker's <c>SelectedCategory</c>,
    /// and picking a category (or "All Categories") snaps this back to <see cref="FeaturePickerMode.Category"/> —
    /// the four nav entries (Selected, Price, a category / All Categories, Templates) are mutually exclusive.
    /// </summary>
    [ObservableProperty]
    private FeaturePickerMode _mode = FeaturePickerMode.Category;

    public bool IsTemplatesCategory => Mode == FeaturePickerMode.Templates;
    public bool IsNotTemplatesCategory => !IsTemplatesCategory;
    public bool IsPriceCategory => Mode == FeaturePickerMode.Price;
    public bool IsSelectedCategory => Mode == FeaturePickerMode.Selected;

    /// <summary>Whether the catalog/detail columns should show the normal indicator library (a category or "All Categories").</summary>
    public bool IsIndicatorCategory => Mode == FeaturePickerMode.Category;

    // Completes the asymmetric invariant begun in IndicatorCatalogSection.OnSelectedCategoryChanged:
    //   SelectedCategory != null  =>  Mode == Category
    //   Mode != Category          =>  SelectedCategory == null
    partial void OnModeChanged(FeaturePickerMode value)
    {
        if (value != FeaturePickerMode.Category)
        {
            _onLeftCategoryMode();
        }

        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
        OnPropertyChanged(nameof(IsPriceCategory));
        OnPropertyChanged(nameof(IsSelectedCategory));
        OnPropertyChanged(nameof(IsIndicatorCategory));
    }

    /// <summary>Clears the category filter — the "All Categories" nav entry.</summary>
    [RelayCommand]
    private void ClearCategory()
    {
        // Former inline order was "SelectedCategory = null; Mode = Category"; the callback is the
        // "SelectedCategory = null" half, still run before the mode is (re)asserted to Category.
        _onLeftCategoryMode();
        Mode = FeaturePickerMode.Category;
    }

    /// <summary>Switches the catalog/detail columns to the "Price" nav entry.</summary>
    [RelayCommand]
    private void SelectPrice() => Mode = FeaturePickerMode.Price;

    /// <summary>Switches the catalog/detail columns to the "Selected" nav entry.</summary>
    [RelayCommand]
    private void SelectSelectedChannels() => Mode = FeaturePickerMode.Selected;

    /// <summary>Switches the catalog/detail columns to the Templates nav entry.</summary>
    [RelayCommand]
    private void SelectTemplates() => Mode = FeaturePickerMode.Templates;
}

/// <summary>
/// The composed-channel-list concern of <see cref="FeatureChannelPickerViewModel"/> (SRP
/// extraction, TemplateCrudConsolidation Task #5 / S5-4): the ordered <c>Channels</c> collection
/// that every other concern reads or mutates, the row currently open in the "Selected" nav entry's
/// detail editor together with its live indicator settings, the add / remove / reorder commands,
/// and the final <see cref="BuildFeatureSpec"/> materialization. Verbatim move of the former inline
/// implementation - the parent re-exposes every member under its original name so
/// <c>FeatureChannelPickerView.axaml</c>'s compiled bindings and the existing tests are unaffected.
/// Cross-concern inputs (the catalog's current selection, the normalization for the next added
/// channel, the price-toggle desync fix-up on Price-row removal, the toast sink) are supplied as
/// delegates so this section references neither the parent nor its sibling sections.
/// </summary>
internal sealed partial class ChannelListSection : ObservableObject
{
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly Func<IndicatorCatalogItem?> _selectedCatalogItem;
    private readonly Func<CoreIndicatorSettings?> _selectedIndicatorSettings;
    private readonly Func<ChannelNormalization> _nextNormalization;
    private readonly Action<PriceType> _onPriceRowRemoved;
    private readonly Action<string> _showToast;

    /// <param name="indicatorFactory">Used to rebuild live parameter objects for in-place label refresh.</param>
    /// <param name="selectedCatalogItem">Reads the indicator catalog's current selection for <see cref="AddIndicatorChannel"/>.</param>
    /// <param name="selectedIndicatorSettings">Reads the catalog's editable default settings for <see cref="AddIndicatorChannel"/>.</param>
    /// <param name="nextNormalization">Reads the "normalization for the next added channel".</param>
    /// <param name="onPriceRowRemoved">Invoked when a Price row is removed so the owner can clear the matching include flag without re-adding the row.</param>
    /// <param name="showToast">Sink for the "X Added" and parameter-warning toasts.</param>
    public ChannelListSection(
        IIndicatorFactory indicatorFactory,
        Func<IndicatorCatalogItem?> selectedCatalogItem,
        Func<CoreIndicatorSettings?> selectedIndicatorSettings,
        Func<ChannelNormalization> nextNormalization,
        Action<PriceType> onPriceRowRemoved,
        Action<string> showToast)
    {
        _indicatorFactory = indicatorFactory;
        _selectedCatalogItem = selectedCatalogItem;
        _selectedIndicatorSettings = selectedIndicatorSettings;
        _nextNormalization = nextNormalization;
        _onPriceRowRemoved = onPriceRowRemoved;
        _showToast = showToast;
    }

    /// <summary>The composed channels, in list order. Shared SSoT: the price and template concerns add to / remove from this same instance.</summary>
    public ObservableCollection<FeatureChannelRowViewModel> Channels { get; } = new();

    /// <summary>Whether the current selection would compose a non-empty, valid <see cref="FeatureSpec"/>.</summary>
    public bool HasChannels => Channels.Count > 0;

    /// <summary>
    /// Raises <see cref="HasChannels"/> after a caller-side batch mutation of <see cref="Channels"/>
    /// (template load / append), matching the former inline code's explicit notify at those points.
    /// </summary>
    public void RaiseHasChannels() => OnPropertyChanged(nameof(HasChannels));

    /// <summary>Row currently selected in the "Selected" nav entry's catalog list, edited via the detail column.</summary>
    [ObservableProperty]
    private FeatureChannelRowViewModel? _selectedChannel;

    /// <summary>
    /// Live, editable settings for <see cref="SelectedChannel"/> when it is an indicator row - built
    /// from the row's captured <see cref="FeatureChannelRowViewModel.Params"/> via
    /// <see cref="FeatureChannelConverter.BuildIndicatorSettings"/> so the detail column can offer the
    /// same per-parameter editor (<c>DynamicIndicatorSettingsView</c>) as the "Indicator" nav entries.
    /// Null for a price row or when nothing is selected.
    /// </summary>
    [ObservableProperty]
    private CoreIndicatorSettings? _selectedChannelSettings;

    /// <summary>Picks a row in the "Selected" nav entry's catalog list for editing in the detail column.</summary>
    [RelayCommand]
    private void SelectChannel(FeatureChannelRowViewModel? row) => SelectedChannel = row;

    partial void OnSelectedChannelChanged(FeatureChannelRowViewModel? value)
    {
        if (SelectedChannelSettings?.ParameterObject is { } previousParams)
        {
            previousParams.PropertyChanged -= OnSelectedChannelParameterChanged;
        }

        CoreIndicatorSettings? settings = null;
        if (value is { Kind: FeatureChannelKind.Indicator })
        {
            try
            {
                settings = FeatureChannelConverter.BuildIndicatorSettings(value.ToChannel(), _indicatorFactory, out var warnings);
                if (warnings.Count > 0)
                {
                    _showToast(string.Join(" / ", warnings));
                }
            }
            catch (InvalidOperationException)
            {
                settings = null;
            }
        }

        SelectedChannelSettings = settings;

        if (settings?.ParameterObject is { } paramObj)
        {
            paramObj.PropertyChanged += OnSelectedChannelParameterChanged;
        }
    }

    /// <summary>
    /// Re-derives the edited row's <see cref="FeatureChannelRowViewModel.Params"/>/<see cref="FeatureChannelRowViewModel.Label"/>
    /// whenever the live parameter object bound in the detail column changes, so an already-added
    /// channel's period etc. can be edited in place (mirrors the label format used when the channel
    /// was first added in <see cref="AddIndicatorChannel"/>).
    /// </summary>
    private void OnSelectedChannelParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (SelectedChannel is not { Kind: FeatureChannelKind.Indicator, Indicator: { } type } row || SelectedChannelSettings is null)
        {
            return;
        }

        row.Params = FeatureChannelConverter.ExtractParams(SelectedChannelSettings, _indicatorFactory);
        row.Label = FeatureChannelConverter.BuildIndicatorLabel(type.ToString(), SelectedChannelSettings.ParameterObject);
    }

    private bool CanAddIndicatorChannel() => _selectedCatalogItem() is not null && _selectedIndicatorSettings() is not null;

    [RelayCommand(CanExecute = nameof(CanAddIndicatorChannel))]
    private void AddIndicatorChannel()
    {
        if (_selectedCatalogItem() is not { } item || _selectedIndicatorSettings() is not { } settings)
        {
            return;
        }

        var parameters = FeatureChannelConverter.ExtractParams(settings, _indicatorFactory);

        Channels.Add(new FeatureChannelRowViewModel
        {
            Kind = FeatureChannelKind.Indicator,
            Indicator = item.Type,
            Params = parameters,
            Label = FeatureChannelConverter.BuildIndicatorLabel(item.ShortName, settings.ParameterObject),
            Normalization = _nextNormalization(),
        });
        OnPropertyChanged(nameof(HasChannels));
        _showToast(FeaturePickerToast.Added(item.ShortName));
    }

    [RelayCommand]
    private void RemoveChannel(FeatureChannelRowViewModel? row)
    {
        if (row is null || !Channels.Contains(row))
        {
            return;
        }

        Channels.Remove(row);
        if (row.Kind == FeatureChannelKind.Price && row.Price is { } field)
        {
            _onPriceRowRemoved(field);
        }

        if (SelectedChannel == row)
        {
            SelectedChannel = null;
        }

        OnPropertyChanged(nameof(HasChannels));
    }

    [RelayCommand]
    private void MoveChannelUp(FeatureChannelRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        int index = Channels.IndexOf(row);
        if (index > 0)
        {
            Channels.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveChannelDown(FeatureChannelRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        int index = Channels.IndexOf(row);
        if (index >= 0 && index < Channels.Count - 1)
        {
            Channels.Move(index, index + 1);
        }
    }

    /// <summary>
    /// Composes the current rows into a <see cref="FeatureSpec"/> in list order, or returns
    /// <see langword="null"/> when no channels are selected.
    /// </summary>
    public FeatureSpec? BuildFeatureSpec()
    {
        if (Channels.Count == 0)
        {
            return null;
        }

        return new FeatureSpec
        {
            Channels = Channels.Select(r => r.ToChannel()).ToList(),
        };
    }
}

/// <summary>
/// Self-contained picker that composes a <see cref="FeatureSpec"/> for a
/// <see cref="PredictionFeatureMode.ComposedFeatures"/> training run: individually selectable
/// Open/High/Low/Close price channels plus any registered indicator (Volume included), each with its
/// own <see cref="ChannelNormalization"/>. It reuses the shared indicator building blocks
/// (<see cref="IIndicatorFactory"/>, <see cref="IndicatorCatalogItem"/>, and the
/// <c>DynamicIndicatorSettingsView</c> parameter editor) rather than the full Indicator Settings
/// dialog, so the main dialog is left untouched. Parameter marshalling is delegated to
/// <see cref="FeatureChannelConverter"/> (the single conversion point shared with the exporter).
///
/// <para>
/// The price-channel, indicator-catalog, nav/mode and composed-channel-list concerns live in
/// <see cref="PriceChannelSection"/>, <see cref="IndicatorCatalogSection"/>,
/// <see cref="NavigationSection"/> and <see cref="ChannelListSection"/>; this class re-exposes their
/// members under the original names so <c>FeatureChannelPickerView.axaml</c>'s compiled bindings
/// bind to the same paths.
/// </para>
/// </summary>
public partial class FeatureChannelPickerViewModel : ViewModelBase
{
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly IToastNotificationService? _toastService;

    private readonly PriceChannelSection _price;
    private readonly IndicatorCatalogSection _catalog;
    private readonly NavigationSection _nav;
    private readonly ChannelListSection _channelList;

    /// <summary>
    /// Shared template Save/Load/Append/Delete/LoadAll skeleton (SSoT with
    /// <c>IndicatorSettingsDialogViewModel</c> and <c>FilterTemplatePickerDialogViewModel</c>).
    /// Null when this picker was constructed without an <see cref="ITemplateService"/> (design-time
    /// DataContext, tests) - every template command below no-ops in that case, exactly as the former
    /// <c>_templateService is null</c> guard did.
    /// </summary>
    private readonly TemplateCrudHelper<FeatureSpecTemplate>? _templateCrud;

    /// <summary>Exposed for the toast-overlay binding in <c>FeatureChannelPickerView.axaml</c>, same
    /// display convention as <c>IndicatorSettingsDialogViewModel.ToastService</c>. May be null (e.g. in
    /// tests or the design-time DataContext); the binding degrades to no-op when unset.</summary>
    public IToastNotificationService? ToastService => _toastService;

    public FeatureChannelPickerViewModel(
        IIndicatorFactory indicatorFactory,
        ITemplateService? templateService = null,
        IToastNotificationService? toastService = null)
    {
        _indicatorFactory = indicatorFactory ?? throw new ArgumentNullException(nameof(indicatorFactory));
        _toastService = toastService;
        if (templateService is not null)
        {
            _templateCrud = new TemplateCrudHelper<FeatureSpecTemplate>(templateService, toastService, TemplateType.Feature);
        }

        _nav = new NavigationSection(onLeftCategoryMode: () => SelectedCategory = null);
        _nav.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        // Constructed before _catalog and _price: its delegates only read those siblings lazily on a
        // user command, never during construction, and _price needs this section's Channels instance.
        _channelList = new ChannelListSection(
            _indicatorFactory,
            selectedCatalogItem: () => SelectedCatalogItem,
            selectedIndicatorSettings: () => SelectedIndicatorSettings,
            nextNormalization: () => SelectedNormalization,
            onPriceRowRemoved: field => SetPriceToggleSilently(field, false),
            showToast: message => ToastService?.ShowNotification(message));
        _channelList.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        _catalog = new IndicatorCatalogSection(
            _indicatorFactory,
            onCategoryPicked: () => Mode = FeaturePickerMode.Category);
        _catalog.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(SelectedCatalogItem))
            {
                AddIndicatorChannelCommand.NotifyCanExecuteChanged();
            }
        };

        _price = new PriceChannelSection(
            _channelList.Channels,
            nextNormalization: () => SelectedNormalization,
            notifyHasChannels: () => OnPropertyChanged(nameof(HasChannels)),
            onFieldAdded: label => ToastService?.ShowNotification(FeaturePickerToast.Added(label)));
        _price.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);

        Channels.CollectionChanged += (_, _) => SaveTemplateCommand.NotifyCanExecuteChanged();

        _ = LoadTemplatesAsync();
    }

    // --- display-control flags (parity with the Library picker's reuse intent) ---

    /// <summary>Whether colour controls are shown for the selected indicator. False when embedded in the wizard.</summary>
    [ObservableProperty]
    private bool _showColorSettings;

    /// <summary>Whether a "set as default" affordance is shown. False when embedded in the wizard.</summary>
    [ObservableProperty]
    private bool _showSetAsDefault;

    // --- catalog + filter (delegated to IndicatorCatalogSection; facades keep the binding paths) ---

    public ObservableCollection<IndicatorCatalogItem> AllCatalogItems => _catalog.AllCatalogItems;
    public ObservableCollection<IndicatorCatalogItem> FilteredCatalogItems => _catalog.FilteredCatalogItems;
    public ObservableCollection<CoreIndicatorCategory> Categories => _catalog.Categories;

    public string SearchText
    {
        get => _catalog.SearchText;
        set => _catalog.SearchText = value;
    }

    public CoreIndicatorCategory? SelectedCategory
    {
        get => _catalog.SelectedCategory;
        set => _catalog.SelectedCategory = value;
    }

    public IndicatorCatalogItem? SelectedCatalogItem
    {
        get => _catalog.SelectedCatalogItem;
        set => _catalog.SelectedCatalogItem = value;
    }

    /// <summary>Editable default settings for the selected catalog item, bound to the parameter editor.</summary>
    public CoreIndicatorSettings? SelectedIndicatorSettings
    {
        get => _catalog.SelectedIndicatorSettings;
        set => _catalog.SelectedIndicatorSettings = value;
    }

    // --- nav entry / mode (delegated to NavigationSection; facades keep the binding paths) ---

    /// <summary>
    /// Which nav entry currently drives the catalog/detail columns. Setting this to anything other
    /// than <see cref="FeaturePickerMode.Category"/> clears <see cref="SelectedCategory"/>, and
    /// picking a category (or "All Categories") snaps this back to <see cref="FeaturePickerMode.Category"/> —
    /// the four nav entries (Selected, Price, a category / All Categories, Templates) are mutually exclusive.
    /// </summary>
    public FeaturePickerMode Mode
    {
        get => _nav.Mode;
        set => _nav.Mode = value;
    }

    public bool IsTemplatesCategory => _nav.IsTemplatesCategory;
    public bool IsNotTemplatesCategory => _nav.IsNotTemplatesCategory;
    public bool IsPriceCategory => _nav.IsPriceCategory;
    public bool IsSelectedCategory => _nav.IsSelectedCategory;

    /// <summary>Whether the catalog/detail columns should show the normal indicator library (a category or "All Categories").</summary>
    public bool IsIndicatorCategory => _nav.IsIndicatorCategory;

    /// <summary>Clears the category filter — the "All Categories" nav entry.</summary>
    public IRelayCommand ClearCategoryCommand => _nav.ClearCategoryCommand;

    /// <summary>Switches the catalog/detail columns to the "Price" nav entry.</summary>
    public IRelayCommand SelectPriceCommand => _nav.SelectPriceCommand;

    /// <summary>Switches the catalog/detail columns to the "Selected" nav entry.</summary>
    public IRelayCommand SelectSelectedChannelsCommand => _nav.SelectSelectedChannelsCommand;

    /// <summary>Switches the catalog/detail columns to the Templates nav entry.</summary>
    public IRelayCommand SelectTemplatesCommand => _nav.SelectTemplatesCommand;

    // --- normalization for newly added channels ---

    /// <summary>Normalization assigned to the next channel added; each row can be changed afterwards.</summary>
    [ObservableProperty]
    private ChannelNormalization _selectedNormalization = ChannelNormalization.None;

    public static IReadOnlyList<ChannelNormalization> NormalizationOptions { get; } =
        Enum.GetValues<ChannelNormalization>();

    // --- composed channel list (delegated to ChannelListSection; facades keep the binding paths / test surface) ---

    public ObservableCollection<FeatureChannelRowViewModel> Channels => _channelList.Channels;

    /// <summary>Whether the current selection would compose a non-empty, valid <see cref="FeatureSpec"/>.</summary>
    public bool HasChannels => _channelList.HasChannels;

    /// <summary>Row currently selected in the "Selected" nav entry's catalog list, edited via the detail column.</summary>
    public FeatureChannelRowViewModel? SelectedChannel
    {
        get => _channelList.SelectedChannel;
        set => _channelList.SelectedChannel = value;
    }

    /// <summary>Live, editable settings for <see cref="SelectedChannel"/> when it is an indicator row.</summary>
    public CoreIndicatorSettings? SelectedChannelSettings
    {
        get => _channelList.SelectedChannelSettings;
        set => _channelList.SelectedChannelSettings = value;
    }

    /// <summary>Picks a row in the "Selected" nav entry's catalog list for editing in the detail column.</summary>
    public IRelayCommand SelectChannelCommand => _channelList.SelectChannelCommand;

    // --- price channel toggles (delegated to PriceChannelSection; facades keep the binding paths / test surface) ---

    public bool IncludeOpen
    {
        get => _price.IncludeOpen;
        set => _price.IncludeOpen = value;
    }

    public bool IncludeHigh
    {
        get => _price.IncludeHigh;
        set => _price.IncludeHigh = value;
    }

    public bool IncludeLow
    {
        get => _price.IncludeLow;
        set => _price.IncludeLow = value;
    }

    public bool IncludeClose
    {
        get => _price.IncludeClose;
        set => _price.IncludeClose = value;
    }

    /// <summary>Formats a user-facing label for a <see cref="PriceType"/>.</summary>
    public static string FormatPriceTypeLabel(PriceType type) => PriceDataHelper.FormatPriceTypeLabel(type);

    /// <summary>The 15 price types, shown as addable catalog rows in the "Price" nav entry in standard PriceType order.</summary>
    public static IReadOnlyList<PriceFieldCatalogItem> PriceCatalogItems { get; } =
        PriceDataHelper.PriceTypeOptions.Select(t => new PriceFieldCatalogItem
        {
            Field = t,
            Label = FormatPriceTypeLabel(t)
        }).ToList();

    /// <summary>Field currently selected in the Price-mode catalog list.</summary>
    public PriceFieldCatalogItem? SelectedPriceField
    {
        get => _price.SelectedPriceField;
        set => _price.SelectedPriceField = value;
    }

    /// <summary>Adds the selected raw price field as a channel.</summary>
    public IRelayCommand AddPriceChannelCommand => _price.AddPriceChannelCommand;

    public bool IsPriceFieldIncluded(PriceType field) => _price.IsPriceFieldIncluded(field);

    public void SetPriceToggleSilently(PriceType field, bool value) => _price.SetPriceToggleSilently(field, value);

    /// <summary>Adds the selected catalog indicator (with its edited default settings) as a channel.</summary>
    public IRelayCommand AddIndicatorChannelCommand => _channelList.AddIndicatorChannelCommand;

    /// <summary>Removes a row from the composed channel list (and re-syncs its Price include flag).</summary>
    public IRelayCommand RemoveChannelCommand => _channelList.RemoveChannelCommand;

    /// <summary>Moves a row one position earlier in the composed channel list.</summary>
    public IRelayCommand MoveChannelUpCommand => _channelList.MoveChannelUpCommand;

    /// <summary>Moves a row one position later in the composed channel list.</summary>
    public IRelayCommand MoveChannelDownCommand => _channelList.MoveChannelDownCommand;

    // --- templates (save/load the whole Price+Selected+Indicator channel list as one FeatureSpecTemplate) ---

    public ObservableCollection<FeatureSpecTemplate> Templates { get; } = new();

    [ObservableProperty]
    private FeatureSpecTemplate? _selectedTemplate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    private string _newTemplateName = string.Empty;

    /// <summary>
    /// Human-readable channel labels for <see cref="SelectedTemplate"/>, refreshed on selection
    /// change. Mirrors <c>IndicatorSettingsDialogViewModel.SelectedTemplateIndicatorNames</c> so the
    /// Templates-mode detail panel shows a readable preview instead of the raw <see cref="FeatureChannel"/> shape.
    /// </summary>
    public ObservableCollection<string> SelectedTemplateChannelLabels { get; } = new();

    partial void OnSelectedTemplateChanged(FeatureSpecTemplate? value) => RefreshSelectedTemplateChannelLabels();

    /// <summary>
    /// Rebuilds <see cref="SelectedTemplateChannelLabels"/> from <see cref="SelectedTemplate"/>.
    /// Called on selection change and again after an in-place overwrite save of the selected
    /// template, whose <see cref="FeatureSpecTemplate.Spec"/> changes without the selection
    /// reference changing.
    /// </summary>
    private void RefreshSelectedTemplateChannelLabels()
    {
        SelectedTemplateChannelLabels.Clear();
        if (SelectedTemplate is not { } value)
        {
            return;
        }

        foreach (var channel in value.Spec.Channels)
        {
            SelectedTemplateChannelLabels.Add(FeatureChannelRowViewModel.FromChannel(channel, _indicatorFactory).Label);
        }
    }

    private async Task LoadTemplatesAsync()
    {
        if (_templateCrud is null)
        {
            return;
        }

        await _templateCrud.LoadAllAsync(Templates, reportErrors: false);
    }

    private bool CanSaveTemplate() => _templateCrud is { IsBusy: false } && !string.IsNullOrWhiteSpace(NewTemplateName) && Channels.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private async Task SaveTemplateAsync()
    {
        if (_templateCrud is null || _templateCrud.IsBusy || string.IsNullOrWhiteSpace(NewTemplateName) || Channels.Count == 0)
        {
            return;
        }

        var trimmedName = NewTemplateName.Trim();
        await _templateCrud.SaveAsync(
            trimmedName,
            Templates,
            build: existing =>
            {
                var template = existing ?? new FeatureSpecTemplate
                {
                    Id = Guid.NewGuid(),
                    Name = trimmedName,
                    CreatedAt = DateTime.UtcNow,
                };

                template.Spec = new FeatureSpec { Channels = Channels.Select(r => r.ToChannel()).ToList() };
                template.UpdatedAt = DateTime.UtcNow;
                return template;
            },
            commit: (saved, existing) =>
            {
                if (existing is null)
                {
                    Templates.Add(saved);
                }
                else if (existing == SelectedTemplate)
                {
                    RefreshSelectedTemplateChannelLabels();
                }

                NewTemplateName = string.Empty;
            });
    }

    [RelayCommand]
    private async Task LoadTemplateAsync(FeatureSpecTemplate? template)
    {
        if (_templateCrud is null || _templateCrud.IsBusy || template is null)
        {
            return;
        }

        await _templateCrud.ApplyAsync(
            template,
            append: false,
            apply: t =>
            {
                // Reset price toggles silently first so TogglePriceChannel's own Channels.Add calls
                // (triggered by OnIncludeXChanged) don't fire while the row list is being rebuilt below.
                SetPriceToggleSilently(PriceType.Open, false);
                SetPriceToggleSilently(PriceType.High, false);
                SetPriceToggleSilently(PriceType.Low, false);
                SetPriceToggleSilently(PriceType.Close, false);

                Channels.Clear();
                foreach (var channel in t.Spec.Channels)
                {
                    Channels.Add(FeatureChannelRowViewModel.FromChannel(channel, _indicatorFactory));
                    if (channel is { Kind: FeatureChannelKind.Price, Price: { } field })
                    {
                        SetPriceToggleSilently(field, true);
                    }
                }

                OnPropertyChanged(nameof(HasChannels));
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Adds <paramref name="template"/>'s channels to the current selection without clearing it first -
    /// the non-destructive counterpart to <see cref="LoadTemplateAsync"/>, mirroring
    /// <c>IndicatorSettingsDialogViewModel.AppendTemplateAsync</c>'s Load/Append/Delete convention.
    /// Indicator channels may duplicate freely (same as the Add flow never deduplicates them); a Price
    /// channel is skipped when its field is already included, since <see cref="IncludeOpen"/> etc. are
    /// the single-flag-per-field existence SSoT for Price channels (see <c>PriceChannelSection</c>)
    /// and a second row for the same field would desync that flag from the actual channel list.
    /// </summary>
    [RelayCommand]
    private async Task AppendTemplateAsync(FeatureSpecTemplate? template)
    {
        if (_templateCrud is null || _templateCrud.IsBusy || template is null)
        {
            return;
        }

        await _templateCrud.ApplyAsync(
            template,
            append: true,
            apply: t =>
            {
                foreach (var channel in t.Spec.Channels)
                {
                    if (channel is { Kind: FeatureChannelKind.Price, Price: { } existingField } && IsPriceFieldIncluded(existingField))
                    {
                        continue;
                    }

                    Channels.Add(FeatureChannelRowViewModel.FromChannel(channel, _indicatorFactory));
                    if (channel is { Kind: FeatureChannelKind.Price, Price: { } newField })
                    {
                        SetPriceToggleSilently(newField, true);
                    }
                }

                OnPropertyChanged(nameof(HasChannels));
                return Task.CompletedTask;
            });
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(FeatureSpecTemplate? template)
    {
        if (_templateCrud is null || _templateCrud.IsBusy || template is null)
        {
            return;
        }

        await _templateCrud.DeleteAsync(
            template,
            Templates,
            afterRemove: removed =>
            {
                if (SelectedTemplate == removed)
                {
                    SelectedTemplate = null;
                }
            });
    }

    /// <summary>
    /// Composes the current rows into a <see cref="FeatureSpec"/> in list order, or returns
    /// <see langword="null"/> when no channels are selected.
    /// </summary>
    public FeatureSpec? BuildFeatureSpec() => _channelList.BuildFeatureSpec();
}
