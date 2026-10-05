using System;

namespace StockAnalyzer.Core.Models.Training;

public enum RetrainingStatus { Disabled, Waiting, Running, Succeeded, Failed, Cancelled, Skipped, Interrupted }

public sealed record RetrainingSchedule(int SchemaVersion, string JobId, TrainingJobConfig Config,
    bool Enabled, DateTimeOffset LastOccurrenceUtc, RetrainingStatus Status,
    string? CandidateModelId = null, string? Error = null);

public sealed record RetrainingOccurrence(string JobId, DateTimeOffset DueUtc, RetrainingStatus Status,
    DateTimeOffset UpdatedUtc, string? RunId = null, string? CandidateModelId = null, string? Error = null);
