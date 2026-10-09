using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Retention and listing of portfolio-RL run evidence under the Data folder. It has no registry target, activation pointer, lease
/// or deletion of runs: a run stays until its folder is removed by the user, and listing never mutates anything. The only removal is the
/// panel that a rejected transfer attempt captured itself (<see cref="TryRemoveCapturedPanel"/>). The existing T15/T16
/// registry is not involved.
/// </summary>
public sealed class RlPortfolioArtifactStore
{
    /// <summary>Folder below <see cref="Root"/> that holds the immutable panel snapshots the runs were trained on; it is not a run, so listing skips it.</summary>
    private const string PanelFolderName = "panels";
    private readonly TimeProvider _clock;
    public string Root { get; }

    public RlPortfolioArtifactStore(string dataRoot, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Root = Path.GetFullPath(Path.Combine(dataRoot, RlFiles.TrainingFolder, RlPortfolioContract.ArtifactFolder));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Folder that holds the panel snapshots of the runs (it may not exist yet); a panel folder is named by its identity.</summary>
    public string PanelsRoot => Path.Combine(Root, PanelFolderName);

    /// <summary>Folder for the panel snapshot of one run (not created here; the snapshot factory creates it and refuses an existing one).</summary>
    public string PanelDirectory(Guid panelId)
        => panelId == Guid.Empty ? throw new ArgumentException("A panel identity is required.", nameof(panelId))
            : Path.Combine(PanelsRoot, panelId.ToString("N"));

    /// <summary>
    /// Removes a panel snapshot that the caller captured for an attempt that was rejected before any run existed. Only a direct, plain child of
    /// <see cref="PanelsRoot"/> named by a GUID ("N") is removed; anything else, a link, or a folder that cannot be deleted is left alone and reported as false.
    /// </summary>
    public bool TryRemoveCapturedPanel(string panelDirectory)
    {
        if (string.IsNullOrWhiteSpace(panelDirectory)) return false;
        try
        {
            string full = Path.GetFullPath(panelDirectory);
            string parent = Path.GetFullPath(PanelsRoot);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)
                || !Directory.Exists(full)
                || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
            Directory.Delete(full, recursive: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// The folder of a panel an earlier run stored, for a run that reuses it. The folder must exist as a plain directory (not a link) and hold a panel manifest; the
    /// content is verified by <see cref="RlPanelSnapshot.Load(string, StockAnalyzer.Core.Models.Training.TrainingResourceLimits, StockAnalyzer.Core.Models.Settings.RlPortfolioSettings, RlPanelEvaluationMode, System.Threading.CancellationToken)"/>.
    /// </summary>
    public string ExistingPanelDirectory(Guid panelId)
    {
        string directory = PanelDirectory(panelId);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || !File.Exists(Path.Combine(directory, RlPortfolioContract.PanelManifest)))
            throw new InvalidDataException($"{RlPortfolioContract.UniverseErrorPrefix} the stored panel {panelId:N} does not exist.");
        return directory;
    }

    /// <summary>Creates the folder of a new run; a run identity is never reused.</summary>
    public string Begin(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A run identity is required.", nameof(runId));
        string directory = RunDirectory(runId);
        if (Directory.Exists(directory)) throw new InvalidOperationException("Portfolio artifact run identity already exists.");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Hashes the listed files of the run folder and writes the manifest last; a run can be published once.</summary>
    public async Task<RlPortfolioArtifactManifest> PublishAsync(Guid runId, string universeSha256, string calendarSha256,
        IReadOnlyCollection<string> fileNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        if (!ContentHash.IsLowerHex64(universeSha256) || !ContentHash.IsLowerHex64(calendarSha256))
            throw new ArgumentException("Universe and calendar identities must be lowercase SHA-256 values.");
        string directory = RunDirectory(runId);
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Portfolio artifact run was not begun.");
        string manifestPath = Path.Combine(directory, RlPortfolioContract.ArtifactManifest);
        if (File.Exists(manifestPath)) throw new InvalidOperationException("Portfolio artifact run is already published.");
        if (fileNames.Count == 0) throw new ArgumentException("At least one evidence file is required.", nameof(fileNames));
        var files = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (string name in fileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequirePlainName(name);
            string path = Path.Combine(directory, name);
            if (!File.Exists(path) || IsLinked(path)) throw new FileNotFoundException("Evidence file is absent or linked.", name);
            if (!files.TryAdd(name, ContentHash.OfFile(path))) throw new ArgumentException("Evidence file names must be distinct.", nameof(fileNames));
        }
        var manifest = new RlPortfolioArtifactManifest(RlPortfolioContract.ArtifactSchemaVersion, RlPortfolioContract.SpecificationId,
            runId, _clock.GetUtcNow(), universeSha256, calendarSha256, files.ToImmutable());
        cancellationToken.ThrowIfCancellationRequested();
        await AtomicJsonFile.SaveAsync(manifestPath, manifest, RlFiles.Json).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>Every run folder with its integrity status: verified runs newest first, then the others by run identity.</summary>
    public ImmutableArray<RlPortfolioArtifactEntry> List(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root)) return ImmutableArray<RlPortfolioArtifactEntry>.Empty;
        var entries = new List<RlPortfolioArtifactEntry>();
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid runId)) continue;
            entries.Add(Inspect(runId, directory));
        }
        return entries.OrderBy(e => e.Status == RlPortfolioArtifactStatus.Verified ? 0 : 1)
            .ThenByDescending(e => e.Manifest?.CreatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.RunId).ToImmutableArray();
    }

    /// <summary>
    /// The entry of one run, inspected exactly like its entry in <see cref="List"/> (every manifest file is hashed), or null when its folder is absent or
    /// <paramref name="runId"/> is empty. Only the bytes of this run are read, not those of every run. Nothing is cached: the answer is always the current disk state.
    /// </summary>
    public RlPortfolioArtifactEntry? Find(Guid runId)
    {
        if (runId == Guid.Empty) return null;
        string directory = RunDirectory(runId);
        return Directory.Exists(directory) ? Inspect(runId, directory) : null;
    }

    private RlPortfolioArtifactEntry Inspect(Guid runId, string directory)
    {
        string manifestPath = Path.Combine(directory, RlPortfolioContract.ArtifactManifest);
        RlPortfolioArtifactEntry Result(RlPortfolioArtifactStatus status, RlPortfolioArtifactManifest? manifest, string? problem)
            => new(runId, directory, status, manifest, problem);
        if (!File.Exists(manifestPath)) return Result(RlPortfolioArtifactStatus.Incomplete, null, null);
        RlPortfolioArtifactManifest? manifest;
        try
        {
            if (IsLinked(manifestPath) || new FileInfo(manifestPath).Length > RlContract.IpcFrameByteLimit)
                return Result(RlPortfolioArtifactStatus.Invalid, null, "Manifest is linked or oversized.");
            manifest = JsonSerializer.Deserialize<RlPortfolioArtifactManifest>(File.ReadAllText(manifestPath), RlFiles.Json);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { return Result(RlPortfolioArtifactStatus.Invalid, null, error.Message); }
        if (manifest is null || manifest.SchemaVersion != RlPortfolioContract.ArtifactSchemaVersion
            || manifest.SpecificationId != RlPortfolioContract.SpecificationId || manifest.RunId != runId
            || !ContentHash.IsLowerHex64(manifest.UniverseSha256) || !ContentHash.IsLowerHex64(manifest.CalendarSha256)
            || manifest.Files is null || manifest.Files.IsEmpty || manifest.Files.Any(f => !IsPlainName(f.Key) || !ContentHash.IsLowerHex64(f.Value)))
            return Result(RlPortfolioArtifactStatus.Invalid, null, "Manifest identity or schema is not supported.");
        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(directory, file.Key);
            try
            {
                if (!File.Exists(path) || IsLinked(path) || ContentHash.OfFile(path) != file.Value)
                    return Result(RlPortfolioArtifactStatus.Corrupt, manifest, file.Key);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return Result(RlPortfolioArtifactStatus.Corrupt, manifest, file.Key + ": " + error.Message); }
        }
        return Result(RlPortfolioArtifactStatus.Verified, manifest, null);
    }

    private string RunDirectory(Guid runId)
        => runId == Guid.Empty ? throw new ArgumentException("A run identity is required.", nameof(runId)) : Path.Combine(Root, runId.ToString("N"));
    private static bool IsLinked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsPlainName(string name) => !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != RlPortfolioContract.ArtifactManifest
        && !name.EndsWith(AtomicJsonFile.TemporarySuffix, StringComparison.Ordinal) && !name.EndsWith(AtomicJsonFile.BackupSuffix, StringComparison.Ordinal);
    private static void RequirePlainName(string name)
    {
        if (!IsPlainName(name)) throw new ArgumentException("Evidence files must be plain names inside the run folder and never the manifest.", nameof(name));
    }
}
