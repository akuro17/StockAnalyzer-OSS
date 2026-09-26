using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>Left-column nav display item (spec-external UX request: mirror the reference
/// Screener/Indicator "All Filters"/"Indicators" hierarchical nav list exactly, instead of a flat
/// group-name list). Backtest only ever has one category section ("Indicators" - "Columns"/"Criteria"
/// are Screener-only concepts, already excluded per <see cref="BacktestIndicatorSelectionViewModel"/>'s
/// own class remarks), so this is a minimal local type rather than reusing Screener's own
/// <c>ScreenerGroupDisplayItem</c> (which models Columns/Criteria sections this tab does not have).</summary>
public sealed class BacktestIndicatorGroupDisplayItem
{
    public bool IsHeader { get; init; }
    public bool IsAllFilters { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsStandardItem => !IsHeader && !IsAllFilters;
}

/// <summary>Task 6 condition-builder Timeframe picker item: wraps <see cref="BacktestConditionSide.Frame"/>'s
/// nullable convention ("Same as run") as a display-friendly, pre-localized option, since
/// <c>EnumToLocalizedDisplayNameConverter</c> has no representation for a null enum value.</summary>
public sealed class BacktestConditionFrameOption
{
    public TimeFrame? Value { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public override string ToString() => DisplayName;
}

/// <summary>
/// One row of BacktestWindow Tab 2's "added indicators" list (spec §9.6).
/// </summary>
public partial class BacktestIndicatorSelectionItem : ObservableObject
{
    public string Key { get; }
    public IndicatorType Type { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private TimeFrame? _frame;

    [ObservableProperty]
    private CoreIndicatorParameterBase? _parameters;

    public BacktestIndicatorSelectionItem(string key, IndicatorType type, string displayName, TimeFrame? frame, CoreIndicatorParameterBase? parameters)
    {
        Key = key;
        Type = type;
        DisplayName = displayName;
        _frame = frame;
        _parameters = parameters;
    }
}

/// <summary>
/// BacktestWindow Tab 2 (spec §5.3/§9.6): a dedicated 3-column indicator picker built on the same
/// <see cref="IScreenerCatalogProvider"/> the Screener's <c>IndicatorRegistrationViewModel</c> uses,
/// but restricted to <see cref="ScreenerItemCategoryType.Indicator"/> catalog entries only (Columns
/// and Criteria are screener-only concepts, explicitly excluded per spec §5.3/§9.6) — and never
/// modifies <c>IndicatorRegistrationViewModel</c>/<c>ScreenerViewModel</c> themselves (spec §3.2).
/// "Unsupported" (spec §5.3: unsupported indicators are rejected as UnsupportedIndicator) is determined objectively via
/// <see cref="IIndicatorFactory.IsRegistered"/> - an indicator type the factory cannot construct at
/// all obviously cannot be computed for a backtest run either. Selecting one shows a warning instead
/// of silently discarding it; it is never added to <see cref="AddedIndicators"/>.
/// </summary>
public partial class BacktestIndicatorSelectionViewModel : ViewModelBase
{
    private readonly IScreenerCatalogProvider _catalogProvider;
    private readonly IIndicatorFactory? _indicatorFactory;

    /// <summary>Lower bound of the condition Offset input boxes: the causality invariant (never below the current bar). Decimal because the input control's bounds are decimals.</summary>
    public decimal MinConditionOffset => BacktestConditionOffsetRule.MinOffset;

    /// <summary>Upper bound of the condition Offset input boxes: the configured <c>Backtest:MaxConditionOffset</c>, the same value the saved-configuration validation enforces.</summary>
    public decimal MaxConditionOffset { get; }
    private readonly ILocalizationService _localizationService;
    private System.Collections.Generic.List<ScreenerCatalogItem> _allIndicatorItems = new();
    private int _nextKeySuffix = 1;

    /// <summary>
    /// SAで改善 (Round 2, Y:\Temp\sa_improvement_plan_BacktestUiCleanupRound2.md Task 3): drives the same
    /// toast-notification overlay <see cref="Views.IndicatorSettingsWindow"/>'s "Library -> Active" flow
    /// already uses (<see cref="ViewModels.Dialogs.IndicatorSettingsDialogViewModel.AddSelectedLibraryIndicator"/>),
    /// so adding a condition here confirms itself the exact same way - the user's explicit "no inventing a
    /// different notification style" instruction. <see cref="BacktestWindow"/>'s own toast overlay Border
    /// binds directly to this property, mirroring <c>IndicatorSettingsWindow.axaml</c>'s own binding shape.
    /// </summary>
    public IToastNotificationService ToastService { get; }

    public ObservableCollection<BacktestIndicatorGroupDisplayItem> NavGroups { get; } = new();
    public ObservableCollection<ScreenerCatalogItem> FilteredItems { get; } = new();
    public ObservableCollection<BacktestIndicatorSelectionItem> AddedIndicators { get; } = new();

    /// <summary>
    /// Task 5 safe-extension: the comparison conditions <see cref="Services.Backtest.Engine.ConditionBasedBacktestStrategy"/>
    /// (Task 3) evaluates, read directly by <c>BacktestWindowViewModel.RunBacktestAsync</c> for strategy
    /// selection. Empty by default - a run with no conditions here behaves exactly as it did before this
    /// feature shipped (falls back to <see cref="Services.Backtest.Engine.NoOpBacktestStrategy"/>). Task 6
    /// owns building the "Registered Conditions" list UI/Add-Remove commands and the corresponding
    /// Save/Restore DTO mapping (mirroring <see cref="AddedIndicators"/>'s own pattern) - this collection
    /// only exists so Task 5's run-time wiring has something concrete to read.
    /// </summary>
    public ObservableCollection<BacktestConditionEntry> ConditionEntries { get; } = new();

    /// <summary>
    /// SAで改善 (Y:\Temp\sa_improvement_plan_BacktestResultsUiPolish.md Task 4): per-role "buckets" of
    /// <see cref="ConditionEntries"/>, kept in sync automatically (see the constructor's
    /// <c>ConditionEntries.CollectionChanged</c> subscription below) so the Results tab can render three
    /// equal Entry/Exit/Both columns without duplicating add/remove/restore logic at every one of
    /// <see cref="ConditionEntries"/>'s own mutation call sites. <see cref="BacktestConditionRole.Reversal"/>
    /// is bucketed into <see cref="BothConditionEntries"/> alongside <see cref="BacktestConditionRole.Both"/>
    /// - both are the two roles the UI's "Both" button can ever emit, and this spec has no 4th column.
    /// </summary>
    public ObservableCollection<BacktestConditionEntry> EntryConditionEntries { get; } = new();
    public ObservableCollection<BacktestConditionEntry> ExitConditionEntries { get; } = new();
    public ObservableCollection<BacktestConditionEntry> BothConditionEntries { get; } = new();

    [ObservableProperty]
    private BacktestIndicatorGroupDisplayItem? _selectedGroupItem;

    // ===== Task 6: Comparison Condition builder (Left/Right Focus-Bind state) =====
    // Mirrors IndicatorRegistrationViewModel's own Left/Right Focus-Bind pattern (StockAnalyzer.Avalonia
    // .ViewModels.IndicatorRegistrationViewModel) - structural precedent only, per the parent plan's
    // section 2 reuse audit ("do not modify IndicatorRegistrationViewModel/ScreenerViewModel"). This is
    // deliberately ADDITIVE alongside the pre-existing single-target "track this indicator"
    // SelectedCatalogItem/SelectedIndicatorSettings/AddSelectedIndicatorCommand/AddedIndicators flow
    // above (Task 17, spec §5.3/§9.6) - that flow's own tests (BacktestIndicatorSelectionTests) assert
    // its exact current behavior, so it is left completely untouched rather than replaced. The shared
    // center catalog's SelectedCatalogItem now ALSO feeds whichever condition-builder side
    // (ActiveConditionSide) is focused, via OnSelectedCatalogItemChanged below.

    [ObservableProperty]
    private TargetSide _activeConditionSide = TargetSide.Left;

    [ObservableProperty]
    private bool _isLeftConditionActive = true;

    [ObservableProperty]
    private bool _isRightConditionActive;

    partial void OnActiveConditionSideChanged(TargetSide value)
    {
        IsLeftConditionActive = value == TargetSide.Left;
        IsRightConditionActive = value == TargetSide.Right;
    }

    [RelayCommand]
    private void SelectLeftConditionTarget() => ActiveConditionSide = TargetSide.Left;

    [RelayCommand]
    private void SelectRightConditionTarget()
    {
        if (ConditionTargetMode == RightHandTargetMode.Indicator)
        {
            ActiveConditionSide = TargetSide.Right;
        }
    }

    /// <summary>Only NumericValue/Indicator are offered (safe extension audit, plan section 2: "StringValue
    /// likely unused for Backtest since bars have no string series" - left unused-but-present on the
    /// reused enum rather than silently wiring an unreachable string-comparison path).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEntryConditionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddExitConditionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBothConditionCommand))]
    private RightHandTargetMode _conditionTargetMode = RightHandTargetMode.NumericValue;

    [ObservableProperty]
    private bool _isConditionRightNumericMode = true;

    [ObservableProperty]
    private bool _isConditionRightIndicatorMode;

    partial void OnConditionTargetModeChanged(RightHandTargetMode value)
    {
        IsConditionRightNumericMode = value == RightHandTargetMode.NumericValue;
        IsConditionRightIndicatorMode = value == RightHandTargetMode.Indicator;
    }

    [RelayCommand]
    private void SetConditionRightModeNumeric()
    {
        ConditionTargetMode = RightHandTargetMode.NumericValue;
        ActiveConditionSide = TargetSide.Left;
        RightConditionCatalogItem = null;
        RightConditionSettings = null;
        RightConditionAvailableOutputs.Clear();
    }

    [RelayCommand]
    private void SetConditionRightModeIndicator()
    {
        ConditionTargetMode = RightHandTargetMode.Indicator;
        ActiveConditionSide = TargetSide.Right;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEntryConditionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddExitConditionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBothConditionCommand))]
    private ScreenerCatalogItem? _leftConditionCatalogItem;

    [ObservableProperty]
    private CoreIndicatorSettings? _leftConditionSettings;

    public ObservableCollection<string> LeftConditionAvailableOutputs { get; } = new();

    [ObservableProperty]
    private string _leftConditionSelectedOutput = IndicatorResult.MainSeriesName;

    [ObservableProperty]
    private bool _hasMultipleLeftConditionOutputs;

    [ObservableProperty]
    private int _leftConditionOffset;

    [ObservableProperty]
    private BacktestConditionFrameOption _leftConditionFrame = new() { DisplayName = "Same as run" };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEntryConditionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddExitConditionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddBothConditionCommand))]
    private ScreenerCatalogItem? _rightConditionCatalogItem;

    [ObservableProperty]
    private CoreIndicatorSettings? _rightConditionSettings;

    public ObservableCollection<string> RightConditionAvailableOutputs { get; } = new();

    [ObservableProperty]
    private string _rightConditionSelectedOutput = IndicatorResult.MainSeriesName;

    [ObservableProperty]
    private bool _hasMultipleRightConditionOutputs;

    [ObservableProperty]
    private int _rightConditionOffset;

    [ObservableProperty]
    private BacktestConditionFrameOption _rightConditionFrame = new() { DisplayName = "Same as run" };

    [ObservableProperty]
    private decimal _conditionRightNumericValue;

    /// <summary>
    /// Task 8b safe extension (Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md sections
    /// 3.4/4.5): entry-price-relative Stop-Loss/Take-Profit, shown as a small "Risk Management"
    /// sub-section near the Role picker. Both null (blank) by default, meaning disabled — an untouched
    /// run is entirely unaffected by this feature, matching <see cref="BacktestRiskManagementSettings"/>'s
    /// own "opt-in, no invented default" shape.
    /// </summary>
    [ObservableProperty]
    private decimal? _riskStopLossPercent;

    [ObservableProperty]
    private decimal? _riskTakeProfitPercent;

    /// <summary>
    /// Safe extension (Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md section 2.1/4):
    /// the trade direction a newly-added Entry/Both condition opens, selected via the Long/Short toggle at
    /// the Entry-button row's left edge. Defaults to Long so an untouched selector still produces the same
    /// <see cref="BacktestConditionEntry.Position"/> default the Core model itself uses.
    /// </summary>
    [ObservableProperty]
    private TradeSide _targetPosition = TradeSide.Long;

    [ObservableProperty]
    private bool _isTargetPositionLong = true;

    [ObservableProperty]
    private bool _isTargetPositionShort;

    partial void OnTargetPositionChanged(TradeSide value)
    {
        IsTargetPositionLong = value == TradeSide.Long;
        IsTargetPositionShort = value == TradeSide.Short;
    }

    [RelayCommand]
    private void SetTargetPositionLong() => TargetPosition = TradeSide.Long;

    [RelayCommand]
    private void SetTargetPositionShort() => TargetPosition = TradeSide.Short;

    /// <summary>Excludes Contains/DoesNotContain (plan section 6: string-oriented operators are not
    /// reachable for an Indicator-only Backtest condition).</summary>
    public ObservableCollection<ComparisonOperator> AvailableConditionOperators { get; } = new(new[]
    {
        ComparisonOperator.GreaterThan,
        ComparisonOperator.GreaterThanOrEqual,
        ComparisonOperator.LessThan,
        ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.Equal,
        ComparisonOperator.NotEqual,
    });

    [ObservableProperty]
    private ComparisonOperator _conditionOperator = ComparisonOperator.GreaterThan;

    /// <summary>Null (index 0, "same as the run's Frame") plus the same D1/W1/MN1 restriction the
    /// Configuration tab's own Frame selector already uses (plan section 4.4 - <c>ParquetDataService</c>
    /// only ever serves those three).</summary>
    public IReadOnlyList<BacktestConditionFrameOption> AvailableConditionFrames { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ScreenerCatalogItem? _selectedCatalogItem;

    [ObservableProperty]
    private TimeFrame? _selectedFrameOverride;

    [ObservableProperty]
    private string? _unsupportedWarningMessage;

    /// <summary>Editable parameter form for <see cref="SelectedCatalogItem"/> (spec-external UX
    /// request: match the reference Screener/Indicator "Left Target" card, which lets the user review
    /// and adjust an indicator's settings before adding it, instead of always adding silent factory
    /// defaults). Re-seeded from <see cref="IScreenerCatalogProvider.GetDefaultSettings"/> whenever the
    /// selection changes; <see cref="AddSelectedIndicator"/> captures whatever the user left here.</summary>
    [ObservableProperty]
    private CoreIndicatorSettings? _selectedIndicatorSettings;

    /// <summary>Read-only hint shown in place of the registered-indicators list itself (spec-external
    /// UX request: the list moved to the Results tab, alongside the run output it feeds into).</summary>
    [ObservableProperty]
    private string _addedIndicatorsSummary = string.Empty;

    public BacktestIndicatorSelectionViewModel(
        IScreenerCatalogProvider catalogProvider,
        ILocalizationService localizationService,
        IIndicatorFactory? indicatorFactory = null)
        : this(catalogProvider, localizationService, toastService: null, indicatorFactory)
    {
    }

    public BacktestIndicatorSelectionViewModel(
        IScreenerCatalogProvider catalogProvider,
        ILocalizationService localizationService,
        IToastNotificationService? toastService,
        IIndicatorFactory? indicatorFactory = null,
        StockAnalyzer.Core.Services.IStockAnalyzerSettings? settings = null)
    {
        MaxConditionOffset = settings?.BacktestMaxConditionOffset ?? new StockAnalyzer.Core.Models.Settings.BacktestSettings().MaxConditionOffset;
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        ToastService = toastService ?? new ToastNotificationService();
        _indicatorFactory = indicatorFactory;

        AddedIndicators.CollectionChanged += (_, _) => UpdateAddedIndicatorsSummary();
        UpdateAddedIndicatorsSummary();
        ConditionEntries.CollectionChanged += (_, _) => RefreshConditionEntryBuckets();
        LoadCatalog();

        AvailableConditionFrames = new[]
        {
            new BacktestConditionFrameOption { Value = null, DisplayName = _localizationService.GetString("Backtest_Condition_FrameSameAsRun") },
            new BacktestConditionFrameOption { Value = TimeFrame.D1, DisplayName = _localizationService.GetString("Enum_TimeFrame_D1") },
            new BacktestConditionFrameOption { Value = TimeFrame.W1, DisplayName = _localizationService.GetString("Enum_TimeFrame_W1") },
            new BacktestConditionFrameOption { Value = TimeFrame.MN1, DisplayName = _localizationService.GetString("Enum_TimeFrame_MN1") },
        };
        LeftConditionFrame = AvailableConditionFrames[0];
        RightConditionFrame = AvailableConditionFrames[0];
    }

    private void UpdateAddedIndicatorsSummary()
    {
        AddedIndicatorsSummary = _localizationService.GetFormattedString(
            "Backtest_IndicatorSelection_RegisteredSummary",
            "{0} indicator(s) registered - see the Results tab.",
            AddedIndicators.Count);
    }

    private void LoadCatalog()
    {
        var items = _catalogProvider.GetCatalogItems(_indicatorFactory);
        _allIndicatorItems = items
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Indicator)
            .Where(i => i.IndicatorType is not IndicatorType type || !BacktestIndicatorEligibility.IsTypeBlocked(type))
            .ToList();

        NavGroups.Clear();
        NavGroups.Add(new BacktestIndicatorGroupDisplayItem { IsAllFilters = true, DisplayName = _localizationService.GetString("Nav_AllFilters") });
        NavGroups.Add(new BacktestIndicatorGroupDisplayItem { IsHeader = true, DisplayName = _localizationService.GetString("Backtest_IndicatorSelection_NavHeader") });
        foreach (var group in _allIndicatorItems.Select(i => i.GroupName).Distinct().OrderBy(g => g, StringComparer.Ordinal))
        {
            NavGroups.Add(new BacktestIndicatorGroupDisplayItem { DisplayName = group });
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        IEnumerable<ScreenerCatalogItem> query = _allIndicatorItems;

        if (SelectedGroupItem is { IsStandardItem: true } group)
        {
            query = query.Where(i => i.GroupName == group.DisplayName);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(i =>
                i.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                i.ShortName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            FilteredItems.Add(item);
        }
    }

    partial void OnSelectedGroupItemChanged(BacktestIndicatorGroupDisplayItem? value)
    {
        if (value is { IsHeader: true }) return;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedCatalogItemChanged(ScreenerCatalogItem? value)
    {
        UnsupportedWarningMessage = null;
        SelectedIndicatorSettings = value?.IndicatorType is IndicatorType type
            ? _catalogProvider.GetDefaultSettings(type, _indicatorFactory)
            : null;

        if (value?.IndicatorType is not null)
        {
            BindConditionSide(value, ActiveConditionSide == TargetSide.Left);
        }
    }

    /// <summary>Task 6 condition-builder equivalent of <c>IndicatorRegistrationViewModel.BindToSide</c>
    /// (structural precedent, plan section 2 - that file itself is never modified): seeds whichever side
    /// (Left/Right) is currently focused with the newly-selected catalog item's default settings and
    /// available Output series (<see cref="IScreenerCatalogProvider.GetOutputSeriesNames"/>, plan
    /// section 4.4's Task 6a amendment).
    ///
    /// SAで改善 bug fix (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): the catalog's
    /// "Price" group has one row per <see cref="PriceType"/> (Open/High/Low/...), all sharing
    /// <see cref="IndicatorType.Price"/> and distinguished only by <see cref="ScreenerCatalogItem.ShortName"/>
    /// - exactly the same shape <c>IndicatorRegistrationViewModel.BindToSide</c> already special-cases.
    /// This method previously had no such special case, so every Price row silently bound to the same
    /// generic default settings (PriceSource always Close) regardless of which row was clicked - a
    /// condition like "High > Low" was actually always "Close > Close". Reusing that exact precedent here
    /// fixes it.</summary>
    private void BindConditionSide(ScreenerCatalogItem catalogItem, bool isLeft)
    {
        if (catalogItem.IndicatorType is not IndicatorType type) return;

        CoreIndicatorSettings? settings;
        IReadOnlyList<string> outputs;
        if (type == IndicatorType.Price)
        {
            PriceType priceType = Enum.TryParse(catalogItem.ShortName, out PriceType parsed) ? parsed : PriceType.Close;
            settings = new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Price,
                DisplayName = catalogItem.DisplayName,
                PriceSource = priceType,
                ParameterObject = null,
            };
            outputs = new[] { catalogItem.ShortName };
        }
        else
        {
            settings = _catalogProvider.GetDefaultSettings(type, _indicatorFactory);
            // Causality guard: hide series a backtest must not use (e.g. Ichimoku's ChikouSpan) - see BacktestIndicatorEligibility.
            outputs = _catalogProvider.GetOutputSeriesNames(type, _indicatorFactory)
                .Where(outputName => BacktestIndicatorEligibility.IsAllowed(type, outputName))
                .ToList();
        }

        if (isLeft)
        {
            LeftConditionCatalogItem = catalogItem;
            LeftConditionSettings = settings;
            LeftConditionAvailableOutputs.Clear();
            foreach (string outputName in outputs) LeftConditionAvailableOutputs.Add(outputName);
            LeftConditionSelectedOutput = LeftConditionAvailableOutputs.FirstOrDefault() ?? IndicatorResult.MainSeriesName;
            HasMultipleLeftConditionOutputs = LeftConditionAvailableOutputs.Count > 1;
        }
        else
        {
            RightConditionCatalogItem = catalogItem;
            RightConditionSettings = settings;
            RightConditionAvailableOutputs.Clear();
            foreach (string outputName in outputs) RightConditionAvailableOutputs.Add(outputName);
            RightConditionSelectedOutput = RightConditionAvailableOutputs.FirstOrDefault() ?? IndicatorResult.MainSeriesName;
            HasMultipleRightConditionOutputs = RightConditionAvailableOutputs.Count > 1;
        }
    }

    private bool CanAddCondition() =>
        LeftConditionCatalogItem?.IndicatorType is not null &&
        (ConditionTargetMode != RightHandTargetMode.Indicator || RightConditionCatalogItem?.IndicatorType is not null);

    /// <summary>SAで改善 (2026-09-18): the separate "Add" button + Role picker were replaced with three
    /// direct action buttons (Entry/Exit/Both) - each commits the currently-configured Left/Right target
    /// state as a new <see cref="BacktestConditionEntry"/> with its own fixed Role in one click, instead
    /// of requiring the user to first pick a Role then press a separate Add button. The AND/OR
    /// LogicalOperator picker was removed at the same time (user request) - every new entry now uses
    /// <see cref="BacktestConditionEntry.LogicalOperator"/>'s own default (<see cref="LogicalOperator.And"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanAddCondition))]
    private void AddEntryCondition() => AddConditionWithRole(BacktestConditionRole.EntryOnly);

    [RelayCommand(CanExecute = nameof(CanAddCondition))]
    private void AddExitCondition() => AddConditionWithRole(BacktestConditionRole.ExitOnly);

    /// <summary>The "Both" button commits <see cref="BacktestConditionRole.Reversal"/>, not
    /// <see cref="BacktestConditionRole.Both"/> (corrected 2026-09-19, see
    /// Y:\Temp\sa_implementation_plan_BacktestPositionDirectionReversal.md section 2.2): the user's
    /// original request for this button — Position-aware Entry/Exit with same-bar reversal when the
    /// opposite side is held — is Reversal's exact definition; Both's own, older side-agnostic meaning is
    /// preserved unconditionally for entries that already carry it (e.g. restored from a pre-existing
    /// saved configuration).</summary>
    [RelayCommand(CanExecute = nameof(CanAddCondition))]
    private void AddBothCondition() => AddConditionWithRole(BacktestConditionRole.Reversal);

    private void AddConditionWithRole(BacktestConditionRole role)
    {
        if (!CanAddCondition()) return;

        // Same objective, mechanical "unsupported" rejection AddSelectedIndicator already applies
        // (spec §5.3) - a condition side ultimately becomes a StrategyIndicatorRequest too (Task 3), so
        // an unregistered IndicatorType must be rejected here at selection time, not only surfaced as a
        // confusing failure much later when the engine actually runs.
        if (!IsSupported(LeftConditionCatalogItem!) || (ConditionTargetMode == RightHandTargetMode.Indicator && !IsSupported(RightConditionCatalogItem!)))
        {
            UnsupportedWarningMessage = _localizationService.GetString("Backtest_UnsupportedIndicatorMessage");
            return;
        }
        UnsupportedWarningMessage = null;

        BacktestConditionSide left = CreateConditionSide(
            LeftConditionCatalogItem!, LeftConditionSettings, LeftConditionSelectedOutput, LeftConditionFrame.Value, LeftConditionOffset);

        BacktestConditionSide? right = ConditionTargetMode == RightHandTargetMode.Indicator
            ? CreateConditionSide(RightConditionCatalogItem!, RightConditionSettings, RightConditionSelectedOutput, RightConditionFrame.Value, RightConditionOffset)
            : null;

        var newEntry = new BacktestConditionEntry
        {
            Left = left,
            Operator = ConditionOperator,
            TargetMode = ConditionTargetMode,
            RightNumericValue = ConditionRightNumericValue,
            Right = right,
            Role = role,
            Position = TargetPosition,
        };
        ConditionEntries.Add(newEntry);

        // SAで改善 (Round 2): confirms the add exactly like Indicator Manager/Library's "Active"-toggle
        // flow does (IndicatorSettingsDialogViewModel.AddSelectedLibraryIndicator) - same
        // ToastService.ShowNotification($"{name} {Msg_Added}") shape, no custom notification style.
        string entryDisplay = BacktestConditionEntryDisplayConverter.Instance.Convert(
            newEntry, typeof(string), null, CultureInfo.InvariantCulture) as string ?? string.Empty;
        ToastService.ShowNotification($"{entryDisplay} {_localizationService.GetString("Msg_Added")}");

        ActiveConditionSide = TargetSide.Left;
    }

    private BacktestConditionSide CreateConditionSide(
        ScreenerCatalogItem catalogItem, CoreIndicatorSettings? settings, string outputName, TimeFrame? frame, int offset) => new()
    {
        IndicatorType = catalogItem.IndicatorType!.Value,
        Parameters = settings?.ParameterObject?.Clone(),
        OutputName = _catalogProvider.NormalizeOutputSeriesName(
            catalogItem.IndicatorType!.Value,
            outputName,
            catalogItem.IndicatorType == IndicatorType.Price ? settings?.PriceSource : null,
            _indicatorFactory),
        Offset = offset,
        Frame = frame,
        // SAで改善 (Y:\Temp\sa_improvement_plan_BacktestPriceSourceConditionFix.md): scoped to the Price
        // catalog rows only - BindConditionSide is the only place that ever sets a meaningful, non-default
        // PriceSource on `settings`, so this stays a strictly-additive fix for the specific broken case
        // (no behavior change for any other indicator type).
        PriceSource = catalogItem.IndicatorType == IndicatorType.Price ? settings?.PriceSource : null,
    };

    [RelayCommand]
    private void RemoveConditionEntry(BacktestConditionEntry? entry)
    {
        if (entry is null) return;
        ConditionEntries.Remove(entry);
    }

    /// <summary>Rebuilds the three role buckets from scratch on every <see cref="ConditionEntries"/>
    /// change (add/remove/clear-then-restore) - simpler and less error-prone than trying to translate
    /// each individual <see cref="System.Collections.Specialized.NotifyCollectionChangedAction"/> into a
    /// matching bucket edit, and this list is never large enough for the O(n) rebuild to matter.</summary>
    private void RefreshConditionEntryBuckets()
    {
        EntryConditionEntries.Clear();
        ExitConditionEntries.Clear();
        BothConditionEntries.Clear();
        foreach (BacktestConditionEntry entry in ConditionEntries)
        {
            switch (entry.Role)
            {
                case BacktestConditionRole.EntryOnly:
                    EntryConditionEntries.Add(entry);
                    break;
                case BacktestConditionRole.ExitOnly:
                    ExitConditionEntries.Add(entry);
                    break;
                default:
                    BothConditionEntries.Add(entry);
                    break;
            }
        }
    }

    /// <summary>
    /// Task 6's own owed Save/Restore DTO mapping (Task 5 deliberately left <see cref="ConditionEntries"/>
    /// unwired - see this property's own class remarks). Mirrors <see cref="ReplaceAddedIndicators"/>'s
    /// pattern.
    /// </summary>
    public void ReplaceConditionEntries(IEnumerable<BacktestConditionEntryDto> dtos)
    {
        ConditionEntries.Clear();
        foreach (BacktestConditionEntryDto dto in dtos)
        {
            ConditionEntries.Add(new BacktestConditionEntry
            {
                Left = ToSide(dto.Left),
                Operator = dto.Operator,
                TargetMode = dto.TargetMode,
                RightNumericValue = dto.RightNumericValue,
                Right = dto.Right is null ? null : ToSide(dto.Right),
                LogicalOperator = dto.LogicalOperator,
                Role = dto.Role,
                Position = dto.Position,
            });
        }

        static BacktestConditionSide ToSide(BacktestConditionSideDto sideDto) => new()
        {
            IndicatorType = sideDto.IndicatorType,
            Parameters = sideDto.Parameters?.Clone(),
            OutputName = IndicatorOutputSeriesResolver.Normalize(sideDto.IndicatorType, sideDto.OutputName, sideDto.PriceSource),
            Offset = sideDto.Offset,
            Frame = sideDto.Frame,
            PriceSource = sideDto.PriceSource,
        };
    }

    public List<BacktestConditionEntryDto> BuildConditionEntryDtos() => ConditionEntries.Select(entry => new BacktestConditionEntryDto
    {
        Left = ToSideDto(entry.Left),
        Operator = entry.Operator,
        TargetMode = entry.TargetMode,
        RightNumericValue = entry.RightNumericValue,
        Right = entry.Right is null ? null : ToSideDto(entry.Right),
        LogicalOperator = entry.LogicalOperator,
        Role = entry.Role,
        Position = entry.Position,
    }).ToList();

    private static BacktestConditionSideDto ToSideDto(BacktestConditionSide side) => new()
    {
        IndicatorType = side.IndicatorType,
        Parameters = side.Parameters?.Clone(),
        OutputName = IndicatorOutputSeriesResolver.Normalize(side.IndicatorType, side.OutputName, side.PriceSource),
        Offset = side.Offset,
        Frame = side.Frame,
        PriceSource = side.PriceSource,
    };

    /// <summary>
    /// Task 8b: null whenever both fields are blank, so a run/save with the feature untouched never
    /// constructs a <see cref="BacktestRiskManagementSettings"/>/<see cref="BacktestRiskManagementSettingsDto"/>
    /// at all - not just one with both properties null (belt-and-suspenders alongside
    /// <see cref="BacktestRiskManagementSettings.IsEnabled"/>'s own guard on the consuming side).
    /// </summary>
    public BacktestRiskManagementSettings? BuildRiskManagementSettings() =>
        RiskStopLossPercent is null && RiskTakeProfitPercent is null
            ? null
            : new BacktestRiskManagementSettings { StopLossPercent = RiskStopLossPercent, TakeProfitPercent = RiskTakeProfitPercent };

    public BacktestRiskManagementSettingsDto? BuildRiskManagementSettingsDto() =>
        RiskStopLossPercent is null && RiskTakeProfitPercent is null
            ? null
            : new BacktestRiskManagementSettingsDto { StopLossPercent = RiskStopLossPercent, TakeProfitPercent = RiskTakeProfitPercent };

    public void ReplaceRiskManagementSettings(BacktestRiskManagementSettingsDto? dto)
    {
        RiskStopLossPercent = dto?.StopLossPercent;
        RiskTakeProfitPercent = dto?.TakeProfitPercent;
    }

    /// <summary>Objective, mechanical support check - see class remarks. Never guesses at a trading/signal convention.</summary>
    public bool IsSupported(ScreenerCatalogItem item)
    {
        if (item.IndicatorType is null) return false;
        return _indicatorFactory is null || _indicatorFactory.IsRegistered(item.IndicatorType.Value);
    }

    [RelayCommand]
    private void AddSelectedIndicator()
    {
        var item = SelectedCatalogItem;
        if (item?.IndicatorType is null) return;

        if (!IsSupported(item))
        {
            UnsupportedWarningMessage = _localizationService.GetString("Backtest_UnsupportedIndicatorMessage");
            return;
        }

        UnsupportedWarningMessage = null;
        var key = $"{item.ShortName}_{_nextKeySuffix++}";
        AddedIndicators.Add(new BacktestIndicatorSelectionItem(
            key,
            item.IndicatorType.Value,
            item.DisplayName,
            SelectedFrameOverride,
            SelectedIndicatorSettings?.ParameterObject?.Clone()));
    }

    [RelayCommand]
    private void RemoveIndicator(BacktestIndicatorSelectionItem? item)
    {
        if (item is null) return;
        AddedIndicators.Remove(item);
    }

    /// <summary>
    /// Rebuilds <see cref="AddedIndicators"/> from a persisted/restored set (spec §5.5 Save/Restore
    /// round trip, task 14). DisplayName is re-resolved from the current catalog by
    /// <see cref="StockAnalyzer.Core.Models.IndicatorType"/> rather than persisted, since the catalog
    /// (not the saved file) is this app's SSoT for display names; a type that no longer resolves
    /// (catalog changed since the file was saved) falls back to its raw enum name rather than losing
    /// the entry silently.
    /// </summary>
    public void ReplaceAddedIndicators(IEnumerable<BacktestSelectedIndicatorDto> dtos)
    {
        AddedIndicators.Clear();
        foreach (BacktestSelectedIndicatorDto dto in dtos)
        {
            string displayName = _allIndicatorItems.FirstOrDefault(i => i.IndicatorType == dto.Type)?.DisplayName ?? dto.Type.ToString();
            AddedIndicators.Add(new BacktestIndicatorSelectionItem(dto.Key, dto.Type, displayName, dto.Frame, dto.Parameters?.Clone()));
        }
    }
}
