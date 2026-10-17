using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>How a manual prediction run on the main chart ended.</summary>
public enum PredictionRunOutcome
{
    /// <summary>The chart predicted again; <see cref="PredictionRunReport.Display"/> is empty when no prediction is shown (see the notice).</summary>
    Completed,
    /// <summary>The chart holds no candles.</summary>
    NoData,
    /// <summary>The chart shows a reinforcement-learning replay, which has no prediction.</summary>
    ReplayShown,
    /// <summary>The chart has no prediction service.</summary>
    Unavailable,
}

/// <param name="Display">The prediction text the chart shows after the run; null when there is none.</param>
/// <param name="Notice">Why there is no prediction when the owner can correct it (the model window differs from the configured one); null otherwise.</param>
/// <param name="Bars">How many candles the chart holds (a model needs at least its window size).</param>
public sealed record PredictionRunReport(PredictionRunOutcome Outcome, string Symbol, TimeframeType Timeframe, string? Display, string? Notice, int Bars = 0);

/// <summary>The seam from the Prediction Models window to the main chart (ChartViewModel is a transient service, so the window cannot receive it from DI).</summary>
public interface IPredictionRunTarget
{
    /// <summary>Predicts again on the main chart now, with the active model or ensemble, for the candles it already holds.</summary>
    Task<PredictionRunReport> RunAsync(CancellationToken cancellationToken);
}
