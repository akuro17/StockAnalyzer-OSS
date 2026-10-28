using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// A zero-mutation, read-only implementation of <see cref="IRlModelGenerationRegistry"/> and <see cref="IModelGenerationRegistry"/>.
/// It never creates directories, never enters coordination locks, and never deletes files.
/// </summary>
public sealed class ReadOnlyModelGenerationRegistry : IRlModelGenerationRegistry, IModelGenerationRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly string _rlRoot;

    public ReadOnlyModelGenerationRegistry(string? root = null)
    {
        _root = root ?? PathDiscovery.ResolveModelGenerationsDirectoryWithoutCreating();
        _rlRoot = Path.Combine(_root, RlFiles.RlFolder);
    }

    #region IRlModelGenerationRegistry

    public string? RlActiveId
    {
        get
        {
            try
            {
                string activePath = Path.Combine(_rlRoot, RlFiles.Active);
                if (!File.Exists(activePath)) return null;

                using var stream = File.OpenRead(activePath);
                using var doc = JsonDocument.Parse(stream);
                return doc.RootElement.TryGetProperty("activeId", out var prop) ? prop.GetString() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public RlGeneration? GetRlGeneration(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        try
        {
            string manifestPath = Path.Combine(_rlRoot, modelId, RlFiles.Manifest);
            if (!File.Exists(manifestPath)) return null;

            string json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<RlGeneration>(json, RlFiles.Json);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<RlGeneration> ListRl()
    {
        if (!Directory.Exists(_rlRoot)) return Array.Empty<RlGeneration>();

        var result = new List<RlGeneration>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_rlRoot))
            {
                string modelId = Path.GetFileName(dir);
                if (GetRlGeneration(modelId) is { } gen)
                {
                    result.Add(gen);
                }
            }
        }
        catch
        {
            // Read-only best effort
        }
        return result;
    }

    public ModelGenerationLease? AcquireRl(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        try
        {
            string modelPath = Path.Combine(_rlRoot, modelId, RlFiles.Model);
            if (!File.Exists(modelPath)) return null;

            return new ModelGenerationLease(modelId, modelPath, release: static () => { });
        }
        catch
        {
            return null;
        }
    }

    public ModelGenerationLease? AcquireRlActive()
    {
        return RlActiveId is { } activeId ? AcquireRl(activeId) : null;
    }

    public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support registration.");

    public Task<bool> ActivateRlAsync(string modelId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support activation.");

    #endregion

    #region IModelGenerationRegistry

    public string? ActiveId
    {
        get
        {
            try
            {
                string activePath = Path.Combine(_root, "active.json");
                if (!File.Exists(activePath)) return null;

                using var stream = File.OpenRead(activePath);
                using var doc = JsonDocument.Parse(stream);
                return doc.RootElement.TryGetProperty("activeId", out var prop) ? prop.GetString() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public string? PreviousId
    {
        get
        {
            try
            {
                string activePath = Path.Combine(_root, "active.json");
                if (!File.Exists(activePath)) return null;

                using var stream = File.OpenRead(activePath);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("history", out var prop) && prop.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.EnumerateArray())
                    {
                        return item.GetString();
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    public ModelGenerationManifest? TryGetManifest(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        try
        {
            string manifestPath = Path.Combine(_root, modelId, "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            string json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<ModelGenerationManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<ModelGenerationSummary> List()
    {
        if (!Directory.Exists(_root)) return Array.Empty<ModelGenerationSummary>();

        var result = new List<ModelGenerationSummary>();
        try
        {
            string? currentActive = ActiveId;
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                string dirName = Path.GetFileName(dir);
                if (dirName.StartsWith('.') || string.Equals(dirName, RlFiles.RlFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryGetManifest(dirName) is { } manifest)
                {
                    result.Add(new ModelGenerationSummary(
                        manifest.ModelId,
                        manifest.ModelFile,
                        manifest.TargetType,
                        manifest.Timeframe,
                        manifest.Horizon,
                        manifest.Score,
                        manifest.EvaluationRevision,
                        manifest.ModelId == currentActive));
                }
            }
        }
        catch
        {
            // Read-only best effort
        }

        return result.OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
    }

    public ModelGenerationLease? Acquire(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        try
        {
            var manifest = TryGetManifest(modelId);
            if (manifest is null) return null;

            string modelPath = Path.Combine(_root, modelId, manifest.ModelFile);
            if (!File.Exists(modelPath)) return null;

            return new ModelGenerationLease(modelId, modelPath, release: static () => { });
        }
        catch
        {
            return null;
        }
    }

    public ModelGenerationLease? AcquireActive()
    {
        return ActiveId is { } activeId ? Acquire(activeId) : null;
    }

    public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support registration.");

    public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, CancellationToken ct = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support activation.");

    public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId, CancellationToken ct = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support confirmation.");

    public Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support rollback.");

    public Task DeactivateAsync(CancellationToken ct = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support deactivation.");

    public Task<bool> RemoveAsync(string modelId, CancellationToken ct = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support removal.");

    public Task<ModelGenerationManifest> VerifyCandidateAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default)
        => throw new NotSupportedException("ReadOnlyModelGenerationRegistry does not support verification.");

    #endregion
}
