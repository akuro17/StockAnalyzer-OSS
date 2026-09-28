using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class DotSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_Dot";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _baseRadius = ChartSettingsConstants.DefaultDotBaseRadius;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _upColor = new(1, 120, 1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _downColor = new(1, 0, 1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private HsvData _naturalColor = new(1, 0, 0, 0.5);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private DotShapeType _shapeType = DotShapeType.Circle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private PriceType _priceType = PriceType.Close;

    public IReadOnlyList<DotShapeType> AvailableShapeTypes { get; } = Enum.GetValues<DotShapeType>();
    public IReadOnlyList<PriceType> AvailablePriceTypes => PriceDataHelper.PriceTypeOptions;

    private double _initialBaseRadius;
    private HsvData _initialUpColor;
    private HsvData _initialDownColor;
    private HsvData _initialNaturalColor;
    private DotShapeType _initialShapeType;
    private PriceType _initialPriceType;

    public DotSettingsViewModel(IChartSettingsManager settingsManager, ILogger<DotSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        BaseRadius = settings.DotBaseRadius;
        UpColor = HsvData.FromHtmlSafe(settings.DotUpColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBullishColor));
        DownColor = HsvData.FromHtmlSafe(settings.DotDownColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBearishColor));
        NaturalColor = HsvData.FromHtmlSafe(settings.DotNaturalColor, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultNeutralColor));
        ShapeType = settings.DotShape;
        PriceType = settings.DotPriceType;
    }

    protected override void UpdateInitialState()
    {
        _initialBaseRadius = BaseRadius;
        _initialUpColor = UpColor;
        _initialDownColor = DownColor;
        _initialNaturalColor = NaturalColor;
        _initialShapeType = ShapeType;
        _initialPriceType = PriceType;
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            DotBaseRadius = BaseRadius,
            DotUpColor = UpColor.ToHtml(),
            DotDownColor = DownColor.ToHtml(),
            DotNaturalColor = NaturalColor.ToHtml(),
            DotShape = ShapeType,
            DotPriceType = PriceType
        };
    }

    public override bool IsModified =>
        Math.Abs(BaseRadius - _initialBaseRadius) > 0.001 ||
        UpColor != _initialUpColor ||
        DownColor != _initialDownColor ||
        NaturalColor != _initialNaturalColor ||
        ShapeType != _initialShapeType ||
        PriceType != _initialPriceType;
}
