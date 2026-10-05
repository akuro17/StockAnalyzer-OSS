using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// One combination of an evaluation-only CPCV run (T14), parsed from a <c>METRIC:</c> line carrying <c>cpcv_combination</c>.
/// It reports only that combination's own scores: the combination count is never presented as a number of paths.
/// </summary>
public sealed record CpcvMetricRow
{
    public int Combination { get; init; }
    public int Combinations { get; init; }
    public IReadOnlyList<int> TestGroups { get; init; } = Array.Empty<int>();
    public int TrainCount { get; init; }
    public int TestCount { get; init; }
    public bool IsRegression { get; init; }
    public double Accuracy { get; init; }
    public double MacroF1 { get; init; }
    public double Rmse { get; init; }
    public double Mae { get; init; }

    /// <summary>
    /// Builds a row from a metric dictionary, or returns <see langword="null"/> when it is not a CPCV combination line.
    /// <paramref name="groups"/> / <paramref name="testGroups"/> come from the job config that started the run, so the
    /// test-group labels are recomputed by the shared enumeration instead of being carried as text.
    /// </summary>
    public static CpcvMetricRow? FromMetric(IReadOnlyDictionary<string, double>? metric, int groups, int testGroups)
    {
        if (metric is null || !metric.TryGetValue("cpcv_combination", out var index)) return null;
        var isRegression = metric.ContainsKey("cpcv_rmse");
        if (!isRegression && !metric.ContainsKey("cpcv_macro_f1")) return null;
        int[] labels;
        try { labels = TrainingCpcvContract.Combination(groups, testGroups, (int)index); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { labels = Array.Empty<int>(); }
        double Value(string key) => metric.TryGetValue(key, out var v) ? v : double.NaN;
        return new CpcvMetricRow
        {
            Combination = (int)index,
            Combinations = metric.TryGetValue("cpcv_combinations", out var total) ? (int)total : 0,
            TestGroups = labels,
            TrainCount = (int)Value("cpcv_n_train"),
            TestCount = (int)Value("cpcv_n_test"),
            IsRegression = isRegression,
            Accuracy = Value("cpcv_accuracy"),
            MacroF1 = Value("cpcv_macro_f1"),
            Rmse = Value("cpcv_rmse"),
            Mae = Value("cpcv_mae"),
        };
    }

    /// <summary>One localized line: combination, held-out groups, train/test windows and the headline scores.</summary>
    public string Display
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var groupsText = string.Join(",", TestGroups.Select(g => (g + 1).ToString(culture)));
            var template = LocalizationManager.Instance[IsRegression ? "TrainingWizard_CpcvRow_Regression" : "TrainingWizard_CpcvRow"];
            return string.Format(culture, template, Combination + 1, groupsText, TrainCount, TestCount,
                (IsRegression ? Rmse : Accuracy).ToString(IsRegression ? "F4" : "P1", culture),
                (IsRegression ? Mae : MacroF1).ToString(IsRegression ? "F4" : "F3", culture));
        }
    }
}
