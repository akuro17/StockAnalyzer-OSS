using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Services;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ChartViewModel
{
    private readonly IModelAnalysisService? _modelAnalysisService;
    internal Task PredictionUpdateTask { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private ModelAnalysisSnapshot? _modelAnalysis;
    [ObservableProperty] private string? _regimeDisplay;
    [ObservableProperty] private string? _trainPeriodTooltip;
    [ObservableProperty] private string? _validationPeriodTooltip;
    [ObservableProperty] private string? _oosPeriodTooltip;
    [ObservableProperty] private double _periodLegendLeft;
    [ObservableProperty] private double _periodLegendTop;
    public bool HasPeriodDisplay => !string.IsNullOrEmpty(TrainPeriodTooltip);
    public bool HasOosPeriodDisplay => !string.IsNullOrEmpty(OosPeriodTooltip);

    partial void OnModelAnalysisChanged(ModelAnalysisSnapshot? value)
    {
        string? Tip(string kind)
        {
            var period = value?.Analysis.Periods.FirstOrDefault(p => p.Symbol == Symbol && p.Kind == kind);
            return period is null ? null : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LocalizationManager.Instance.Get("ModelAnalysis_PeriodTooltip"), period.AnchorStart,
                period.AnchorEnd, period.SampleCount, value!.Analysis.DataRevision);
        }
        TrainPeriodTooltip = Tip("train"); ValidationPeriodTooltip = Tip("validation"); OosPeriodTooltip = Tip("oos");
        OnPropertyChanged(nameof(HasPeriodDisplay)); OnPropertyChanged(nameof(HasOosPeriodDisplay));
    }

    private void ClearModelAnalysis()
    {
        ModelAnalysis = null;
        RegimeDisplay = null;
    }
}
