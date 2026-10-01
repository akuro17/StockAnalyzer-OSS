using System;
using System.IO;
using System.Text.Json;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>Portable lineage of a training run, including its original initialization mode.</summary>
public sealed record TrainingLineage(string Mode, string RunId, string? ParentModelId,
    string? ParentCheckpointSha256, string? ParentModelSha256)
{
    public static TrainingLineage? Read(string? json)
    {
        if (json is null) return null;
        var value = JsonSerializer.Deserialize<TrainingLineage>(json, TrainingConfigJson.Options)
            ?? throw new InvalidDataException("Training lineage is missing.");
        if (string.IsNullOrWhiteSpace(value.RunId)
            || value.Mode is not ("fresh" or "fine_tune")
            || (value.Mode == "fresh" && (value.ParentModelId is not null
                || value.ParentCheckpointSha256 is not null || value.ParentModelSha256 is not null))
            || (value.Mode == "fine_tune" && (!TrainingCheckpointContract.IsHash(value.ParentModelId)
                || !TrainingCheckpointContract.IsHash(value.ParentCheckpointSha256)
                || !TrainingCheckpointContract.IsHash(value.ParentModelSha256))))
            throw new InvalidDataException("Training lineage is invalid.");
        return value;
    }
}
