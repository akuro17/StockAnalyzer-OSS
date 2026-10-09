using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services;

public sealed record RlPointerRecovery(string Target, bool Unavailable, string? Error, string? BackupModelId,
    string CurrentIdentity, string BackupIdentity);

/// <summary>One root transaction and OS-held generation leases for cooperating registry processes.</summary>
public sealed partial class ModelGenerationRegistry
{
    internal static class Coordination
    {
        internal const string Folder = ".coordination";
        internal const string LockFile = "registry.lock";
        internal const string LeaseFolder = "leases";
        internal const string LeaseExtension = ".lease";
        internal const string BackupSuffix = AtomicJsonFile.BackupSuffix;
        internal const string FailureEvidencePrefix = "failure_";
        internal const string FailureEvidenceExtension = ".json";
        internal const string FailureEvidencePattern = FailureEvidencePrefix + "*" + FailureEvidenceExtension;
        internal static string FailureEvidenceFileName(int processId) => FailureEvidencePrefix + processId + FailureEvidenceExtension;
        internal const string PredictionLease = "model";
        internal const string RlLease = "rl";
        internal const int PointerSchema = 1;
        internal const long MetadataByteLimit = 64 * 1024;
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    }
    internal TimeSpan StoreLockTimeout { get; init; } = Coordination.Timeout;
    private FileStream? _storeHandle;
    private int _storeDepth;
    private string? _storeStamp;
    private async Task EnterMutationAsync(CancellationToken ct)
    {
        if (!await _mutation.WaitAsync(StoreLockTimeout, ct).ConfigureAwait(false))
        {
            var error = new TimeoutException("Registry local mutation ownership timed out.");
            RecordStoreFailure("mutation-timeout", error); throw error;
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private IDisposable EnterStore(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_storeHandle is null)
            {
                try
                {
                    RequireStorePath(_root);
                    string directory = Path.Combine(_root, Coordination.Folder);
                    RequireStorePath(directory); Directory.CreateDirectory(directory);
                    string file = Path.Combine(directory, Coordination.LockFile);
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested(); RequireStorePath(file);
                        try { _storeHandle = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
                        catch (IOException error) when (FileLockViolation.IsHeldByAnotherOwner(error))
                        {
                            if (elapsed.Elapsed >= StoreLockTimeout) throw new TimeoutException("Registry transaction ownership timed out.", error);
                            if (ct.WaitHandle.WaitOne(Coordination.Poll)) ct.ThrowIfCancellationRequested();
                        }
                    }
                    string stamp = SnapshotStamp();
                    if (_storeStamp != stamp)
                    {
                        LoadSupervisedSnapshot(); _rlLoaded = false; _storeStamp = stamp;
                    }
                }
                catch (Exception error)
                {
                    _storeHandle?.Dispose(); _storeHandle = null;
                    RecordStoreFailure("transaction", error); throw;
                }
            }
            _storeDepth++;
            return new Release(() =>
            {
                lock (_gate)
                    if (--_storeDepth == 0) { _storeHandle!.Dispose(); _storeHandle = null; }
            });
        }
    }

