using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Settings page for the Seasonality Chart tab: the legend / axis / monthly-table font sizes, the
/// monthly-returns table up / down / neutral cell colors, and the ten per-year overlay palette slots. All
/// values persist on <see cref="GlobalChartSettings"/>; the page is a top-level Settings category
/// shown directly above "Chart".
/// </summary>
public partial class SeasonalitySettingsViewModel : BaseChartSettingsViewModel
{
    private const float FontEpsilon = 0.01f;

    public override string TitleKey => "Tab_SeasonalityChart";
    public override string IconKey => "SettingsChartIcon";

    /// <summary>Selectable overlay-depth bounds for the spinner, from the analysis SSoT.</summary>
    public decimal MinYearsToOverlay => SeasonalityChartConstants.MinYearsToOverlay;

    public decimal MaxYearsToOverlay => SeasonalityChartConstants.MaxYearsToOverlay;

    /// <summary>Selectable start month bounds for the spinner (1 = Jan, 12 = Dec).</summary>
    public decimal MinStartMonth => (decimal)ChartSettingsConstants.MinSeasonalityStartMonth;

    public decimal MaxStartMonth => (decimal)ChartSettingsConstants.MaxSeasonalityStartMonth;

    /// <summary>Selectable per-year trace stroke-width bounds / step for the spinner, from the settings SSoT.</summary>
    public decimal MinLineThickness => (decimal)ChartSettingsConstants.MinSeasonalityLineThickness;

    public decimal MaxLineThickness => (decimal)ChartSettingsConstants.MaxSeasonalityLineThickness;

    public decimal LineThicknessStep => (decimal)ChartSettingsConstants.SeasonalityLineThicknessStep;

    /// <summary>Selectable endpoint marker radius bounds / step for the spinner, from the settings SSoT.</summary>
    public decimal MinEndpointMarkerRadius => (decimal)ChartSettingsConstants.MinSeasonalityEndpointMarkerRadius;

    public decimal MaxEndpointMarkerRadius => (decimal)ChartSettingsConstants.MaxSeasonalityEndpointMarkerRadius;

    public decimal EndpointMarkerRadiusStep => (decimal)ChartSettingsConstants.SeasonalityEndpointMarkerRadiusStep;

    /// <summary>Orientation options for the monthly returns table (Mode "Monthly").</summary>
    public static IReadOnlyList<SeasonalityMonthlyTableOrientation> TableOrientations { get; } = Enum.GetValues<SeasonalityMonthlyTableOrientation>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private uint _yearsToOverlay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private uint _startMonth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _lineThickness;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _endpointMarkerRadius;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private SeasonalityMonthlyTableOrientation _monthlyTableOrientation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _legendFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _axisFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _monthlyTableFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _monthlyUpColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _monthlyDownColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _monthlyNeutralColor;

    [ObservableProperty]
    private ObservableCollection<HsvData> _yearColors = new();

    private List<HsvData> _initialYearColors = new();

    public SeasonalitySettingsViewModel(
        IChartSettingsManager settingsManager,
        ILogger<SeasonalitySettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    private void OnYearColorsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(IsModified));

