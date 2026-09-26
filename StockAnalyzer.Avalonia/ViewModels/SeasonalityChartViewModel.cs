using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using SkiaSharp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>View state for the DI-backed seasonality polar chart ("Seasonality Clock") panel.</summary>
public partial class SeasonalityChartViewModel : ViewModelBase, IDisposable
{
    private static string IdleStatus => LocalizationManager.Instance["Seasonality_Status_Idle"];
    private static readonly TimeSpan DefaultReanalyzeDebounce = TimeSpan.FromMilliseconds(350);

    /// <summary>Defaults used when no settings manager is supplied (design-time / unit tests).</summary>
    private static readonly GlobalChartSettings FallbackSeasonalitySettings = new GlobalChartSettings().Validate();

    private readonly ISeasonalityChartDataSource _dataSource;
    private readonly IChartSettingsManager? _chartSettingsManager;
    private int _reanalyzeToken;
    private bool _reanalyzeQueued;
    private bool _disposed;

    /// <summary>
    /// Guards the two-way sync between <see cref="YearsToOverlay"/> and the persisted
    /// <see cref="GlobalChartSettings.SeasonalityYearsToOverlay"/>: set while this view model is the
    /// one pushing the value, so the settings-changed echo does not write it straight back.
    /// </summary>
    private bool _syncingYearsToOverlay;

    /// <summary>
    /// Session-only per-year overlay state keyed by calendar year (decision D3): visibility and an
    /// optional stroke-width override. Survives a same-symbol re-analysis and a display-mode switch;
    /// cleared when the active symbol changes. Never persisted — only the common line thickness is.
    /// </summary>
    private readonly Dictionary<int, (bool IsVisible, float? Override)> _yearRowState = new();

    /// <summary>The symbol <see cref="_yearRowState"/> was last built for; a change resets that state.</summary>
    private string? _yearRowSymbol;

    /// <summary><see cref="YearPalette"/> converted to Skia colours, cached against the list instance.</summary>
    private IReadOnlyList<Color>? _yearPaletteSkKey;
    private SKColor[] _yearPaletteSk = Array.Empty<SKColor>();

