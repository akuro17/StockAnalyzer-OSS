using System;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>Training-only saved limits and an exclusive live settings preview.</summary>
public interface ITrainingResourceSettings
{
    TrainingResourceOverrides Snapshot { get; }
    TrainingResourceOverrides SavedSnapshot { get; }
    string? LoadError { get; }
    bool IsSaving { get; }
    bool TryAcquirePreview(object owner);
    void SetPreview(object owner, TrainingResourceOverrides value);
    void ReleasePreview(object owner);
    Task SaveAsync(object owner);
}
