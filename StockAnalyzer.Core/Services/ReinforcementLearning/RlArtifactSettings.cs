using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public sealed record RlArtifactPolicy(decimal QuotaGiB = RlArtifactSettings.DefaultQuotaGiB,
    decimal MinimumFreeGiB = RlArtifactSettings.DefaultMinimumFreeGiB,
    int FailedRetentionDays = RlArtifactSettings.DefaultFailedRetentionDays,
    int? NonactiveRetentionDays = null, int? NonactiveKeepCount = null,
    int CheckpointKeepCount = RlCheckpointContract.DefaultKeepCount)
{
    public bool IsComplete => NonactiveRetentionDays.HasValue && NonactiveKeepCount.HasValue;
    public long QuotaBytes => Bytes(QuotaGiB);
    public long MinimumFreeBytes => Bytes(MinimumFreeGiB);
    private static long Bytes(decimal gib) => checked((long)(gib * RlArtifactSettings.BytesPerGiB));
    public void Validate(bool complete = false)
    {
        if (QuotaGiB <= 0 || MinimumFreeGiB < 0 || FailedRetentionDays < 0 || CheckpointKeepCount < 1
            || NonactiveRetentionDays < 0 || NonactiveKeepCount < 0
            || FailedRetentionDays > TimeSpan.MaxValue.TotalDays || NonactiveRetentionDays > TimeSpan.MaxValue.TotalDays)
            throw new ArgumentException("Invalid artifact capacity or retention settings.");
        if (QuotaBytes <= 0) throw new ArgumentException("Artifact quota must contain at least one byte.");
        _ = MinimumFreeBytes;
        if (complete && !IsComplete) throw new InvalidOperationException("Unavailable: save both nonactive retention days and keep count first.");
    }
}

/// <summary>Saved, reloadable RL storage policy; these defaults were explicitly selected by the user.</summary>
public sealed class RlArtifactSettings
{
    public const decimal DefaultQuotaGiB = 10m;
    public const decimal DefaultMinimumFreeGiB = 5m;
    public const int DefaultFailedRetentionDays = 7;
    public const long BytesPerGiB = 1024L * 1024 * 1024;
    public const int SchemaVersion = 1;
    public const string FileName = "user_rl_artifact_settings.json";
    private readonly string _path;
    public RlArtifactSettings(string? path = null) => _path = path ?? PathDiscovery.ResolveConfigPath(FileName);
    private sealed record Saved(int SchemaVersion, RlArtifactPolicy Policy);
    public RlArtifactPolicy Snapshot
    {
        get
        {
            GuardPath(_path);
            if (!File.Exists(_path)) return new();
            if (new FileInfo(_path).Length > ModelGenerationRegistry.Coordination.MetadataByteLimit)
                throw new InvalidDataException("Artifact settings exceed the metadata bound.");
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), Options);
            if (saved is null || saved.SchemaVersion != SchemaVersion || saved.Policy is null)
                throw new InvalidDataException("Artifact settings schema is invalid.");
            saved.Policy.Validate(); return saved.Policy;
        }
    }
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static void GuardPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked artifact paths are prohibited.");
        }
    }
    public async Task SaveAsync(RlArtifactPolicy policy, CancellationToken ct = default)
    {
        policy.Validate(); GuardPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string lockPath = _path + ".lock"; GuardPath(lockPath);
        FileStream? owner = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (owner is null)
        {
            ct.ThrowIfCancellationRequested();
            try { owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (FileLockViolation.IsHeldByAnotherOwner(error))
            {
                if (elapsed.Elapsed >= ModelGenerationRegistry.Coordination.Timeout) throw new TimeoutException("Artifact settings ownership timed out.", error);
                await Task.Delay(ModelGenerationRegistry.Coordination.Poll, ct).ConfigureAwait(false);
            }
        }
        using (owner)
        {
            GuardPath(_path + AtomicJsonFile.TemporarySuffix); GuardPath(_path + AtomicJsonFile.BackupSuffix);
            await AtomicJsonFile.SaveAsync(_path, new Saved(SchemaVersion, policy), Options).ConfigureAwait(false);
        }
    }
}
