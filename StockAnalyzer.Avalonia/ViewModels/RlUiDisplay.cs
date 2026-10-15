using System;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;

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
    /// <summary>Caption of a list that is loading, failed, unsupported, empty or loaded.</summary>
    public static string LoadCaption(RlDialogLoadState state, int count, ILocalizationService text) => text.GetString(state switch
    {
        RlDialogLoadState.Loading => "Rl.Ui.Load.Loading", RlDialogLoadState.Failed => "Rl.Ui.Load.Failed",
        RlDialogLoadState.Unsupported => "Rl.Ui.Load.Unsupported", RlDialogLoadState.Loaded when count == 0 => "Rl.Ui.Load.Empty",
        RlDialogLoadState.Loaded => "Rl.Checkpoint.Verified", _ => "Rl.Ui.Load.Unloaded"
    });
    /// <summary>Summary of one stored generation: target and symbol, identifier, validation and final mean return.</summary>
    public static string Generation(RlGeneration g, ILocalizationService text) => Target(g.Target, text) + " · " + g.Symbol + Environment.NewLine
        + text.GetString("Rl.Ui.Field.Identifier") + ": " + g.ModelId + Environment.NewLine
        + text.GetString("Rl.Ui.Field.ValidationMeanReturn") + ": " + g.ValidationReturn.ToString("P" + RlContract.DisplayDecimals, System.Globalization.CultureInfo.CurrentCulture) + Environment.NewLine
        + text.GetString("Rl.Ui.Field.FinalMeanReturn") + ": " + g.OosReturn.ToString("P" + RlContract.DisplayDecimals, System.Globalization.CultureInfo.CurrentCulture);
    /// <summary>List label of one stored model: target, symbol, identifier, and a marker when its work folder is gone.</summary>
    public static string ModelLabel(RlGeneration g, bool withoutWorkFolder, ILocalizationService text)
        => Target(g.Target, text) + " · " + g.Symbol + " · " + g.ModelId + (withoutWorkFolder ? " · " + text.GetString("Rl.Ui.NoWorkFolder") : "");
    /// <summary>Confirmation text of the model removal; says so when the model is the active one.</summary>
    public static string RemoveModelQuestion(string modelId, bool isActive, ILocalizationService text)
    {
        string question = string.Format(CultureInfo.CurrentCulture, text.GetString("Rl.Ui.RemoveModel.Confirm"), modelId);
        return isActive ? question + " " + text.GetString("Rl.Ui.RemoveModel.ConfirmActive") : question;
    }
    /// <summary>Result line of the model removal: done, or the reasons the registry refused it.</summary>
    public static string RemoveModelResult(RlGenerationRemoval result, string modelId, ILocalizationService text)
        => result.Removed
            ? string.Format(CultureInfo.CurrentCulture, text.GetString("Rl.Ui.RemoveModel.Done"), modelId)
            : string.Format(CultureInfo.CurrentCulture, text.GetString("Rl.Ui.RemoveModel.Refused"),
                string.Join(", ", result.Reasons.Select(reason => KnownLabel("Rl.Storage.Reason." + reason, text))));
    /// <summary>The wording of a key, or the "unsupported" wording when the key has none.</summary>
    public static string KnownLabel(string key, ILocalizationService text)
    {
        string label = text.GetString(key);
        return label == "[" + key + "]" ? text.GetString("Rl.Ui.Load.Unsupported") : label;
    }
    public static string Metric(string id, ILocalizationService text) => text.GetString(
        id == RlConstraintContract.SelectionMetric ? "Rl.Ui.Metric.MeanUndiscountedReturn" : "Rl.Ui.Load.Unsupported");
}
