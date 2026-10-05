using System;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Independent evaluation contract; never included in the T15/T16 numerical hashes.</summary>
public static class RlAllBlockEvaluationContract
{
    public const string SpecificationId = "SA-RL-ALL-COMPLETE-BLOCKS-R06-v1";
    public const string MetadataKey = "evaluation_specification_id";
    public const int SchemaVersion = 2;
    public const string ReportKey = "all_block_evaluation";
    public static bool IsExpanded(string? specification)
        => specification is null ? false : specification == SpecificationId ? true
            : throw new System.IO.InvalidDataException("Unsupported independent RL evaluation specification.");
    public static string? Read(JsonElement element)
    {
        string? specification = element.TryGetProperty(MetadataKey, out var value) ? value.GetString() : null;
        if (element.TryGetProperty(MetadataKey, out _) && specification is null)
            throw new System.IO.InvalidDataException("An explicit evaluation specification cannot be null.");
        _ = IsExpanded(specification); return specification;
    }
}

public sealed record RlReturnDistribution(int Count, decimal Mean, decimal Median, decimal Minimum,
    decimal Maximum, decimal PositiveRate);
public sealed record RlEvaluationResourceEstimate(long TrainingTransitions, long CandidateValidationTransitions,
    long BaselineTransitions, long SelectedOosTransitions, long NativeParityTransitions,
    long CoreQualificationTransitions, long MaximumTransitions, long NominalEpisodeStarts, long SourceBytes);
public sealed record RlAllBlockEvaluationReport(string SpecificationId, int SchemaVersion, string SourceRevision,
    string ModelSha256, string FrozenDirectionModelId, BacktestConfiguration Costs, RlEvaluationCoverage Coverage,
    RlReturnDistribution Validation, RlReturnDistribution? Oos,
    RlReturnDistribution PairedValidationDelta, RlReturnDistribution? PairedOosDelta,
    RlEvaluationResourceEstimate ResourceEstimate, string SelectionMetric, string PricePit, string InvestmentEfficacy);
