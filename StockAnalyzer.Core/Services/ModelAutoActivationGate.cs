namespace StockAnalyzer.Core.Services;

/// <summary>Why the automatic activation of a freshly registered generation is stopped; both flags false = nothing stops it.</summary>
public sealed record ModelAutoActivationDecision(bool ReachIsZero, bool NotBetterThanUniform)
{
    public bool Stopped => ReachIsZero || NotBetterThanUniform;
}

/// <summary>
/// The rule that keeps the system from activating, on its own, a classification model the chart cannot use (owner decision D4, 2026-10-17): the activation that
/// follows a registration is stopped when the held-out confidence reach at the chart's threshold is exactly 0 (the chart would show Unknown for every window), or
/// when the held-out log-loss is not below that of a uniform guess (<see cref="ModelQualityCheck"/>). What cannot be told (no metrics file, no key, a damaged
/// value, a regression model, no threshold) never stops it. Only the automatic activation is gated; choosing the generation by hand is always possible.
/// The stop uses the UPPER bound of the reach (<see cref="ConfidenceHistogram.ShareUpperBound"/>): the activation is stopped only when no held-out window can reach
/// the threshold, so a threshold that falls between two histogram bins never stops a model that does reach the chart.
/// </summary>
public static class ModelAutoActivationGate
{
    public static ModelAutoActivationDecision Evaluate(string metricsPath, string classOrder, float? confidenceThreshold) =>
        Evaluate(ModelEvaluationSummary.Read(metricsPath), classOrder, confidenceThreshold);

    /// <summary>The same decision on an evaluation that was read already.</summary>
    public static ModelAutoActivationDecision Evaluate(ModelEvaluationSummary summary, string classOrder, float? confidenceThreshold) => new(
        ReachIsZero: confidenceThreshold is { } threshold && summary.Reach?.ShareUpperBound(threshold) == 0.0,
        NotBetterThanUniform: ModelQualityCheck.IsNotBetterThanUniform(summary, classOrder) == true);
}