    [ObservableProperty] private uint _yearsToOverlay = ChartSettingsConstants.DefaultSeasonalityYearsToOverlay;
    [ObservableProperty] private bool _hasYearRows;
    [ObservableProperty] private decimal? _manualBaseRadius;
    [ObservableProperty] private decimal? _manualScaleFactor;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _hasSeriesRows;
    [ObservableProperty] private bool _showMeanPath;
    [ObservableProperty] private uint? _gridOpacityPercent = 60;
    [ObservableProperty] private bool _gridDashed;
    [ObservableProperty] private bool _useVisibleRangeOrigin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrawnResult))]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private SeasonalityChartResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = IdleStatus;

    /// <summary>
    /// Which renderer the single display area shows. Switching only swaps the visible control — it
    /// never re-runs the analysis (the engine output is identical for every mode).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPolarClock))]
    [NotifyPropertyChangedFor(nameof(IsLinearOverlay))]
    [NotifyPropertyChangedFor(nameof(IsMonthlyReturnsTable))]
    [NotifyPropertyChangedFor(nameof(IsMeanPathSettingEnabled))]
    private SeasonalityDisplayMode _displayMode = SeasonalityDisplayMode.PolarClock;

    /// <summary>The month-by-year returns table projected for display; null until a result exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMonthlyReturns))]
    private SeasonalityMonthlyReturnsPresentation? _monthlyReturns;

    /// <summary>
    /// Quiet period after a parameter change before the automatic re-analysis runs, so a burst of
    /// edits (spinner hold, drag) collapses to one recompute. Tests set <see cref="TimeSpan.Zero"/>
    /// for deterministic timing — via the constructor's <c>reanalyzeDebounce</c> argument when the
    /// constructor's own first schedule must be exercised, or via an object initializer otherwise.
    /// </summary>
    internal TimeSpan ReanalyzeDebounceInterval { get; set; } = DefaultReanalyzeDebounce;

    /// <summary>
    /// The radius-normalization modes offered in the per-series picker (decision D4). "Signed unit from
    /// bounds" is omitted: the seasonality chart draws only the base price series, which carries no
    /// explicit bounds, so that mode resolves back to the default and is not a meaningful choice here.
    /// </summary>
    public static IReadOnlyList<SeasonalityRadiusMode> RadiusModes { get; } = new[]
    {
        SeasonalityRadiusMode.PercentVsYearStart,
        SeasonalityRadiusMode.ZScoreWithinYear,
    };

    /// <summary>
    /// One row for the drawn base symbol, offering a radius-mode override applied on re-analysis. The
    /// seasonality chart draws only the base symbol, so this collection holds at most one row.
    /// </summary>
    public ObservableCollection<SeasonalitySeriesRow> SeriesRows { get; } = new();

    /// <summary>
    /// One row per overlaid calendar year, newest first: a visibility toggle, the year's overlay
    /// colour swatch, the year, and an optional per-year stroke-width override (blank = inherit the
    /// common <see cref="GlobalChartSettings.SeasonalityLineThickness"/>). Bound by the chart tab's
    /// "Years" settings group (requirement C4). Rebuilt on each analysis, preserving per-year state by
    /// calendar year.
    /// </summary>
    public ObservableCollection<SeasonalityYearRow> YearRows { get; } = new();

    /// <summary>
    /// Immutable per-year draw-state snapshot (visibility + effective stroke width, keyed by calendar
    /// year) handed to both overlay plot controls. A fresh list instance is published whenever a row
    /// changes, so <c>AffectsRender</c> repaints without a re-analysis (decision D6).
    /// </summary>
    public IReadOnlyList<SeasonalityYearDrawState> YearDrawStates { get; private set; } = Array.Empty<SeasonalityYearDrawState>();

    /// <summary>Icon-rail group keys for the collapsible left settings panel.</summary>
    public const string SettingsGroupBasic = "Basic";

    public const string SettingsGroupDisplay = "Display";

    public const string SettingsGroupSeries = "Series";

    public const string SettingsGroupYears = "Years";

    /// <summary>The open left-panel group, or null when the flyout is collapsed to the icon rail only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsFlyoutOpen))]
    [NotifyPropertyChangedFor(nameof(IsBasicGroupOpen))]
    [NotifyPropertyChangedFor(nameof(IsDisplayGroupOpen))]
    [NotifyPropertyChangedFor(nameof(IsSeriesGroupOpen))]
    [NotifyPropertyChangedFor(nameof(IsYearsGroupOpen))]
    private string? _openSettingsGroup;

    public bool IsSettingsFlyoutOpen => !string.IsNullOrEmpty(OpenSettingsGroup);

    public bool IsBasicGroupOpen => string.Equals(OpenSettingsGroup, SettingsGroupBasic, StringComparison.Ordinal);

    public bool IsDisplayGroupOpen => string.Equals(OpenSettingsGroup, SettingsGroupDisplay, StringComparison.Ordinal);

    public bool IsSeriesGroupOpen => string.Equals(OpenSettingsGroup, SettingsGroupSeries, StringComparison.Ordinal);

    public bool IsYearsGroupOpen => string.Equals(OpenSettingsGroup, SettingsGroupYears, StringComparison.Ordinal);

    /// <summary>Opens the given left-panel group, or collapses the flyout when its icon is clicked again.</summary>
    [RelayCommand]
    private void ToggleSettingsGroup(string? group)
        => OpenSettingsGroup = string.Equals(OpenSettingsGroup, group, StringComparison.Ordinal) ? null : group;

    /// <summary>
    /// Collapses the settings flyout back to the icon rail. Invoked when the user clicks into the plot,
    /// so the chart is not left with a panel covering it.
    /// </summary>
    public void CollapseSettingsFlyout() => OpenSettingsGroup = null;

    /// <summary>Lower bound for the chart tab's own "Years to overlay" input (bound by the view).</summary>
    public decimal MinYears => SeasonalityChartConstants.MinYearsToOverlay;

    /// <summary>Upper bound for the chart tab's own "Years to overlay" input (bound by the view).</summary>
    public decimal MaxYears => SeasonalityChartConstants.MaxYearsToOverlay;

    private static readonly IReadOnlyList<SeasonalityDisplayMode> AllDisplayModes = new[]
    {
        SeasonalityDisplayMode.PolarClock,
        SeasonalityDisplayMode.LinearYearOverlay,
        SeasonalityDisplayMode.MonthlyReturnsTable,
    };

    /// <summary>The render modes offered in the Display settings (definition D-A). Exactly one is shown at a time.</summary>
    public IReadOnlyList<SeasonalityDisplayMode> DisplayModes => AllDisplayModes;

    /// <summary>True while the clockwise polar clock renderer is selected.</summary>
    public bool IsPolarClock => DisplayMode == SeasonalityDisplayMode.PolarClock;

    /// <summary>True while the left-to-right line overlay renderer is selected.</summary>
    public bool IsLinearOverlay => DisplayMode == SeasonalityDisplayMode.LinearYearOverlay;

    /// <summary>True while the month-by-year returns table is selected (no plot is drawn).</summary>
    public bool IsMonthlyReturnsTable => DisplayMode == SeasonalityDisplayMode.MonthlyReturnsTable;

    /// <summary>
    /// True once an overlay has actually been drawn (a result with at least one year trace). The
    /// on-chart status line is hidden in this state — it only carries the empty / loading / error
    /// guidance, not an "overlaid N years" caption over a populated chart.
    /// </summary>
    public bool HasDrawnResult => Result is { Traces.Count: > 0 };

    /// <summary>
    /// Whether the on-chart status line has anything worth showing. It carries only the empty /
    /// loading / error guidance, so it stays hidden once an overlay is drawn and also while a symbol
    /// is active but no result exists yet — that state is transient (an analysis is already
    /// scheduled) and must not flash a "Ready for ..." caption over the pending chart.
    /// </summary>
    public bool HasStatusText => !HasDrawnResult && !string.IsNullOrEmpty(StatusText);

    /// <summary>
    /// Whether the "mean seasonal path" toggle is meaningful. The linear overlay does not draw the
    /// mean path (confirmation Q3), so the setting is disabled while that mode is active.
    /// </summary>
    public bool IsMeanPathSettingEnabled => DisplayMode == SeasonalityDisplayMode.PolarClock;

    /// <summary>True when <see cref="MonthlyReturns"/> holds a table with at least one overlaid year.</summary>
    public bool HasMonthlyReturns => MonthlyReturns is { HasData: true };

    /// <summary>
    /// Per-year overlay colours from the Seasonality settings page (index 0 = newest year, ten slots),
    /// bound by the plot controls. Refreshed whenever the settings manager raises a change.
    /// </summary>
    public IReadOnlyList<Color> YearPalette { get; private set; } = Array.Empty<Color>();

    /// <summary>Legend text size (points) for both plot renderers, from the Seasonality settings page.</summary>
    public double LegendFontSize { get; private set; }

    /// <summary>Radius (DIP) of the circular endpoint marker for the newest year in Polar Clock mode.</summary>
    public float EndpointMarkerRadius { get; private set; }

    /// <summary>Month / grid label size (points) for both plot renderers, from the Seasonality settings page.</summary>
    public double AxisFontSize { get; private set; }

    /// <summary>Monthly-returns table font size (points), from the Seasonality settings page.</summary>
    public double MonthlyTableFontSize { get; private set; }

    /// <summary>Resolved brushes + font size handed to <see cref="SeasonalityMonthlyReturnsPresentation.Create"/>.</summary>
    public SeasonalityMonthlyReturnsStyle MonthlyReturnsStyle { get; private set; } = SeasonalityMonthlyReturnsStyle.Default;

    /// <summary>
    /// The three Data-tab sign-bucket colours (monthly-returns up / down / neutral), also used by the
    /// plot legend to colour each year's annual-return value. Same source as
    /// <see cref="MonthlyReturnsStyle"/> so the legend and the table never disagree.
    /// </summary>
    public Color ReturnPositiveColor { get; private set; }

    /// <inheritdoc cref="ReturnPositiveColor"/>
    public Color ReturnNegativeColor { get; private set; }

    /// <inheritdoc cref="ReturnPositiveColor"/>
    public Color ReturnNeutralColor { get; private set; }

    public SeasonalityChartViewModel(
        ISeasonalityChartDataSource dataSource,
        IChartSettingsManager? chartSettingsManager = null,
        TimeSpan? reanalyzeDebounce = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _chartSettingsManager = chartSettingsManager;
        // The constructor itself schedules the first overlay (see ScheduleReanalyzeIfResultPending
        // below), so the debounce seam has to be applied here, before that call — an object
        // initializer runs only after the constructor body.
        ReanalyzeDebounceInterval = reanalyzeDebounce ?? DefaultReanalyzeDebounce;
        RefreshSeasonalitySettings();

        // Seed the overlay depth from the persisted setting directly (no notification / no
        // re-analysis): the constructor schedules its own first overlay below.
        _yearsToOverlay = SeasonalitySettings.SeasonalityYearsToOverlay;

        _dataSource.Changed += OnDataSourceChanged;
        if (_chartSettingsManager is not null)
        {
            _chartSettingsManager.SettingsChanged += OnSeasonalitySettingsChanged;
        }

        UpdateFromDataSource();

        // A symbol can already be active when this panel is constructed: the startup workspace
        // restore sets ChartViewModel.Symbol (raising OnSymbolChanged -> SetActiveSymbol) before the
        // Seasonality tab's view model exists, so the data source's one-shot Changed event has
        // already fired and been missed. Without this the panel would sit idle until the user
        // changed the symbol again.
        ScheduleReanalyzeIfResultPending();
    }

    private GlobalChartSettings SeasonalitySettings => _chartSettingsManager?.Current ?? FallbackSeasonalitySettings;

    /// <summary>
    /// Recomputes every settings-derived property from the current <see cref="GlobalChartSettings"/>.
    /// Pure recomputation — does not raise change notifications; callers decide.
    /// </summary>
    private void RefreshSeasonalitySettings()
    {
        GlobalChartSettings settings = SeasonalitySettings;
        EndpointMarkerRadius = settings.SeasonalityEndpointMarkerRadius;
        LegendFontSize = settings.SeasonalityLegendFontSize;
        AxisFontSize = settings.SeasonalityAxisFontSize;
        MonthlyTableFontSize = settings.SeasonalityMonthlyTableFontSize;
        YearPalette = BuildYearPalette(settings);
        MonthlyReturnsStyle = BuildMonthlyReturnsStyle(settings);
        ReturnPositiveColor = ColorFromHex(settings.SeasonalityMonthlyUpColor, ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor);
        ReturnNegativeColor = ColorFromHex(settings.SeasonalityMonthlyDownColor, ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor);
        ReturnNeutralColor = ColorFromHex(settings.SeasonalityMonthlyNeutralColor, ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor);
    }

    private static IReadOnlyList<Color> BuildYearPalette(GlobalChartSettings settings)
    {
        string[] hex =
        {
            settings.SeasonalityYearColor1, settings.SeasonalityYearColor2, settings.SeasonalityYearColor3,
            settings.SeasonalityYearColor4, settings.SeasonalityYearColor5, settings.SeasonalityYearColor6,
            settings.SeasonalityYearColor7, settings.SeasonalityYearColor8, settings.SeasonalityYearColor9,
            settings.SeasonalityYearColor10,
        };

        var colors = new Color[hex.Length];
        for (int index = 0; index < hex.Length; index++)
        {
            colors[index] = HsvData.FromHtmlSafe(hex[index]).ToIndicatorColor().ToAvaloniaColor();
        }

        return colors;
    }

    private static SeasonalityMonthlyReturnsStyle BuildMonthlyReturnsStyle(GlobalChartSettings settings)
    {
        IBrush up = BrushFromHex(settings.SeasonalityMonthlyUpColor, ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor);
        IBrush down = BrushFromHex(settings.SeasonalityMonthlyDownColor, ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor);
        IBrush neutral = BrushFromHex(settings.SeasonalityMonthlyNeutralColor, ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor);
        return new SeasonalityMonthlyReturnsStyle(
            up, down, neutral,
            Brushes.White, Brushes.White, Brushes.White,
            settings.SeasonalityMonthlyTableFontSize);
    }

    private static IBrush BrushFromHex(string hex, string fallbackHex)
        => new ImmutableSolidColorBrush(ColorFromHex(hex, fallbackHex));

    /// <summary>Resolves a settings hex string (with a hex fallback) to an Avalonia colour — the shared
    /// path behind <see cref="BrushFromHex"/> and the legend sign colours, so both read the same rule.</summary>
    private static Color ColorFromHex(string hex, string fallbackHex)
        => HsvData.FromHtmlSafe(hex, HsvData.FromHtmlSafe(fallbackHex)).ToIndicatorColor().ToAvaloniaColor();

    private void OnSeasonalitySettingsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            HandleSeasonalitySettingsChanged();
            return;
        }

        Dispatcher.UIThread.Post(HandleSeasonalitySettingsChanged);
    }

    private void HandleSeasonalitySettingsChanged()
    {
        if (_disposed)
        {
            return;
        }

        RefreshSeasonalitySettings();

        // The overlay depth lives on GlobalChartSettings (its persistent single source of truth); a
        // change from the Settings page arrives here. Pushing it through the property re-analyses via
        // OnYearsToOverlayChanged; the guard keeps that from writing the same value straight back.
        uint settingsYears = SeasonalitySettings.SeasonalityYearsToOverlay;
        if (settingsYears != YearsToOverlay)
        {
            _syncingYearsToOverlay = true;
            try
            {
                YearsToOverlay = settingsYears;
            }
            finally
            {
                _syncingYearsToOverlay = false;
            }
        }

        if (Result is { } currentResult && currentResult.Parameters.StartMonth != SeasonalitySettings.SeasonalityStartMonth)
        {
            ScheduleReanalyze();
        }

        MonthlyReturns = BuildMonthlyReturns();

        // The overlay palette and the common line thickness both live on the settings; rebuild the
        // year rows so swatches and inherited thicknesses follow, keeping each year's session state.
        RebuildYearRows();

        OnPropertyChanged(nameof(EndpointMarkerRadius));
        OnPropertyChanged(nameof(YearPalette));
        OnPropertyChanged(nameof(LegendFontSize));
        OnPropertyChanged(nameof(AxisFontSize));
        OnPropertyChanged(nameof(MonthlyTableFontSize));
        OnPropertyChanged(nameof(MonthlyReturnsStyle));
        OnPropertyChanged(nameof(ReturnPositiveColor));
        OnPropertyChanged(nameof(ReturnNegativeColor));
        OnPropertyChanged(nameof(ReturnNeutralColor));
    }

    /// <summary>Builds the monthly-returns projection for the current result with the current style, or null.</summary>
    private SeasonalityMonthlyReturnsPresentation? BuildMonthlyReturns()
        => Result is { } currentResult
            ? SeasonalityMonthlyReturnsPresentation.Create(
                SeasonalityMonthlyReturnsEngine.Build(currentResult),
                LocalizationManager.Instance["Seasonality_MonthlyReturns_Annual"],
                LocalizationManager.Instance["Seasonality_MonthlyReturns_Average"],
                MonthlyReturnsStyle,
                SeasonalitySettings.SeasonalityMonthlyTableOrientation)
            : null;

    /// <summary>
    /// Runs one seasonality analysis for the current parameters. Not a bound command — the view has no
    /// analyze button; every trigger flows through <see cref="ScheduleReanalyze"/> (debounced) or a
    /// data-source change. Kept internally callable so tests can drive one run deterministically.
    /// </summary>
    internal async Task RunAnalysisAsync()
    {
        if (IsAnalyzing)
        {
            return;
        }

        if (!_dataSource.HasActiveSymbol)
        {
            StatusText = LocalizationManager.Instance["Seasonality_Status_NoSymbol"];
            return;
        }

        SeasonalityChartParameters parameters = new(YearsToOverlay, ManualBaseRadius, ManualScaleFactor, ShowMeanPath, SeasonalitySettings.SeasonalityStartMonth);
        try
        {
            parameters.Validate();
        }
        catch (ArgumentException exception)
        {
            StatusText = exception.Message;
            return;
        }

        try
        {
            IsAnalyzing = true;
            StatusText = string.Format(
                CultureInfo.InvariantCulture,
                LocalizationManager.Instance["Seasonality_Status_Analyzing"], _dataSource.ActiveSymbol);
            _dataSource.SetRadiusModeOverrides(BuildRadiusModeOverrides());
            await _dataSource.AnalyzeAsync(parameters);
            IsAnalyzing = false;
            UpdateFromDataSource();
        }
        catch (Exception exception)
        {
            // Every failure path has to clear IsAnalyzing and say what went wrong. This runs on the
            // fire-and-forget ScheduleReanalyze task, so an escaping exception is silently lost and
            // the panel stays wedged at "Analyzing ..." forever (e.g. the active symbol has no daily
            // history on disk, or the data provider throws while resolving the file).
            IsAnalyzing = false;
            StatusText = exception is InvalidOperationException or ArgumentException
                ? exception.Message
                : string.Format(
                    CultureInfo.InvariantCulture,
                    LocalizationManager.Instance["Seasonality_Status_AnalyzeFailed"],
                    _dataSource.ActiveSymbol,
                    exception.GetType().Name);
        }
    }

    private void OnDataSourceChanged(object? sender, EventArgs eventArgs)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            HandleDataSourceChanged();
            return;
        }

        Dispatcher.UIThread.Post(HandleDataSourceChanged);
    }

    private void HandleDataSourceChanged()
    {
        UpdateFromDataSource();

        // The bridge cleared the result because the active symbol, comparison set or indicator set
        // changed on the chart. Pull a fresh overlay automatically rather than waiting for a manual
        // trigger that no longer exists.
        ScheduleReanalyzeIfResultPending();
    }

    private void UpdateFromDataSource()
    {
        Result = _dataSource.Current;
        MonthlyReturns = BuildMonthlyReturns();
        RebuildSeriesRows();
        RebuildYearRows();
        if (IsAnalyzing)
        {
            return;
        }

        StatusText = _dataSource switch
        {
            { Current: { } result } => result.Traces.Count == 0
                ? LocalizationManager.Instance["Seasonality_Status_NoData"]
                : string.Format(
                    CultureInfo.InvariantCulture,
                    LocalizationManager.Instance["Seasonality_Status_Overlaid"],
                    result.CalendarYears.Count,
                    _dataSource.ActiveSymbol),
            // An active symbol with no result yet needs no caption: the constructor and
            // HandleDataSourceChanged always schedule an analysis for this state, so the chart is
            // about to appear. A "Ready for ..." line here just flashes and pushes the plot down.
            { HasActiveSymbol: true } => string.Empty,
            _ => IdleStatus
        };
    }

    /// <summary>The current per-series mode selections, keyed by series id, for the data source.</summary>
    private IReadOnlyDictionary<int, SeasonalityRadiusMode> BuildRadiusModeOverrides()
    {
        var map = new Dictionary<int, SeasonalityRadiusMode>(SeriesRows.Count);
        foreach (SeasonalitySeriesRow row in SeriesRows)
        {
            map[row.SeriesId] = row.RadiusMode;
        }

        return map;
    }

    // Every parameter that changes the computed overlay funnels into the debounced scheduler; the
    // pure-render options (GridOpacityPercent / GridDashed / UseVisibleRangeOrigin) intentionally have
    // no hook here — the control re-renders from them without a re-analysis.
    partial void OnYearsToOverlayChanged(uint value)
    {
        PersistYearsToOverlay(value);
        ScheduleReanalyze();
    }

    /// <summary>
    /// Writes the chart tab's overlay-depth edit back onto <see cref="GlobalChartSettings"/> so the
    /// Settings page and this input never hold two different effective values (decision D4). Uses the
    /// in-memory preview channel — disk persistence stays with the Settings page's own save.
    /// </summary>
    private void PersistYearsToOverlay(uint value)
    {
        if (_chartSettingsManager is null || _syncingYearsToOverlay)
        {
            return;
        }

        GlobalChartSettings current = _chartSettingsManager.Current;
        if (current.SeasonalityYearsToOverlay == value)
        {
            return;
        }

        _syncingYearsToOverlay = true;
        try
        {
            _chartSettingsManager.UpdatePreview(current with { SeasonalityYearsToOverlay = value });
        }
        finally
        {
            _syncingYearsToOverlay = false;
        }
    }

    partial void OnManualBaseRadiusChanged(decimal? value) => ScheduleReanalyze();

    partial void OnManualScaleFactorChanged(decimal? value) => ScheduleReanalyze();

    partial void OnShowMeanPathChanged(bool value) => ScheduleReanalyze();

    private void OnSeriesRowRadiusModeChanged(SeasonalitySeriesRow row) => ScheduleReanalyze();

    /// <summary>
    /// Shared recovery path: when a symbol is active but no result exists yet and nothing is running,
    /// pull the first overlay. Called from the constructor (a tab created after the symbol was set
    /// missed the data source's one-shot <see cref="ISeasonalityChartDataSource.Changed"/>) and from
    /// <see cref="HandleDataSourceChanged"/> (the bridge cleared the result on a symbol change).
    /// </summary>
    private void ScheduleReanalyzeIfResultPending()
    {
        if (_dataSource.Current is null && _dataSource.HasActiveSymbol && !IsAnalyzing)
        {
            ScheduleReanalyze();
        }
    }

    /// <summary>
    /// Queues an automatic re-analysis after <see cref="ReanalyzeDebounceInterval"/> of quiet. A newer
    /// call supersedes an older pending one (generation token); a call while a run is in flight is
    /// remembered and honoured once it finishes.
    /// </summary>
    private void ScheduleReanalyze()
    {
        if (_disposed || !_dataSource.HasActiveSymbol)
        {
            return;
        }

        if (IsAnalyzing)
        {
            _reanalyzeQueued = true;
            return;
        }

        int token = ++_reanalyzeToken;
        _ = RunDebouncedAsync(token);
    }

    private async Task RunDebouncedAsync(int token)
    {
        if (ReanalyzeDebounceInterval > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(ReanalyzeDebounceInterval).ConfigureAwait(true);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        if (_disposed || token != _reanalyzeToken || IsAnalyzing)
        {
            return;
        }

        await RunAnalysisAsync().ConfigureAwait(true);

        if (_reanalyzeQueued && !_disposed)
        {
            _reanalyzeQueued = false;
            ScheduleReanalyze();
        }
    }

    /// <summary>
    /// Reconciles <see cref="SeriesRows"/> with the latest result: keeps existing rows (only refreshing
    /// the mode shown, silently) when the series-id layout is unchanged, otherwise rebuilds them. A
    /// null or empty result clears the list.
    /// </summary>
    private void RebuildSeriesRows()
    {
        SeasonalityChartResult? result = Result;
        if (result is null || result.Traces.Count == 0)
        {
            if (SeriesRows.Count > 0)
            {
                SeriesRows.Clear();
            }

            HasSeriesRows = false;
            return;
        }

        var seen = new HashSet<int>();
        var distinctSeries = new List<SeasonalityYearTrace>();
        foreach (SeasonalityYearTrace trace in result.Traces)
        {
            if (seen.Add(trace.SeriesId))
            {
                distinctSeries.Add(trace);
            }
        }

        bool sameLayout = SeriesRows.Count == distinctSeries.Count;
        if (sameLayout)
        {
            for (int i = 0; i < distinctSeries.Count; i++)
            {
                if (SeriesRows[i].SeriesId != distinctSeries[i].SeriesId)
                {
                    sameLayout = false;
                    break;
                }
            }
        }

        if (sameLayout)
        {
            for (int i = 0; i < distinctSeries.Count; i++)
            {
                SeriesRows[i].SetRadiusModeSilently(distinctSeries[i].RadiusMode);
            }
        }
        else
        {
            SeriesRows.Clear();
            foreach (SeasonalityYearTrace trace in distinctSeries)
            {
                SeriesRows.Add(new SeasonalitySeriesRow(
                    trace.SeriesId, trace.SeriesLabel, trace.RadiusMode, OnSeriesRowRadiusModeChanged));
            }
        }

        HasSeriesRows = SeriesRows.Count > 0;
    }

    /// <summary>
    /// Reconciles <see cref="YearRows"/> with the latest result's overlaid calendar years (newest
    /// first, so a row's index is its recency rank), carrying each year's session visibility /
    /// thickness-override state across the rebuild by <see cref="SeasonalityYearTrace.CalendarYear"/>.
    /// That session state is cleared first when the active symbol changed (decision D3). A null /
    /// empty result clears the rows. Always republishes <see cref="YearDrawStates"/>.
    /// </summary>
    private void RebuildYearRows()
    {
        string? symbol = _dataSource.ActiveSymbol;
        if (!string.Equals(symbol, _yearRowSymbol, StringComparison.OrdinalIgnoreCase))
        {
            _yearRowState.Clear();
            _yearRowSymbol = symbol;
        }

        SeasonalityChartResult? result = Result;
        if (result is null || result.CalendarYears.Count == 0)
        {
            if (YearRows.Count > 0)
            {
                YearRows.Clear();
            }

            HasYearRows = false;
            PublishYearDrawStates();
            return;
        }

        float commonThickness = SeasonalitySettings.SeasonalityLineThickness;
        SKColor[] paletteSk = ResolveYearPaletteSk();
        IReadOnlyList<int> years = result.CalendarYears; // ascending

        YearRows.Clear();
        for (int index = 0; index < years.Count; index++)
        {
            int year = years[years.Count - 1 - index]; // newest first; index == recency rank
            (bool isVisible, float? overrideThickness) = _yearRowState.TryGetValue(year, out (bool IsVisible, float? Override) state)
                ? state
                : (true, (float?)null);

            YearRows.Add(new SeasonalityYearRow(
                year,
                ResolveYearSwatchBrush(index, paletteSk),
                commonThickness,
                isVisible,
                overrideThickness,
                OnYearRowChanged));
        }

        HasYearRows = YearRows.Count > 0;
        PublishYearDrawStates();
    }

    /// <summary>Records a row's edited session state and republishes the draw-state snapshot (no re-analysis — decision D6).</summary>
    private void OnYearRowChanged(SeasonalityYearRow row)
    {
        _yearRowState[row.CalendarYear] = (row.IsVisible, row.LineThicknessOverride);
        PublishYearDrawStates();
    }

    /// <summary>Publishes a fresh immutable per-year draw-state list so the bound plot controls repaint.</summary>
    private void PublishYearDrawStates()
    {
        if (YearRows.Count == 0)
        {
            YearDrawStates = Array.Empty<SeasonalityYearDrawState>();
            OnPropertyChanged(nameof(YearDrawStates));
            return;
        }

        var states = new SeasonalityYearDrawState[YearRows.Count];
        for (int index = 0; index < YearRows.Count; index++)
        {
            SeasonalityYearRow row = YearRows[index];
            states[index] = new SeasonalityYearDrawState(row.CalendarYear, row.IsVisible, row.EffectiveLineThickness);
        }

        YearDrawStates = states;
        OnPropertyChanged(nameof(YearDrawStates));
    }

    /// <summary>
    /// <see cref="YearPalette"/> as Skia colours, cached against the list instance so a rebuild
    /// allocates nothing while the palette is unchanged.
    /// </summary>
    private SKColor[] ResolveYearPaletteSk()
    {
        IReadOnlyList<Color> palette = YearPalette;
        if (!ReferenceEquals(palette, _yearPaletteSkKey) || _yearPaletteSk.Length != palette.Count)
        {
            var converted = new SKColor[palette.Count];
            for (int index = 0; index < palette.Count; index++)
            {
                Color color = palette[index];
                converted[index] = new SKColor(color.R, color.G, color.B, color.A);
            }

            _yearPaletteSkKey = palette;
            _yearPaletteSk = converted;
        }

        return _yearPaletteSk;
    }

    /// <summary>
    /// The swatch brush for the row at <paramref name="recencyRank"/>, derived through the shared
    /// <see cref="SeasonalitySharedAxisFormatting.ResolveYearColor"/> so the panel swatch matches the
    /// trace hue the plot draws (including the tint/shade past the palette size).
    /// </summary>
    private static IBrush ResolveYearSwatchBrush(int recencyRank, IReadOnlyList<SKColor> paletteSk)
    {
        // The theme argument is only consulted when the palette is empty (design-time). The
        // Seasonality settings palette always has ten slots, so ThemeColors.Dark is never read here.
        SKColor color = SeasonalitySharedAxisFormatting.ResolveYearColor(recencyRank, paletteSk, ThemeColors.Dark);
        return new ImmutableSolidColorBrush(new Color(color.Alpha, color.Red, color.Green, color.Blue));
    }

    public void Dispose()
    {
        _disposed = true;
        _reanalyzeToken++;
        _dataSource.Changed -= OnDataSourceChanged;
        if (_chartSettingsManager is not null)
        {
            _chartSettingsManager.SettingsChanged -= OnSeasonalitySettingsChanged;
        }
    }
}