    private string SnapshotStamp()
    {
        var files = new[] { Path.Combine(_root, PointerName), Path.Combine(RlRoot, RlFiles.Active), Path.Combine(RlRoot, RlContinuousContract.ActiveFile) };
        var manifests = new[] { _root, RlRoot }.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateDirectories(root).Where(d => IsHash(Path.GetFileName(d)))
                .Select(d => Path.Combine(d, ManifestName)));
        return ContentHash.OfText(string.Join("\n", files.Concat(manifests).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => f + ":" + FileIdentity(f))));
    }
    private string FileIdentity(string path)
    {
        RequireStorePath(path);
        if (!File.Exists(path)) return "absent";
        if (new FileInfo(path).Length > Coordination.MetadataByteLimit) return "oversized";
        return ContentHash.OfFile(path);
    }
    private Pointer ReadStorePointer(string path, int historyLimit, bool absentAllowed = true)
    {
        RequireStorePath(path);
        if (!File.Exists(path))
        {
            if (absentAllowed && !File.Exists(path + Coordination.BackupSuffix)) return new(null, Array.Empty<string>());
            throw new InvalidDataException("Pointer is missing; explicit verified-backup recovery is required.");
        }
        if (new FileInfo(path).Length > Coordination.MetadataByteLimit) throw new InvalidDataException("Pointer exceeds its metadata bound.");
        var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Pointer is invalid.");
        if (pointer.SchemaVersion != Coordination.PointerSchema || pointer.History is null || pointer.History.Length > historyLimit
            || pointer.ActiveId is { } active && !IsHash(active) || pointer.History.Any(id => !IsHash(id))
            || pointer.History.Distinct(StringComparer.Ordinal).Count() != pointer.History.Length || pointer.History.Contains(pointer.ActiveId))
            throw new InvalidDataException("Pointer schema/identities/history are invalid.");
        return pointer;
    }

    private void LoadSupervisedSnapshot()
    {
        _pointer = ReadStorePointer(Path.Combine(_root, PointerName), PriorGenerationLimit);
        _known.Clear();
        foreach (var directory in Directory.EnumerateDirectories(_root).Where(d => IsHash(Path.GetFileName(d))))
        {
            string path = Path.Combine(directory, ManifestName);
            try
            {
                RequireStorePath(path);
                if (!File.Exists(path) || new FileInfo(path).Length > Coordination.MetadataByteLimit) continue;
                RequireStoreTree(directory);
                var manifest = JsonSerializer.Deserialize<ModelGenerationManifest>(File.ReadAllText(path), JsonOptions);
                if (manifest is not null && manifest.ModelId == Path.GetFileName(directory) && Verify(manifest, directory)) _known.Add(manifest.ModelId, manifest);
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException) { }
        }
        if (_pointer.ActiveId is { } active && !_known.ContainsKey(active)) throw new InvalidDataException("Active model generation is missing or corrupt.");
    }

    private IDisposable CreateStoreLease(string kind, string id)
    {
        string directory = Path.Combine(_root, Coordination.Folder, Coordination.LeaseFolder, kind + "_" + id);
        RequireStorePath(directory); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + Coordination.LeaseExtension);
        var handle = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        return new Release(() =>
        {
            handle.Dispose();
            try { RequireStorePath(file); File.Delete(file); } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        });
    }
    private bool HasStoreLease(string kind, string id)
    {
        string directory = Path.Combine(_root, Coordination.Folder, Coordination.LeaseFolder, kind + "_" + id);
        try
        {
            RequireStorePath(directory);
            if (!Directory.Exists(directory)) return false;
            foreach (string file in Directory.EnumerateFileSystemEntries(directory))
            {
                RequireStorePath(file);
                if (Path.GetExtension(file) != Coordination.LeaseExtension || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _)
                    || !File.Exists(file) || new FileInfo(file).Length != 0) return true;
                // Live handles deny this exclusive probe; orphan files do not imply live ownership.
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(file);
            }
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return true; }
    }
    private void RequireStorePath(string path)
    {
        string full = Path.GetFullPath(path), root = _root.TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Registry operation escapes its owned root.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked registry paths are prohibited.");
    }
    private void RequireStoreTree(string directory)
    {
        RequireStorePath(directory);
        foreach (string file in Directory.EnumerateFileSystemEntries(directory))
        {
            RequireStorePath(file);
            if (Directory.Exists(file)) RequireStoreTree(file);
        }
    }
    private void RequirePointerWritePaths(string path)
    {
        RequireStorePath(path);
        RequireStorePath(path + AtomicJsonFile.TemporarySuffix);
        RequireStorePath(path + Coordination.BackupSuffix);
    }
    // Evidence ownership is independent of the failed transaction: one process-wide leaf lock that serializes every evidence destination
    // (a superset of per-destination serialization, with no per-root state), never _gate, _mutation or the store lock.
    private static readonly object s_failureEvidenceGate = new();
    private void RecordStoreFailure(string operation, Exception error)
    {
        try
        {
            string directory = Path.Combine(_root, Coordination.Folder);
            RequireStorePath(directory);
            string file = Path.GetFullPath(Path.Combine(directory, Coordination.FailureEvidenceFileName(Environment.ProcessId)));
            lock (s_failureEvidenceGate)
            {
                Directory.CreateDirectory(directory);
                RequirePointerWritePaths(file);
                var record = new { operation, utc = DateTimeOffset.UtcNow, process_id = Environment.ProcessId, error_type = error.GetType().Name, message = error.Message };
                AtomicJsonFile.Save(file, record, JsonOptions);
            }
        }
        // Diagnostics are best effort: a failure here must never replace the operation failure being recorded; only process-fatal memory exhaustion propagates.
        catch (Exception failure) when (failure is not OutOfMemoryException) { }
    }
}
