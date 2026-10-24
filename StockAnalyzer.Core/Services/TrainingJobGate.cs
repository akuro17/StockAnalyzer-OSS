using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public sealed class TrainingJobBusyException() : InvalidOperationException("This training job is already running.");

/// <summary>
/// A process-wide and cross-process lease shared by manual and scheduled training. A lease is an empty token file that is held
/// open while the job runs, so it is a temporary file: by default it lives in the OS temp folder, never in the Data folder.
/// </summary>
public sealed class TrainingJobGate
{
    private const string LockFolderName = "job-locks";
    private const int DataRootKeyLength = 16;

    private readonly string _root;
    public TrainingJobGate(string? root = null) => _root = root ?? DefaultRoot(PathDiscovery.ResolveDataPathWithoutCreating(null, "Data"));

    /// <summary>
    /// <c>%TEMP%/StockAnalyzer/job-locks/&lt;key&gt;</c>, where the key identifies the Data root: only jobs that write to the same
    /// Data root can collide, so an instance with another (e.g. isolated test) Data root never blocks this one. The lease is
    /// per Windows user, like the temp folder.
    /// </summary>
    internal static string DefaultRoot(string dataRoot)
    {
        string normalized = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..DataRootKeyLength].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName, LockFolderName, key);
    }

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