/// <summary>
/// One drawn seasonality series in the "Seasonality Clock" side panel. Changing <see cref="RadiusMode"/>
/// via the UI raises the supplied callback so the view model can re-analyze with the new override; a
/// programmatic refresh from a fresh result uses <see cref="SetRadiusModeSilently"/> to avoid a loop.
/// </summary>
public sealed partial class SeasonalitySeriesRow : ObservableObject
{
    private readonly Action<SeasonalitySeriesRow>? _onRadiusModeChanged;
    private bool _suppressCallback;

    public SeasonalitySeriesRow(
        int seriesId,
        string label,
        SeasonalityRadiusMode radiusMode,
        Action<SeasonalitySeriesRow>? onRadiusModeChanged = null)
    {
        SeriesId = seriesId;
        Label = label ?? string.Empty;
        _radiusMode = radiusMode;
        _onRadiusModeChanged = onRadiusModeChanged;
    }

    public int SeriesId { get; }

    public string Label { get; }

    /// <summary>The modes to choose from, exposed here so the item template can bind directly.</summary>
    public IReadOnlyList<SeasonalityRadiusMode> RadiusModes => SeasonalityChartViewModel.RadiusModes;

    [ObservableProperty] private SeasonalityRadiusMode _radiusMode;

    partial void OnRadiusModeChanged(SeasonalityRadiusMode value)
    {
        if (_suppressCallback)
        {
            return;
        }

        _onRadiusModeChanged?.Invoke(this);
    }

