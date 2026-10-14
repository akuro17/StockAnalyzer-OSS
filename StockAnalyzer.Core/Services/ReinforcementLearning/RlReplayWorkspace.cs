using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// A private temporary directory <c>Data\RlReplay\&lt;guid&gt;</c> for one external replay or one calendar read. It holds a <c>.lock</c> file
/// open (no sharing) for its whole life, so a sweep of stale directories (a crashed run) never touches one that is still in use. The directory
/// is deleted on dispose; a failed delete is logged and never changes the result of the replay.
/// </summary>
internal sealed class RlReplayWorkspace : IDisposable
{
    internal const string FolderName = "RlReplay";
    internal const string LockFileName = ".lock";
    private static readonly ConcurrentDictionary<string, bool> SweptRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileStream _lock;
    private readonly ILogger _logger;

    public string Directory { get; }

    private RlReplayWorkspace(string directory, FileStream lockStream, ILogger logger)
    {
        Directory = directory;
        _lock = lockStream;
        _logger = logger;
    }

    /// <summary>Creates a fresh workspace below the data root; the first call per data root also sweeps stale workspaces.</summary>
    /// <param name="dataRoot">The <c>Data</c> folder; null resolves the application data folder.</param>
    public static RlReplayWorkspace Create(string? dataRoot, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        string folder = Path.Combine(dataRoot ?? PathDiscovery.ResolveDataPath(null, "Data"), FolderName);
        if (SweptRoots.TryAdd(Path.GetFullPath(folder), true)) SweepStale(folder, logger);
        string directory = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var stream = new FileStream(Path.Combine(directory, LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        return new RlReplayWorkspace(directory, stream, logger);
    }

    /// <summary>
    /// Deletes every child directory whose lock file can be opened exclusively (stale); a directory still locked is skipped. A child directory
    /// WITHOUT a lock file (a run that stopped between creating the folder and its lock, or a removed lock) is deleted only when it is older than
    /// <see cref="RlReplayWorkspaceLimits.LocklessFolderMinimumAge"/>, so a workspace that is being created right now is never touched.
    /// </summary>
    internal static void SweepStale(string folder, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (!System.IO.Directory.Exists(folder)) return;
        foreach (string child in System.IO.Directory.EnumerateDirectories(folder))
        {
            try
            {
                string lockPath = Path.Combine(child, LockFileName);
                if (!File.Exists(lockPath))
                {
                    if (DateTime.UtcNow - System.IO.Directory.GetCreationTimeUtc(child) > RlReplayWorkspaceLimits.LocklessFolderMinimumAge)
                        System.IO.Directory.Delete(child, true);
                    continue;
                }
                using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                System.IO.Directory.Delete(child, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use by another running instance, or not removable now: leave it.
                logger.LogDebug(ex, "RL replay workspace {Directory} was not swept", child);
            }
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        try { System.IO.Directory.Delete(Directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning(ex, "RL replay workspace {Directory} could not be deleted", Directory); }
    }
}
