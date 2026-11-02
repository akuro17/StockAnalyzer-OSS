using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public interface IModelMetadataAnnotationService
{
    public const int MaxDisplayNameLength = 128;
    public const int MaxHistoryEntries = 100;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    ModelMetadataAnnotation? GetAnnotation(ModelMetadataKey key);
    ModelMetadataAnnotation? GetAnnotation(string modelId);

    string ResolveDisplayName(ModelMetadataKey key, string fallbackName);
    string ResolveDisplayName(string modelId, string fallbackName);

    Task<ModelMetadataAnnotation> SetCustomDisplayNameAsync(
        ModelMetadataKey key,
        string? newDisplayName,
        DateTimeOffset? initialCreatedUtc = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<ModelMetadataAnnotation> SetCustomDisplayNameAsync(
        string modelId,
        string? newDisplayName,
        DateTimeOffset? initialCreatedUtc = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<ModelMetadataAnnotation> ClearCustomDisplayNameAsync(
        ModelMetadataKey key,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<ModelMetadataAnnotation> ClearCustomDisplayNameAsync(
        string modelId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
