using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public sealed record ImportanceDisplayRow(string Channel, string Display, double PositiveMagnitude,
    double NegativeMagnitude)
{
    public double ScaleMaximum => StockAnalyzer.Core.Models.Training.ModelAnalysisContract.PercentScale;
}

public partial class AIPredictionsSettingsViewModel
{
    private readonly IModelAnalysisService? _modelAnalysisService;
    private readonly IDispatcherService? _analysisDispatcher;
    public ObservableCollection<ImportanceDisplayRow> ImportanceRows { get; } = new();
    [ObservableProperty] private string? _importanceTitle;
    [ObservableProperty] private string? _importanceStatus;
    [ObservableProperty] private string? _importanceProvenance;
    [ObservableProperty] private bool _isAnalysisLoading;
    internal Task AnalysisLoadTask { get; private set; } = Task.CompletedTask;

    private void SelectAnalysis(ModelGenerationSummary? generation, long revision)
    {
        ImportanceRows.Clear(); ImportanceTitle = null; ImportanceProvenance = null;
        ImportanceStatus = LocalizationManager.Instance.Get("ModelAnalysis_Unavailable");
        IsAnalysisLoading = generation is not null && _modelAnalysisService is not null;
        AnalysisLoadTask = LoadAnalysisAsync(generation, revision);
    }

    private async Task LoadAnalysisAsync(ModelGenerationSummary? generation, long revision)
    {
        if (generation is null || _modelAnalysisService is null) return;
        ModelAnalysisSnapshot? snapshot = null;
        try { snapshot = await _modelAnalysisService.LoadAsync(generation.ModelId).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Selected model analysis could not be loaded."); }
        void Publish()
        {
            if (_isDisposed || revision != _generationSelectionRevision || SelectedGeneration?.ModelId != generation.ModelId) return;
            IsAnalysisLoading = false;
            if (snapshot is null) return;
            var analysis = snapshot.Analysis;
            var importance = analysis.Importance;
            ImportanceProvenance = string.Format(CultureInfo.CurrentCulture,
                LocalizationManager.Instance.Get("ModelAnalysis_Provenance"), generation.ModelId, analysis.DataRevision);
            ImportanceTitle = importance.Method is "split_gain" or "grouped_permutation"
                ? LocalizationManager.Instance.Get(importance.Method == "split_gain"
                    ? "ModelAnalysis_GainTitle" : "ModelAnalysis_PermutationTitle") : null;
            ImportanceStatus = LocalizationManager.Instance.Get(importance.Status switch
            {
                "available" => importance.Method == "split_gain" ? "ModelAnalysis_GainDescription" : "ModelAnalysis_PermutationDescription",
                "zero_total_gain" => "ModelAnalysis_ZeroGain",
                "regression_metric_not_adopted" => "ModelAnalysis_RegressionUnavailable",
                _ => "ModelAnalysis_Unavailable",
            });
            if (importance.Status != "available") return;
            string unit = LocalizationManager.Instance.Get(importance.Unit == "relative_gain_percent"
                ? "ModelAnalysis_GainUnit" : "ModelAnalysis_PermutationUnit");
            foreach (var item in importance.Values)
                ImportanceRows.Add(new ImportanceDisplayRow(item.Channel,
                    item.Value.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + " " + unit,
                    Math.Max(0, item.Value), Math.Max(0, -item.Value)));
        }
        if (_analysisDispatcher is null) Publish(); else _analysisDispatcher.Post(Publish);
    }
}
