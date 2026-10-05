using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public sealed class TrainingJobBusyException() : InvalidOperationException("This training job is already running.");

/// <summary>A process-wide and cross-process lease shared by manual and scheduled training.</summary>
public sealed class TrainingJobGate
{
    private readonly string _root;
    public TrainingJobGate(string? root = null) => _root = root ?? Path.Combine(
        PathDiscovery.ResolveDataPath(null, "Data"), "TrainingArtifacts", "job-locks");

    public static TrainingJobConfig Freeze(TrainingJobConfig config) =>
        TrainingConfigJson.DeserializeConfig(TrainingConfigJson.Serialize(config));

    public static string Identity(TrainingJobConfig config)
    {
        var stable = config with
        {
            RunId = null, ResourceLimits = null, RemainingRunBudgetSeconds = null,
            ParentModelHash = null, IndicatorChannelExportPaths = null, PreparedInputDir = null,
            Hyperparameters = config.Hyperparameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TrainingConfigJson.Serialize(stable))))
            .ToLowerInvariant();
    }

    public IDisposable Acquire(TrainingJobConfig config)
        => AcquireIdentity(Identity(config));

    /// <summary>Reuse the managed cross-process gate for a separately versioned training contract.</summary>
    public IDisposable AcquireIdentity(string identity)
    {
        if (identity.Length != SHA256.HashSizeInBytes * 2 || identity.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Training identity must be a SHA256 hash.", nameof(identity));
        Directory.CreateDirectory(_root);
        try
        {
            return new FileStream(Path.Combine(_root, identity + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (FileLockViolation.IsHeldByAnotherOwner(ex))
        {
            throw new TrainingJobBusyException();
        }
    }
}