    public override bool IsModified
    {
        get
        {
            if (YearsToOverlay != Snapshot.SeasonalityYearsToOverlay ||
                StartMonth != Snapshot.SeasonalityStartMonth ||
                Math.Abs(LineThickness - Snapshot.SeasonalityLineThickness) > FontEpsilon ||
                Math.Abs(EndpointMarkerRadius - Snapshot.SeasonalityEndpointMarkerRadius) > FontEpsilon ||
                MonthlyTableOrientation != Snapshot.SeasonalityMonthlyTableOrientation ||
                Math.Abs(LegendFontSize - Snapshot.SeasonalityLegendFontSize) > FontEpsilon ||
                Math.Abs(AxisFontSize - Snapshot.SeasonalityAxisFontSize) > FontEpsilon ||
                Math.Abs(MonthlyTableFontSize - Snapshot.SeasonalityMonthlyTableFontSize) > FontEpsilon ||
                MonthlyUpColor != HsvData.FromHtmlSafe(Snapshot.SeasonalityMonthlyUpColor) ||
                MonthlyDownColor != HsvData.FromHtmlSafe(Snapshot.SeasonalityMonthlyDownColor) ||
                MonthlyNeutralColor != HsvData.FromHtmlSafe(Snapshot.SeasonalityMonthlyNeutralColor) ||
                YearColors.Count != _initialYearColors.Count)
            {
                return true;
            }

            for (int i = 0; i < YearColors.Count; i++)
            {
                if (YearColors[i] != _initialYearColors[i])
                {
                    return true;
                }
            }

            return false;
        }
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        YearsToOverlay = settings.SeasonalityYearsToOverlay;
        StartMonth = settings.SeasonalityStartMonth;
        LineThickness = settings.SeasonalityLineThickness;
        EndpointMarkerRadius = settings.SeasonalityEndpointMarkerRadius;
        MonthlyTableOrientation = settings.SeasonalityMonthlyTableOrientation;
        LegendFontSize = settings.SeasonalityLegendFontSize;
        AxisFontSize = settings.SeasonalityAxisFontSize;
        MonthlyTableFontSize = settings.SeasonalityMonthlyTableFontSize;
        MonthlyUpColor = HsvData.FromHtmlSafe(
            settings.SeasonalityMonthlyUpColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor));
        MonthlyDownColor = HsvData.FromHtmlSafe(
            settings.SeasonalityMonthlyDownColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor));
        MonthlyNeutralColor = HsvData.FromHtmlSafe(
            settings.SeasonalityMonthlyNeutralColor,
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor));

        var colors = new List<HsvData>
        {
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor1, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor1)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor2, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor2)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor3, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor3)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor4, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor4)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor5, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor5)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor6, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor6)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor7, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor7)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor8, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor8)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor9, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor9)),
            HsvData.FromHtmlSafe(settings.SeasonalityYearColor10, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultSeasonalityYearColor10)),
        };

        // Detach during the bulk replace so the per-item events do not each raise IsModified, then
        // reattach. First call: the '-=' is a harmless no-op and the '+=' is the sole subscription.
        YearColors.CollectionChanged -= OnYearColorsChanged;
        YearColors.Clear();
        foreach (HsvData color in colors)
        {
            YearColors.Add(color);
        }

        YearColors.CollectionChanged += OnYearColorsChanged;
    }

    protected override void UpdateInitialState()
        => _initialYearColors = YearColors.ToList();

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            SeasonalityYearsToOverlay = YearsToOverlay,
            SeasonalityStartMonth = StartMonth,
            SeasonalityLineThickness = LineThickness,
            SeasonalityEndpointMarkerRadius = EndpointMarkerRadius,
            SeasonalityMonthlyTableOrientation = MonthlyTableOrientation,
            SeasonalityLegendFontSize = LegendFontSize,
            SeasonalityAxisFontSize = AxisFontSize,
            SeasonalityMonthlyTableFontSize = MonthlyTableFontSize,
            SeasonalityMonthlyUpColor = MonthlyUpColor.ToHtml(),
            SeasonalityMonthlyDownColor = MonthlyDownColor.ToHtml(),
            SeasonalityMonthlyNeutralColor = MonthlyNeutralColor.ToHtml(),
            SeasonalityYearColor1 = YearColors[0].ToHtml(),
            SeasonalityYearColor2 = YearColors[1].ToHtml(),
            SeasonalityYearColor3 = YearColors[2].ToHtml(),
            SeasonalityYearColor4 = YearColors[3].ToHtml(),
            SeasonalityYearColor5 = YearColors[4].ToHtml(),
            SeasonalityYearColor6 = YearColors[5].ToHtml(),
            SeasonalityYearColor7 = YearColors[6].ToHtml(),
            SeasonalityYearColor8 = YearColors[7].ToHtml(),
            SeasonalityYearColor9 = YearColors[8].ToHtml(),
            SeasonalityYearColor10 = YearColors[9].ToHtml(),
        };
    }
}
