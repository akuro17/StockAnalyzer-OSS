using System.Collections.Generic;
using StockAnalyzer.Core.Models.Confluence;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Orchestrates the signal refinement pipeline: raw signals -> combined confluence score.
/// Acts as the primary facade for the Signal Orchestration feature (FR-39-12).
/// </summary>
public class SignalOrchestrator
{
    /// <summary>
    /// Processes a collection of raw signals into a single, refined confluence result.
    /// This pipeline includes deduplication and decorrelation group weighting.
    /// </summary>
    public ConfluenceResult Orchestrate(int index, IReadOnlyList<ConfluenceSignal> rawSignals)
    {
        return Orchestrate(index, rawSignals, new List<ConfluenceSignal>());
    }

    /// <summary>
    /// Processes a collection of raw signals into a single, refined confluence result using a provided workspace list.
    /// </summary>
    public ConfluenceResult Orchestrate(int index, IReadOnlyList<ConfluenceSignal> rawSignals, IList<ConfluenceSignal> workspace)
    {
        // This facade delegates to the mathematical calculator while providing 
        // a stable architectural entry point for ViewModels and other services.
        // Future extensions like signal weighting overrides or persistence can be added here.
        return ConfluenceScoreCalculator.Calculate(index, rawSignals, workspace);
    }
}
