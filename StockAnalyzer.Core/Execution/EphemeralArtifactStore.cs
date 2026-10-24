using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// The folders of the ephemeral sandbox: one session folder per process (<c>&lt;root&gt;/&lt;sessionId&gt;</c>) holding one folder per job
/// (<c>&lt;session&gt;/&lt;jobId&gt;</c>), and the clean-up of what a crashed process left behind. The session folder carries a <c>session.lock</c> file the
/// process keeps open exclusively while it lives, which is how another process tells a live session from an abandoned one. Every failure of a
/// delete is logged and never thrown: clean-up must not turn a finished job into an error.
/// </summary>
internal sealed class EphemeralArtifactStore : IDisposable
{
    public const string SessionLockFileName = "session.lock";

    /// <summary>Length of a session id. Fixed, so a path length computed before <see cref="EnsureSession"/> stays valid when the id is replaced there.</summary>
    internal const int SessionIdLength = 12;

    private readonly string _root;
    private readonly SandboxedExecutionSettings _settings;
    private readonly ILogger _logger;
    private readonly Func<string> _newSessionId;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private volatile string _sessionId;
    private FileStream? _sessionLock;
    private bool _disposed;

    /// <param name="root">The sandbox root (<see cref="EphemeralSandbox.Root"/> in production; a test passes its own folder below it).</param>
    /// <param name="newSessionId">Where session ids come from; null uses <see cref="NewSessionId"/> (only a test passes another source).</param>
    public EphemeralArtifactStore(string root, SandboxedExecutionSettings settings, ILogger logger, Func<string>? newSessionId = null)
    {
        _root = root;
        _settings = settings;
        _logger = logger;
        _newSessionId = newSessionId ?? NewSessionId;
        _sessionId = _newSessionId();
    }

    /// <summary>The id of this process's session folder. It may be replaced once, by <see cref="EnsureSession"/>, when the folder it names is taken.</summary>
    public string SessionId => _sessionId;

    public string SessionDirectory => Path.Combine(_root, SessionId);

    public string JobDirectory(string jobId) => Path.Combine(SessionDirectory, jobId);

    /// <summary>
    /// Creates the session folder and takes its lock on the first call; later calls do nothing. A folder that already exists is never adopted (a live
    /// session of another process, or an abandoned one whose files would mix into this session): the id is replaced and the next one is tried.
    /// </summary>
    /// <exception cref="IOException">No free session folder was found within the attempts.</exception>
    public void EnsureSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessionLock is not null)
            {
                return;
            }

            for (int attempt = 1; ; attempt++)
            {
                if (!Directory.Exists(SessionDirectory))
                {
                    Directory.CreateDirectory(SessionDirectory);
                    try
                    {
                        // CreateNew: a lock file another process created in the meantime (the folder is then theirs) is a collision, not a lock to reuse.
                        _sessionLock = new FileStream(Path.Combine(SessionDirectory, SessionLockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                        return;
                    }
                    catch (IOException) when (attempt < ExecutionLimits.SessionIdAttempts)
                    {
                        // Not recursive: only the empty folder created above goes; a folder another process filled in the meantime is theirs.
                        TryDeleteEmptyDirectory(SessionDirectory);
                    }
                }

                if (attempt >= ExecutionLimits.SessionIdAttempts)
                {
                    throw new IOException("A free session folder could not be found.");
                }

                _sessionId = _newSessionId();
            }
        }
    }

    /// <summary>The one place a session id is made.</summary>
    private static string NewSessionId() => Guid.NewGuid().ToString("N")[..SessionIdLength];

    /// <summary>Creates the job folder; false when it already exists (the caller then picks another id).</summary>
    public bool TryCreateJobDirectory(string jobId)
    {
        lock (_gate)
        {
            string path = JobDirectory(jobId);
            if (Directory.Exists(path))
            {
                return false;
            }

            Directory.CreateDirectory(path);
            return true;
        }
    }

    /// <summary>Keeps the job's files from deletion until the returned token is disposed.</summary>
    public IDisposable Lease(string jobId, Action onReleased)
    {
        lock (_gate)
        {
            _leases[jobId] = _leases.GetValueOrDefault(jobId) + 1;
        }

        return new Release(this, jobId, onReleased);
    }

    public bool IsLeased(string jobId)
    {
        lock (_gate)
        {
            return _leases.GetValueOrDefault(jobId) > 0;
        }
    }

    /// <summary>Deletes the job's folder unless a lease holds it; returns whether it is gone (or never existed).</summary>
    public bool TryDeleteJobDirectory(string jobId)
    {
        lock (_gate)
        {
            if (_leases.GetValueOrDefault(jobId) > 0)
            {
                return false;
            }
        }

        return TryDelete(JobDirectory(jobId));
    }

    /// <summary>
    /// Deletes the folders of other sessions that are abandoned: the last write is older than <see cref="SandboxedExecutionSettings.OrphanSessionMaxAgeHours"/>
    /// AND nobody holds the session lock (or there is none). A held lock is never deleted, whatever its age. Returns how many were deleted.
    /// </summary>
    public int CleanOrphanSessions(DateTime nowUtc)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        DateTime limit = nowUtc - TimeSpan.FromHours(_settings.OrphanSessionMaxAgeHours);
        int deleted = 0;
        try
        {
            foreach (string session in Directory.EnumerateDirectories(_root))
            {
                if (string.Equals(Path.GetFileName(session), SessionId, StringComparison.Ordinal)
                    || Directory.GetLastWriteTimeUtc(session) >= limit
                    || IsLockHeld(session))
                {
                    continue;
                }

                if (TryDelete(session))
                {
                    deleted++;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Ephemeral sandbox: the clean-up could not list the sandbox folder.");
        }

        return deleted;
    }

    /// <summary>Releases the lock and deletes the session folder with everything in it, when this store created it (never a folder it only has a name for).</summary>
    public void Dispose()
    {
        bool owned;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owned = _sessionLock is not null;
            _sessionLock?.Dispose();
            _sessionLock = null;
        }

        if (owned)
        {
            TryDelete(SessionDirectory);
        }
    }

    private static bool IsLockHeld(string session)
    {
        string lockPath = Path.Combine(session, SessionLockFileName);
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // cannot tell: treat as held, never delete
        }
    }

    private void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Ephemeral sandbox: a session folder could not be deleted and is left for the next clean-up.");
        }
    }

    private bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Ephemeral sandbox: a folder could not be deleted and is left for the next clean-up.");
            return false;
        }
    }

    private sealed class Release(EphemeralArtifactStore store, string jobId, Action onReleased) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _done, 1) != 0)
            {
                return;
            }

            lock (store._gate)
            {
                int left = store._leases.GetValueOrDefault(jobId) - 1;
                if (left <= 0)
                {
                    store._leases.Remove(jobId);
                }
                else
                {
                    store._leases[jobId] = left;
                }
            }

            onReleased();
        }
    }
}
