using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services;

public sealed partial class ModelGenerationRegistry
{
    internal Func<string, Pointer, Task>? RecoveryPointerWriter { get; init; }
    private string RlPointerPath(string target) => Path.Combine(RlRoot, target switch {
        RlFiles.Target => RlFiles.Active, RlContinuousContract.Target => RlContinuousContract.ActiveFile,
        _ => throw new ArgumentException("Unsupported RL pointer target.", nameof(target)) });
    private void ValidateRlPointer(Pointer pointer, string target, bool full = false)
    {
        foreach (string id in pointer.History.Concat(pointer.ActiveId is { } active ? new[] { active } : Array.Empty<string>()))
        {
            RequireStoreTree(Path.Combine(RlRoot, id));
            if (!_rlKnown.TryGetValue(id, out var generation) || generation.Target != target
                || !(full ? VerifyRl(generation) : VerifyRlBytes(generation))
                || generation.TemporalSpecificationId is not null && generation.Target == RlContinuousContract.Target && !VerifyRlDependency(generation))
                throw new InvalidDataException("Pointer references an unverified generation, target or frozen dependency.");
        }
    }
    private (Pointer Pointer, string? Error) LoadRlPointer(string target)
    {
        try
        {
            var pointer = ReadStorePointer(RlPointerPath(target), RlContract.RollbackGenerationCount);
            ValidateRlPointer(pointer, target); return (pointer, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { RecordStoreFailure("rl-pointer", error); return (new(null, Array.Empty<string>()), error.Message); }
    }

    public RlPointerRecovery PreviewRlRecovery(string target)
    {
        lock (_gate)
        {
            using var transaction = EnterStore(); EnsureRlLoaded();
            string path = RlPointerPath(target), backup = path + Coordination.BackupSuffix;
            var current = LoadRlPointer(target);
            string? candidate = null, error = current.Error;
            if (error is not null)
            {
                try
                {
                    var pointer = ReadStorePointer(backup, RlContract.RollbackGenerationCount, false);
                    if (pointer.ActiveId is null) throw new InvalidDataException("Backup has no active generation to recover.");
                    ValidateRlPointer(pointer, target, full: true); candidate = pointer.ActiveId;
                }
                catch (Exception failure) when (failure is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
                { RecordStoreFailure("rl-backup-preview", failure); error += " " + failure.Message; }
            }
            return new(target, current.Error is not null, error, candidate, FileIdentity(path), FileIdentity(backup));
        }
    }
    public async Task<bool> RecoverRlPointerAsync(RlPointerRecovery reviewed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = EnterStore(cancellationToken);
            Pointer pointer; string path;
            lock (_gate)
            {
                EnsureRlLoaded(); path = RlPointerPath(reviewed.Target);
                var current = PreviewRlRecovery(reviewed.Target);
                if (!current.Unavailable) throw new InvalidOperationException("A healthy current pointer cannot be recovered or rolled back.");
                if (!reviewed.Unavailable || current.BackupModelId is null || current != reviewed)
                    throw new InvalidOperationException("Recovery candidate/context changed; inspect the verified backup again.");
                pointer = ReadStorePointer(path + Coordination.BackupSuffix, RlContract.RollbackGenerationCount, false);
                ValidateRlPointer(pointer, reviewed.Target, full: true);
                // Preserve corruption evidence and the verified backup rather than replacing it with damaged bytes.
                if (File.Exists(path)) File.Copy(path, Path.Combine(_root, Coordination.Folder, "corrupt_pointer_" + Guid.NewGuid().ToString("N") + ".json"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            RequirePointerWritePaths(path);
            if (RecoveryPointerWriter is { } writer) await writer(path, pointer).ConfigureAwait(false);
            else await AtomicJsonFile.SaveAsync(path, pointer, JsonOptions, preserveBackup: true).ConfigureAwait(false);
            lock (_gate)
            {
                _rlLoaded = false; EnsureRlLoaded();
                if ((reviewed.Target == RlFiles.Target ? _rlPointerError : _rlContinuousPointerError) is not null)
                    throw new InvalidDataException("Saved recovery pointer did not verify.");
            }
            return true;
        }
        catch (Exception error) { RecordStoreFailure("rl-recovery", error); throw; }
        finally { _mutation.Release(); }
    }
}
