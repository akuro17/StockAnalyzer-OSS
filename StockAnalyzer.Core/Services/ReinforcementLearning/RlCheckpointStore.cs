#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Only the owning run may write; a registry transaction publishes each coherent checkpoint.</summary>
internal sealed class RlCheckpointStore : IDisposable
{
    internal sealed record Pointer(int SchemaVersion, bool Resumable, string Latest, string DirectionId);
    internal sealed record Manifest(int SchemaVersion, string SpecificationId, Guid RunId, string Id,
        int Seed, int Steps, string Phase, DateTimeOffset CreatedUtc, string Environment,
        Dictionary<string, string> Inputs, Dictionary<string, string> Files)
    { public Dictionary<string, string> Exports { get; init; } = new(StringComparer.Ordinal); }
    internal sealed record Attempt(int SchemaVersion, Guid RunId, bool Active);
    internal sealed record VerificationContext(string Directory, bool ActiveOwner, string Environment,
        Guid RunId, Dictionary<string, string> Inputs);
    internal sealed record Budget(int Transitions, int Starts, decimal ActiveSeconds);
    private readonly string _directory;
    private readonly Guid _runId;
    private readonly Func<long, bool> _capacity;
    private readonly Action<Action> _transaction;
    private readonly Stopwatch _active = Stopwatch.StartNew();
    private readonly decimal _previousSeconds;
    private Budget _budget;
    private readonly FileStream _journal;
    private readonly BinaryWriter _writer;
    private string? _pending;
    private bool _disposed;
    internal string? ResumePath { get; private set; }
    public Func<bool> PauseRequested { get; }
    internal bool PauseCommitted { get; private set; }
    internal Budget Used => _budget with { ActiveSeconds = _previousSeconds + (decimal)_active.Elapsed.TotalSeconds };
    internal static string EnvironmentIdentity => ContentHash.OfText(string.Join("\n", Environment.MachineName,
        RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, RuntimeInformation.FrameworkDescription,
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), ContentHash.OfFile(typeof(RlCheckpointStore).Assembly.Location)));
    internal RlCheckpointStore(string directory, Guid runId, Func<long, bool> capacity, Action<Action> transaction,
        Func<bool> pause, string? checkpoint = null, decimal preparationSeconds = 0)
    {
        _directory = directory; _runId = runId; _capacity = capacity; _transaction = transaction; PauseRequested = pause;
        Guard(directory);
        if (checkpoint is not null && !File.Exists(Path.Combine(directory, RlCheckpointContract.Journal)))
            throw new InvalidDataException("Unavailable: resume work journal is missing; cumulative budget cannot be reset.");
        if (checkpoint is not null && !File.Exists(Path.Combine(directory, RlCheckpointContract.Attempt)))
            throw new InvalidDataException("Unavailable: resume active-time ownership evidence is missing.");
        _budget = ReadBudget(directory);
        string attemptPath = Path.Combine(directory, RlCheckpointContract.Attempt);
        if (File.Exists(attemptPath) && Read<Attempt>(attemptPath).RunId != runId)
            throw new InvalidDataException("Unavailable: active-time evidence belongs to a different logical job.");
        if (_budget.Transitions < 0 || _budget.Starts < 0 || _budget.ActiveSeconds < 0) throw new InvalidDataException("Invalid resume budget.");
        _previousSeconds = _budget.ActiveSeconds + preparationSeconds;
        string journal = Path.Combine(directory, RlCheckpointContract.Journal); Guard(journal);
        _journal = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_journal, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            Save(Path.Combine(directory, RlCheckpointContract.Attempt), new Attempt(RlCheckpointContract.SchemaVersion, runId, true));
            Charge(_budget.Transitions, _budget.Starts);
        }
        catch { _writer.Dispose(); _journal.Dispose(); throw; }
        if (checkpoint is not null) ResumePath = Path.Combine(directory, RlCheckpointContract.Folder, checkpoint);
    }
    internal static void Guard(string path) => RlArtifactSettings.GuardPath(path);
    internal static void Save<T>(string path, T value)
    {
        Guard(path); Guard(path + AtomicJsonFile.TemporarySuffix); Guard(path + AtomicJsonFile.BackupSuffix);
        AtomicJsonFile.SaveAsync(path, value, RlFiles.Json).GetAwaiter().GetResult();
    }
    internal static T Read<T>(string path)
    {
        Guard(path);
        if (new FileInfo(path).Length > RlContract.BufferByteLimit) throw new InvalidDataException("Checkpoint metadata exceeds its bound.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), RlFiles.Json) ?? throw new InvalidDataException("Missing checkpoint metadata.");
    }
    private static void ValidateNative(string path)
    {
        Guard(path);
        using var stream = File.OpenRead(path); using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        long bytes = 0;
        foreach (var entry in archive.Entries)
        {
            bytes = checked(bytes + entry.Length);
            if (bytes > RlContract.BufferByteLimit) throw new InvalidDataException("Native checkpoint exceeds the expanded memory bound.");
        }
        if (!archive.Entries.Any(e => e.FullName.EndsWith("/data.pkl", StringComparison.Ordinal)))
            throw new InvalidDataException("Native checkpoint is not a supported tensor archive.");
    }
    internal static Budget ReadBudget(string directory, bool activeOwner = false)
    {
        string attemptPath = Path.Combine(directory, RlCheckpointContract.Attempt); Guard(attemptPath);
        if (File.Exists(attemptPath))
        {
            var attempt = Read<Attempt>(attemptPath);
            if (attempt.SchemaVersion != RlCheckpointContract.SchemaVersion || attempt.Active && !activeOwner)
                throw new InvalidDataException("Unavailable: cumulative active time is unprovable after an unfinished owner attempt.");
        }
        var budget = File.Exists(Path.Combine(directory, RlCheckpointContract.Budget))
            ? Read<Budget>(Path.Combine(directory, RlCheckpointContract.Budget)) : new(0, 0, 0);
        string path = Path.Combine(directory, RlCheckpointContract.Journal); Guard(path);
        if (!File.Exists(path)) return budget;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length % RlCheckpointContract.JournalRecordBytes != 0 || stream.Length > RlContract.BufferByteLimit)
            throw new InvalidDataException("Unavailable: incomplete or oversized resume work journal.");
        using var reader = new BinaryReader(stream);
        Budget? previous = null;
        while (stream.Position < stream.Length)
        {
            var row = new Budget(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64() / (decimal)TimeSpan.TicksPerSecond);
            if (row.Transitions < 0 || row.Starts < 0 || row.ActiveSeconds < 0 || previous is not null &&
                (row.Transitions < previous.Transitions || row.Starts < previous.Starts || row.ActiveSeconds < previous.ActiveSeconds))
                throw new InvalidDataException("Unavailable: nonmonotonic resume work journal.");
            previous = row;
        }
        return previous is null ? budget : new(Math.Max(budget.Transitions, previous.Transitions),
            Math.Max(budget.Starts, previous.Starts), Math.Max(budget.ActiveSeconds, previous.ActiveSeconds));
    }
    internal static Dictionary<string, string> InputHashes(string directory)
    {
        var names = new[] { RlFiles.Job, RlFiles.Dependencies, RlFiles.SourceManifest, RlFiles.Bars, RlFiles.Actions,
            RlFiles.RawParquet, RlFiles.MetadataParquet, RlFiles.Catalog, RlFiles.Model + RlFiles.ScalerSuffix,
            RlContinuousContract.DirectionModel, RlContinuousContract.DirectionScaler, RlContinuousContract.DirectionManifest,
            RlTemporalContract.DirectionEvidenceFile };
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            string path = Path.Combine(directory, name); Guard(path); hashes.Add(name, ContentHash.OfFile(path));
        }
        foreach (string script in new[] { RlContinuousContract.TrainerScript, RlFiles.TrainerScript, "training/rl_ppo_guard.py",
            RlFiles.DependenciesScript, "training/rl_checkpoint.py" })
            hashes.Add("code:" + script, ContentHash.OfFile(PythonScriptLocator.Resolve(script)));
        return hashes;
    }
    internal static VerificationContext CaptureVerification(string directory, bool activeOwner = false)
    {
        if (!File.Exists(Path.Combine(directory, RlCheckpointContract.Attempt)) || !File.Exists(Path.Combine(directory, RlCheckpointContract.Journal)))
            throw new InvalidDataException("Unavailable: cumulative attempt evidence is missing.");
        var attempt = Read<Attempt>(Path.Combine(directory, RlCheckpointContract.Attempt));
        _ = ReadBudget(directory, activeOwner);
        return new(directory, activeOwner, EnvironmentIdentity, attempt.RunId, InputHashes(directory));
    }
    internal static Manifest Verify(string directory, string id, bool verifyExecutionFiles = true, bool activeOwner = false,
        VerificationContext? context = null)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid owned checkpoint identity.");
        string root = Path.Combine(directory, RlCheckpointContract.Folder, id); Guard(root);
        var manifest = Read<Manifest>(Path.Combine(root, RlCheckpointContract.Manifest));
        context ??= CaptureVerification(directory, activeOwner);
        if (context.Directory != directory || context.ActiveOwner != activeOwner)
            throw new InvalidDataException("Checkpoint verification context belongs to a different owned review.");
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        if (manifest.SchemaVersion != RlCheckpointContract.SchemaVersion || manifest.SpecificationId != RlCheckpointContract.SpecificationId
            || manifest.Id != id || manifest.RunId.ToString("N") != job.RootElement.GetProperty("run_id").GetString()
            || manifest.Environment != context.Environment || !job.RootElement.TryGetProperty("continuous_defaults", out _)
            || !RlCheckpointContract.Phases.Contains(manifest.Phase) || !RlContract.CandidateSeeds.Contains(manifest.Seed)
            || manifest.Steps <= 0 || manifest.Steps % RlContinuousContract.RolloutSteps != 0 || manifest.Steps > RlContinuousContract.TrainingTransitionsPerSeed)
            throw new InvalidDataException("Unavailable: checkpoint ownership/environment/schema mismatch.");
        var inputs = context.Inputs;
        if (manifest.Inputs.Count != inputs.Count || inputs.Any(p => !manifest.Inputs.TryGetValue(p.Key, out string? h) || h != p.Value))
            throw new InvalidDataException("Unavailable: frozen checkpoint input/code identity changed.");
        if (manifest.Files.Count != 2) throw new InvalidDataException("Incomplete checkpoint.");
        foreach (string name in new[] { RlCheckpointContract.CoreState, RlCheckpointContract.PythonState })
        {
            string path = Path.Combine(root, name); Guard(path);
            if (!manifest.Files.TryGetValue(name, out string? hash) || new FileInfo(path).Length > RlContract.BufferByteLimit || hash != ContentHash.OfFile(path))
                throw new InvalidDataException("Unavailable: checkpoint content changed or exceeds bounds.");
        }
        ValidateNative(Path.Combine(root, RlCheckpointContract.PythonState));
        if (context.RunId != manifest.RunId)
            throw new InvalidDataException("Unavailable: checkpoint attempt ownership differs.");
        VerifyExports(root, manifest);
        if (verifyExecutionFiles) RlExecutionIdentity.Verify(Path.Combine(directory, RlFiles.Dependencies));
        return manifest;
    }
    private static string[] ExportFiles => new[] { RlFiles.Model, RlFiles.Native, RlFiles.Parity };
    private static void VerifyExports(string root, Manifest manifest)
    {
        if (manifest.Exports is null) throw new InvalidDataException("Unavailable: checkpoint export evidence is missing.");
        if (manifest.Phase != "worker_complete")
        {
            if (manifest.Exports.Count != 0) throw new InvalidDataException("Unexpected checkpoint exports.");
            return;
        }
        if (manifest.Exports.Count != ExportFiles.Length) throw new InvalidDataException("Unavailable: completed checkpoint exports are incomplete.");
        foreach (string name in ExportFiles)
        {
            string path = Path.Combine(root, RlCheckpointContract.ExportFolder, name); Guard(path);
            if (!manifest.Exports.TryGetValue(name, out string? hash) || new FileInfo(path).Length > RlContract.BufferByteLimit || ContentHash.OfFile(path) != hash)
                throw new InvalidDataException("Unavailable: completed checkpoint export changed or exceeds bounds.");
        }
    }
    internal void RestoreExports()
    {
        if (ResumePath is null) return;
        var manifest = Read<Manifest>(Path.Combine(ResumePath, RlCheckpointContract.Manifest));
        VerifyExports(ResumePath, manifest);
        foreach (var export in manifest.Exports)
        {
            string source = Path.Combine(ResumePath, RlCheckpointContract.ExportFolder, export.Key);
            string target = Path.Combine(_directory, export.Key), temporary = target + AtomicJsonFile.TemporarySuffix;
            Guard(source); Guard(target); Guard(temporary);
            try
            {
                File.Copy(source, temporary, overwrite: true);
                if (ContentHash.OfFile(temporary) != export.Value) throw new InvalidDataException("Committed export changed during materialization.");
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal void Charge(int transitions, int starts)
    {
        _budget = new(Math.Max(_budget.Transitions, transitions), Math.Max(_budget.Starts, starts), Used.ActiveSeconds);
        _writer.Write(_budget.Transitions); _writer.Write(_budget.Starts);
        _writer.Write(checked((long)(_budget.ActiveSeconds * TimeSpan.TicksPerSecond)));
        // Complete the OS write before financial work. Disk flush belongs to commit/owner exit;
        // physical power-loss durability is outside the adopted guarantee.
        _writer.Flush();
    }
    internal object Prepare(object core, int seed, int steps, string phase)
    {
        if (_pending is not null) throw new InvalidOperationException("Checkpoint write already pending.");
        if (!RlCheckpointContract.Phases.Contains(phase) || !RlContract.CandidateSeeds.Contains(seed)
            || steps <= 0 || steps % RlContinuousContract.RolloutSteps != 0 || steps > RlContinuousContract.TrainingTransitionsPerSeed)
            throw new InvalidDataException("Checkpoint phase/optimizer boundary is invalid.");
        if (!_capacity(0)) throw new InvalidOperationException("Checkpoint capacity admission failed; training stopped.");
        string id = Guid.NewGuid().ToString("N");
        _pending = Path.Combine(_directory, RlCheckpointContract.Folder, RlCheckpointContract.StagingPrefix + id);
        Guard(_pending); Directory.CreateDirectory(_pending);
        Save(Path.Combine(_pending, RlCheckpointContract.CoreState), core);
        var manifest = new Manifest(RlCheckpointContract.SchemaVersion, RlCheckpointContract.SpecificationId, _runId, id,
            seed, steps, phase, DateTimeOffset.UtcNow, EnvironmentIdentity, InputHashes(_directory), new());
        Save(Path.Combine(_pending, RlCheckpointContract.Manifest), manifest);
        return new { directory = _pending, id };
    }
    internal object Commit(string id)
    {
        if (_pending != Path.Combine(_directory, RlCheckpointContract.Folder, RlCheckpointContract.StagingPrefix + id))
            throw new InvalidDataException("Checkpoint preparation identity changed.");
        Guard(_pending); var manifest = Read<Manifest>(Path.Combine(_pending, RlCheckpointContract.Manifest));
        foreach (string name in new[] { RlCheckpointContract.CoreState, RlCheckpointContract.PythonState })
        {
            string path = Path.Combine(_pending, name); Guard(path);
            if (new FileInfo(path).Length > RlContract.BufferByteLimit) throw new InvalidDataException("Checkpoint file exceeds bound.");
            manifest.Files.Add(name, ContentHash.OfFile(path));
        }
        ValidateNative(Path.Combine(_pending, RlCheckpointContract.PythonState));
        if (manifest.Phase == "worker_complete")
        {
            string exports = Path.Combine(_pending, RlCheckpointContract.ExportFolder); Guard(exports); Directory.CreateDirectory(exports);
            foreach (string name in ExportFiles)
            {
                string source = Path.Combine(_directory, name), target = Path.Combine(exports, name); Guard(source); Guard(target);
                if (new FileInfo(source).Length > RlContract.BufferByteLimit) throw new InvalidDataException("Checkpoint export exceeds bound.");
                File.Copy(source, target); manifest.Exports.Add(name, ContentHash.OfFile(target));
            }
        }
        _transaction(() =>
        {
            if (!_capacity(0)) throw new InvalidOperationException("Checkpoint capacity exhausted; previous commit retained.");
            Save(Path.Combine(_pending, RlCheckpointContract.Manifest), manifest);
            string committed = Path.Combine(_directory, RlCheckpointContract.Folder, id); Guard(committed);
            Directory.Move(_pending, committed);
            Verify(_directory, id, verifyExecutionFiles: false, activeOwner: true);
            using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, RlFiles.Job)));
            string direction = job.RootElement.GetProperty("direction").GetProperty("model_id").GetString()!;
            Save(Path.Combine(_directory, RlCheckpointContract.Pointer), new Pointer(RlCheckpointContract.SchemaVersion, true, id, direction));
        });
        _pending = null; ResumePath = Path.Combine(_directory, RlCheckpointContract.Folder, id);
        Charge(_budget.Transitions, _budget.Starts);
        _journal.Flush(flushToDisk: true);
        PauseCommitted = PauseRequested();
        return new { committed = true, pause = PauseCommitted };
    }
    internal void Complete()
    {
        _transaction(() =>
        {
            string path = Path.Combine(_directory, RlCheckpointContract.Pointer);
            if (File.Exists(path)) Save(path, Read<Pointer>(path) with { Resumable = false });
        });
        Charge(_budget.Transitions, _budget.Starts);
        _journal.Flush(flushToDisk: true);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Charge(_budget.Transitions, _budget.Starts); _journal.Flush(flushToDisk: true);
            Save(Path.Combine(_directory, RlCheckpointContract.Attempt), new Attempt(RlCheckpointContract.SchemaVersion, _runId, false));
        }
        finally { _writer.Dispose(); _journal.Dispose(); }
    }
}
