using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public sealed class ModelMetadataAnnotationService : IModelMetadataAnnotationService
{
    public const int MaxDisplayNameLength = IModelMetadataAnnotationService.MaxDisplayNameLength;
    public const int MaxHistoryEntries = IModelMetadataAnnotationService.MaxHistoryEntries;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly ILogger<ModelMetadataAnnotationService>? _logger;
    private readonly object _initLock = new();

    private ImmutableDictionary<ModelMetadataKey, ModelMetadataAnnotation> _annotations =
        ImmutableDictionary<ModelMetadataKey, ModelMetadataAnnotation>.Empty;
    private long _currentRevision;
    private volatile bool _initialized;

    public ModelMetadataAnnotationService(
        string? filePath = null,
        ILogger<ModelMetadataAnnotationService>? logger = null)
    {
        _filePath = filePath ?? GetDefaultFilePath();
        _logger = logger;
    }

    private static string GetDefaultFilePath()
    {
        var dir = PathDiscovery.ResolveModelGenerationsDirectory();
        return Path.Combine(dir, "model_annotations.json");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var gate = await SerializedPathGate.AcquireAsync(_filePath, cancellationToken).ConfigureAwait(false);
        if (_initialized) return;

        await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_filePath))
        {
            var backupPath = _filePath + AtomicJsonFile.BackupSuffix;
            if (File.Exists(backupPath))
            {
                try
                {
                    var storeFromBak = await AtomicJsonFile.LoadAsync<ModelMetadataStore>(backupPath, JsonOptions).ConfigureAwait(false);
                    if (storeFromBak != null)
                    {
                        PopulateFromStore(storeFromBak);
                        await AtomicJsonFile.SaveAsync(_filePath, storeFromBak, JsonOptions, preserveBackup: true).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception bakEx)
                {
                    _logger?.LogWarning(bakEx, "Failed to load backup annotations file at {BackupPath}", backupPath);
                }
            }

            Volatile.Write(ref _annotations, ImmutableDictionary<ModelMetadataKey, ModelMetadataAnnotation>.Empty);
            _currentRevision = 0;
            return;
        }

        try
        {
            var store = await AtomicJsonFile.LoadAsync<ModelMetadataStore>(_filePath, JsonOptions).ConfigureAwait(false);
            if (store != null)
            {
                PopulateFromStore(store);
                return;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            _logger?.LogError(ex, "Corrupted model annotations file detected at {FilePath}", _filePath);

            var corruptedPath = _filePath + ".corrupted." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            try
            {
                File.Move(_filePath, corruptedPath);
                _logger?.LogInformation("Quarantined corrupted annotations to {CorruptedPath}", corruptedPath);
            }
            catch (Exception moveEx)
            {
                _logger?.LogWarning(moveEx, "Failed to quarantine corrupted file {FilePath} to {CorruptedPath}", _filePath, corruptedPath);
            }

            var backupPath = _filePath + AtomicJsonFile.BackupSuffix;
            if (File.Exists(backupPath))
            {
                try
                {
                    var backupStore = await AtomicJsonFile.LoadAsync<ModelMetadataStore>(backupPath, JsonOptions).ConfigureAwait(false);
                    if (backupStore != null)
                    {
                        PopulateFromStore(backupStore);
                        await AtomicJsonFile.SaveAsync(_filePath, backupStore, JsonOptions, preserveBackup: true).ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception bakEx)
                {
                    _logger?.LogError(bakEx, "Failed to load backup annotations file at {BackupPath}", backupPath);
                }
            }

            Volatile.Write(ref _annotations, ImmutableDictionary<ModelMetadataKey, ModelMetadataAnnotation>.Empty);
            _currentRevision = 0;
        }
    }

    private void PopulateFromStore(ModelMetadataStore store)
    {
        _currentRevision = store.Revision;
        var builder = ImmutableDictionary.CreateBuilder<ModelMetadataKey, ModelMetadataAnnotation>();
        if (store.Entries != null)
        {
            foreach (var entry in store.Entries)
            {
                builder[entry.Key] = entry;
            }
        }
        Volatile.Write(ref _annotations, builder.ToImmutable());
    }

    private void EnsureSynchronouslyLoadedIfPresent()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            var backupPath = _filePath + AtomicJsonFile.BackupSuffix;
            if (!File.Exists(_filePath) && !File.Exists(backupPath))
            {
                _initialized = true;
                return;
            }

            try
            {
                var pathToRead = File.Exists(_filePath) ? _filePath : backupPath;
                using var stream = AtomicJsonFile.OpenForRead(pathToRead);
                var store = JsonSerializer.Deserialize<ModelMetadataStore>(stream, JsonOptions);
                if (store != null)
                {
                    PopulateFromStore(store);
                }
            }
            catch
            {
                // Fall back to empty snapshot; full load/quarantine will execute in InitializeAsync
            }
            finally
            {
                _initialized = true;
            }
        }
    }

    public ModelMetadataAnnotation? GetAnnotation(ModelMetadataKey key)
    {
        EnsureSynchronouslyLoadedIfPresent();
        var snapshot = Volatile.Read(ref _annotations);
        return snapshot.GetValueOrDefault(key);
    }

    public ModelMetadataAnnotation? GetAnnotation(string modelId)
        => GetAnnotation(new ModelMetadataKey(ModelArtifactKind.Prediction, modelId));

    public string ResolveDisplayName(ModelMetadataKey key, string fallbackName)
    {
        var annotation = GetAnnotation(key);
        if (annotation != null && !string.IsNullOrWhiteSpace(annotation.CustomDisplayName))
        {
            return annotation.CustomDisplayName.Trim();
        }
        return fallbackName;
    }

    public string ResolveDisplayName(string modelId, string fallbackName)
        => ResolveDisplayName(new ModelMetadataKey(ModelArtifactKind.Prediction, modelId), fallbackName);

    public async Task<ModelMetadataAnnotation> SetCustomDisplayNameAsync(
        ModelMetadataKey key,
        string? newDisplayName,
        DateTimeOffset? initialCreatedUtc = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key.ModelId))
            throw new ArgumentException("ModelId must not be empty.", nameof(key));

        string? normalized = string.IsNullOrWhiteSpace(newDisplayName) ? null : newDisplayName.Trim();
        if (normalized != null)
        {
            if (normalized.Length > MaxDisplayNameLength)
                throw new ArgumentException($"Display name must not exceed {MaxDisplayNameLength} characters.", nameof(newDisplayName));

            for (int i = 0; i < normalized.Length; i++)
            {
                if (char.IsControl(normalized[i]))
                    throw new ArgumentException("Display name must not contain control characters.", nameof(newDisplayName));
            }
        }

        // Fast no-op check against current in-memory state
        var current = GetAnnotation(key);
        if (string.Equals(current?.CustomDisplayName, normalized, StringComparison.Ordinal))
        {
            return current ?? new ModelMetadataAnnotation
            {
                Key = key,
                CustomDisplayName = null,
                AnnotationCreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        using var gate = await SerializedPathGate.AcquireAsync(_filePath, cancellationToken).ConfigureAwait(false);

        if (!_initialized)
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }

        var snapshot = Volatile.Read(ref _annotations);
        snapshot.TryGetValue(key, out current);

        if (string.Equals(current?.CustomDisplayName, normalized, StringComparison.Ordinal))
        {
            return current ?? new ModelMetadataAnnotation
            {
                Key = key,
                CustomDisplayName = null,
                AnnotationCreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        var now = DateTimeOffset.UtcNow;
        var history = current?.History ?? ImmutableList<ModelUpdateHistoryEntry>.Empty;
        long sequence = history.Count > 0 ? history[^1].Sequence + 1 : 1;

        var historyEntry = new ModelUpdateHistoryEntry(
            Sequence: sequence,
            TimestampUtc: now,
            FieldName: nameof(ModelMetadataAnnotation.CustomDisplayName),
            OldValue: current?.CustomDisplayName,
            NewValue: normalized,
            Reason: reason);

        var newHistory = history.Add(historyEntry);
        if (newHistory.Count > MaxHistoryEntries)
        {
            newHistory = newHistory.TakeLast(MaxHistoryEntries).ToImmutableList();
        }

        var updated = new ModelMetadataAnnotation
        {
            Key = key,
            CustomDisplayName = normalized,
            AnnotationCreatedUtc = current?.AnnotationCreatedUtc ?? initialCreatedUtc ?? now,
            UpdatedUtc = now,
            History = newHistory
        };

        var newSnapshot = snapshot.SetItem(key, updated);
        long newRevision = _currentRevision + 1;
        var store = new ModelMetadataStore(
            SchemaVersion: 1,
            Revision: newRevision,
            Entries: newSnapshot.Values.ToImmutableList());

        await AtomicJsonFile.SaveAsync(_filePath, store, JsonOptions, preserveBackup: true).ConfigureAwait(false);

        _currentRevision = newRevision;
        _initialized = true;
        Volatile.Write(ref _annotations, newSnapshot);
        return updated;
    }

    public Task<ModelMetadataAnnotation> SetCustomDisplayNameAsync(
        string modelId,
        string? newDisplayName,
        DateTimeOffset? initialCreatedUtc = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => SetCustomDisplayNameAsync(new ModelMetadataKey(ModelArtifactKind.Prediction, modelId), newDisplayName, initialCreatedUtc, reason, cancellationToken);

    public Task<ModelMetadataAnnotation> ClearCustomDisplayNameAsync(
        ModelMetadataKey key,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => SetCustomDisplayNameAsync(key, null, reason: reason ?? "Cleared display name", cancellationToken: cancellationToken);

    public Task<ModelMetadataAnnotation> ClearCustomDisplayNameAsync(
        string modelId,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => ClearCustomDisplayNameAsync(new ModelMetadataKey(ModelArtifactKind.Prediction, modelId), reason, cancellationToken);
}
