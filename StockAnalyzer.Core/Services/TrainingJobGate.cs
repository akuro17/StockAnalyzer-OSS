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
    {
        Directory.CreateDirectory(_root);
        try
        {
            return new FileStream(Path.Combine(_root, Identity(config) + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
        {
            throw new TrainingJobBusyException();
        }
    }
}
