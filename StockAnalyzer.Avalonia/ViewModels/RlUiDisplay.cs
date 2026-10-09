using System;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>One display mapping for dialog options and chart/evaluation readouts.</summary>
public static class RlUiDisplay
{
    public static string Target(string id, ILocalizationService text) => text.GetString(id switch
    {
        RlFiles.Target => "Rl.Ui.Target.Direction",
        RlContinuousContract.Target => "Rl.Ui.Target.Continuous",
        _ => "Rl.Ui.Load.Unsupported"
    });
    public static string Policy(string id, ILocalizationService text) => id switch
    {
        RlFiles.Target or RlContinuousContract.Target => Target(id, text),
        RlContract.HoldBaselineId => text.GetString("Rl.Ui.Policy.Hold"),
        RlContract.BuyHoldBaselineId => text.GetString("Rl.Ui.Policy.BuyHold"),
        RlContract.SmaBaselineId => text.GetString("Rl.Ui.Policy.Sma"),
        RlContract.RandomBaselineId => text.GetString("Rl.Ui.Policy.Random"),
        RlContinuousContract.PairedBaselineId => text.GetString("Rl.Ui.Policy.Paired"),
        _ => text.GetString("Rl.Ui.Load.Unsupported")
    };
    public static string Partition(string id, ILocalizationService text) => text.GetString(id switch
    {
        "train" => "Rl.Ui.Partition.train", "validation" => "Rl.Ui.Partition.validation", "oos" => "Rl.Ui.Partition.oos",
        _ => "Rl.Ui.Load.Unsupported"
    });
    public static string Metric(string id, ILocalizationService text) => text.GetString(
        id == RlConstraintContract.SelectionMetric ? "Rl.Ui.Metric.MeanUndiscountedReturn" : "Rl.Ui.Load.Unsupported");
}
