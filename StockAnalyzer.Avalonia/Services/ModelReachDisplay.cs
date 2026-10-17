using System.Collections.Generic;
using System.Globalization;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// The words for "how often a model reaches the confidence threshold on its held-out windows" (the share comes from <c>ModelConfidenceReach</c>) and for the reason
/// the automatic activation was stopped (<c>ModelAutoActivationGate</c>). One place for the number formats and the culture, so the model list and the Training
/// Wizard say the same thing; the culture is <see cref="DisplayFormat.Culture"/> (SA_ARCHITECTURE_RULES, Culture Invariance).
/// </summary>
public static class ModelReachDisplay
{
    private const string ThresholdFormat = "0.##", ShareFormat = "0.0";

    /// <summary>The short text of a model-list row: "Reaches 50% in 0.0% of held-out windows".</summary>
    public static string ListText(double share, float threshold) => string.Format(DisplayFormat.Culture,
        LocalizationManager.Instance["Settings_ModelGenerations_Reach"], Percent(threshold), Percent(share, ShareFormat));

    /// <summary>
    /// The Training Wizard status line: a plain statement, or a note when no held-out window can reach the threshold (<paramref name="cannotReach"/>, the upper bound
    /// of the share is zero: the chart will show Unknown). <paramref name="share"/> is the lower bound that the statement quotes.
    /// </summary>
    public static string StatusLine(double share, bool cannotReach, float threshold) => string.Format(DisplayFormat.Culture,
        LocalizationManager.Instance[cannotReach ? "TrainingWizard_Status_ReachZero" : "TrainingWizard_Status_Reach"],
        Percent(threshold), Percent(share, ShareFormat));

    /// <summary>A threshold as a percentage in the same format the other reach texts use ("50%").</summary>
    public static string Percent(float threshold) => Percent(threshold, ThresholdFormat);

    /// <summary>The reasons the gate gave for stopping the automatic activation, joined ("..., ... and ..."); empty when it did not stop it.</summary>
    public static string StopReasons(ModelAutoActivationDecision decision, float? threshold)
    {
        var reasons = new List<string>();
        var text = LocalizationManager.Instance;
        if (decision.ReachIsZero && threshold is { } confidenceThreshold)
            reasons.Add(string.Format(DisplayFormat.Culture, text["TrainingWizard_Status_StopReasonReach"], Percent(confidenceThreshold)));
        if (decision.NotBetterThanUniform) reasons.Add(text["TrainingWizard_Status_StopReasonNotBetter"]);
        return string.Join(text["TrainingWizard_Status_StopReasonJoin"], reasons);
    }

    private static string Percent(double fraction, string format) => (fraction * 100).ToString(format, DisplayFormat.Culture) + "%";
}