    internal void SetRadiusModeSilently(SeasonalityRadiusMode value)
    {
        if (value == RadiusMode)
        {
            return;
        }

        _suppressCallback = true;
        RadiusMode = value;
        _suppressCallback = false;
    }
}

/// <summary>
/// One overlaid calendar year in the "Seasonality Clock" side panel (requirement C4): a visibility
/// toggle, the year's overlay-colour swatch, the year, and an optional per-year stroke-width
/// override (blank inherits the common <see cref="GlobalChartSettings.SeasonalityLineThickness"/>).
/// Editing a value raises the supplied callback so the view model records the session state and
/// repaints. Only the common thickness persists — a row's override lives for the session (decision D3).
/// </summary>
public sealed partial class SeasonalityYearRow : ObservableObject
{
    private readonly Action<SeasonalityYearRow>? _onChanged;
    private readonly float _commonLineThickness;

    public SeasonalityYearRow(
        int calendarYear,
        IBrush swatchBrush,
        float commonLineThickness,
        bool isVisible,
        float? lineThicknessOverride,
        Action<SeasonalityYearRow>? onChanged = null)
    {
        CalendarYear = calendarYear;
        _swatchBrush = swatchBrush;
        _commonLineThickness = commonLineThickness;
        _isVisible = isVisible;
        _lineThicknessOverride = lineThicknessOverride;
        _onChanged = onChanged;
    }

    /// <summary>The calendar year this row controls; the stable key for its session state.</summary>
    public int CalendarYear { get; }

    /// <summary>Selectable per-year stroke-width bounds / step for the spinner, from the settings SSoT.</summary>
    public decimal MinLineThickness => (decimal)ChartSettingsConstants.MinSeasonalityLineThickness;

    public decimal MaxLineThickness => (decimal)ChartSettingsConstants.MaxSeasonalityLineThickness;

    public decimal LineThicknessStep => (decimal)ChartSettingsConstants.SeasonalityLineThicknessStep;

    /// <summary>Watermark for the override spinner: the common thickness this row inherits when blank.</summary>
    public string CommonLineThicknessText =>
        _commonLineThickness.ToString("0.0", CultureInfo.CurrentCulture);

    [ObservableProperty]
    private IBrush _swatchBrush;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveLineThickness))]
    private float? _lineThicknessOverride;

    /// <summary>The stroke width actually used for this year: the override, or the common thickness.</summary>
    public float EffectiveLineThickness => LineThicknessOverride ?? _commonLineThickness;

    partial void OnIsVisibleChanged(bool value) => _onChanged?.Invoke(this);

    partial void OnLineThicknessOverrideChanged(float? value) => _onChanged?.Invoke(this);
}
