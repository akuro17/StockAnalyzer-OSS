using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>Serialized writer for the training-only resource settings file.</summary>
public sealed class TrainingResourceSettings : ITrainingResourceSettings
{
    public const int SchemaVersion = 1;
    public const string FileName = "user_training_resource_settings.json";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<string, TrainingResourceOverrides, Task> _writeFile;
    private TrainingResourceOverrides _saved = new();
    private TrainingResourceOverrides _preview = new();
    private object? _owner;
    private bool _saving;

    public TrainingResourceSettings(string? path = null) : this(path, WriteFileAsync) { }

    internal TrainingResourceSettings(string? path, Func<string, TrainingResourceOverrides, Task> writeFile)
    {
        _path = path ?? PathDiscovery.ResolveConfigPath(FileName);
        _writeFile = writeFile ?? throw new ArgumentNullException(nameof(writeFile));
        if (!File.Exists(_path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema_version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) ||
                number != SchemaVersion)
            {
                throw new JsonException($"Expected schema_version {SchemaVersion}.");
            }

            _saved = new TrainingResourceOverrides(
                ReadLimit(root, "max_channels"),
                ReadLimit(root, "max_tensor_size_mib"),
                ReadLimit(root, "max_samples"));
            _saved.Validate();
            _preview = _saved;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoadError = $"Training resource settings could not be loaded: {ex.Message}";
        }
    }

    public string? LoadError { get; }
    public TrainingResourceOverrides Snapshot { get { lock (_gate) return _preview; } }
    public TrainingResourceOverrides SavedSnapshot { get { lock (_gate) return _saved; } }
    public bool IsSaving { get { lock (_gate) return _saving; } }

    public bool TryAcquirePreview(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (_owner is not null && !ReferenceEquals(_owner, owner)) return false;
            if (_saving) return false;
            _owner = owner;
            return true;
        }
    }

    public void SetPreview(object owner, TrainingResourceOverrides value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        lock (_gate)
        {
            RequireOwner(owner);
            if (_saving) throw new InvalidOperationException("Training resource settings are being saved.");
            _preview = value;
        }
    }

    public void ReleasePreview(object owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            if (_saving) throw new InvalidOperationException("Training resource settings are being saved.");
            _preview = _saved;
            _owner = null;
        }
    }

    public async Task SaveAsync(object owner)
    {
        TrainingResourceOverrides snapshot;
        lock (_gate)
        {
            RequireOwner(owner);
            if (_saving) throw new InvalidOperationException("Training resource settings are being saved.");
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            snapshot = _preview;
            snapshot.Validate();
            _saving = true;
        }

        try
        {
            await _writeFile(_path, snapshot).ConfigureAwait(false);
            lock (_gate) _saved = snapshot;
        }
        finally
        {
            lock (_gate) _saving = false;
        }
    }

    private void RequireOwner(object owner)
    {
        if (owner is null || !ReferenceEquals(_owner, owner))
            throw new InvalidOperationException("This settings page does not own the training preview.");
    }

    private static int? ReadLimit(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var value))
            throw new JsonException($"{name} must be an integer or null.");
        return value;
    }

    private static Task WriteFileAsync(string path, TrainingResourceOverrides snapshot) =>
        AtomicJsonFile.SaveAsync(path, new FileShape(SchemaVersion,
            snapshot.MaxChannels, snapshot.MaxTensorSizeMiB, snapshot.MaxSamples));

    private sealed record FileShape(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("max_channels")] int? MaxChannels,
        [property: JsonPropertyName("max_tensor_size_mib")] int? MaxTensorSizeMiB,
        [property: JsonPropertyName("max_samples")] int? MaxSamples);
}
