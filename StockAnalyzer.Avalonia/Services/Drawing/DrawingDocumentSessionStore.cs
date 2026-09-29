using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Active drawing session for a specific DrawingDocumentKey.
/// Guarantees a single source of truth across multiple open tabs/views for the same symbol+timeframe.
/// </summary>
public sealed class DrawingDocumentSession
{
    private readonly object _lock = new();
    private Guid? _activeOwnerToken;
    private readonly Dictionary<Guid, Guid> _runtimeToPersistent = new();
    private readonly Dictionary<Guid, Guid> _persistentToRuntime = new();
    private readonly ConcurrentDictionary<ChartDrawingContextType, DrawingHistoryService> _historyServices = new();

    public DrawingDocumentKey Key { get; }
    public DrawingDocumentState? CurrentDocument { get; private set; }
    public long Revision { get; private set; }
    public bool IsDirty { get; private set; }

    public long TotalHistoryPayloadBytes => _historyServices.Values.Sum(h => h.CurrentByteTotal);

    public DrawingHistoryService GetOrCreateHistory(ChartDrawingContextType contextType)
    {
        return _historyServices.GetOrAdd(contextType, _ => new DrawingHistoryService(
            Key,
            _limits.MaxHistoryEntriesPerContext,
            _limits.MaxHistoryPayloadBytesPerContext));
    }

    /// <summary>
    /// Gets the history service containing the earliest preserved undo entry in this session,
    /// along with its timestamp. Returns null if all history services in this session are empty.
    /// </summary>
    public (DateTime TimestampUtc, DrawingHistoryService Service)? GetOldestHistoryService()
    {
        DateTime? earliest = null;
        DrawingHistoryService? target = null;

        foreach (var h in _historyServices.Values)
        {
            var ts = h.OldestEntryTimestampUtc;
            if (ts.HasValue && (earliest == null || ts.Value < earliest.Value))
            {
                earliest = ts;
                target = h;
            }
        }

        if (earliest.HasValue && target != null)
        {
            return (earliest.Value, target);
        }
        return null;
    }

    /// <summary>
    /// Evicts the oldest undo history entry across all context history services in this session.
    /// Returns true if an entry was evicted with the number of freed bytes, or false if all are empty.
    /// </summary>
    public bool TryEvictOldestHistoryEntry(out long freedBytes)
    {
        var candidate = GetOldestHistoryService();
        if (candidate.HasValue)
        {
            return candidate.Value.Service.TryEvictOldestEntry(out freedBytes);
        }
        freedBytes = 0;
        return false;
    }

    public IReadOnlyDictionary<Guid, Guid> RuntimeToPersistentIds
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<Guid, Guid>(_runtimeToPersistent);
            }
        }
    }

    public event EventHandler<DrawingDocumentState>? DocumentChanged;

    private readonly DrawingInteractionSettings _limits;

    public DrawingDocumentSession(DrawingDocumentKey key, DrawingInteractionSettings? limits = null)
    {
        Key = key;
        _limits = limits ?? new DrawingInteractionSettings();
    }

    public void RegisterIdMapping(Guid runtimeId, Guid persistentId)
    {
        lock (_lock)
        {
            _runtimeToPersistent[runtimeId] = persistentId;
            _persistentToRuntime[persistentId] = runtimeId;
        }
    }

    public bool TryGetPersistentId(Guid runtimeId, out Guid persistentId)
    {
        lock (_lock)
        {
            return _runtimeToPersistent.TryGetValue(runtimeId, out persistentId);
        }
    }

    public bool TryGetRuntimeId(Guid persistentId, out Guid runtimeId)
    {
        lock (_lock)
        {
            return _persistentToRuntime.TryGetValue(persistentId, out runtimeId);
        }
    }

    /// <summary>
    /// Resolves an existing runtime ID to the currently active runtime ID via persistent mapping.
    /// Returns true if resolved to a valid mapping, or false with the original ID if not found.
    /// </summary>
    public bool TryResolveCurrentRuntimeId(Guid runtimeId, out Guid currentRuntimeId)
    {
        lock (_lock)
        {
            if (_runtimeToPersistent.TryGetValue(runtimeId, out var persistentId) &&
                _persistentToRuntime.TryGetValue(persistentId, out currentRuntimeId))
            {
                return true;
            }
            currentRuntimeId = runtimeId;
            return false;
        }
    }

    /// <summary>
    /// Checks whether an edit token can be acquired or is already owned by the given token.
    /// </summary>
    public bool CanAcquireOrOwnEdit(Guid? token)
    {
        lock (_lock)
        {
            if (_activeOwnerToken == null) return true;
            return token.HasValue && token.Value == _activeOwnerToken.Value;
        }
    }

    /// <summary>
    /// Validates whether a document update can be accepted before destructive history commits.
    /// </summary>
    public bool CanUpdateDocument(DrawingDocumentKey key, Guid? token)
    {
        lock (_lock)
        {
            if (key != Key) return false;
            if (_activeOwnerToken.HasValue && token != _activeOwnerToken.Value) return false;
            return true;
        }
    }

    /// <summary>
    /// Attempts to acquire the exclusive edit token for this document key.
    /// Returns true if acquired or already owned by the same token.
    /// </summary>
    public bool TryAcquireEdit(out Guid token)
    {
        lock (_lock)
        {
            if (_activeOwnerToken == null)
            {
                _activeOwnerToken = Guid.NewGuid();
                token = _activeOwnerToken.Value;
                return true;
            }

            token = Guid.Empty;
            return false;
        }
    }

    internal void ForceAcquireEdit(Guid token)
    {
        lock (_lock)
        {
            _activeOwnerToken = token;
        }
    }

    /// <summary>
    /// Releases the active edit token if held.
    /// </summary>
    public void ReleaseEdit(Guid token)
    {
        lock (_lock)
        {
            if (_activeOwnerToken == token)
            {
                _activeOwnerToken = null;
            }
        }
    }

    /// <summary>
    /// Resets the session with a freshly loaded document from storage, clearing dirty state.
    /// Rejects reset if another active owner holds the edit token.
    /// </summary>
    public bool TryReset(DrawingDocumentState document, Guid? token = null)
    {
        DrawingDocumentState notifiedDoc;
        lock (_lock)
        {
            if (document.Key != Key)
            {
                return false; // Mismatched document key
            }

            if (_activeOwnerToken.HasValue && token != _activeOwnerToken.Value)
            {
                return false; // Busy/Locked by another owner
            }

            Revision = document.Revision;
            CurrentDocument = document;
            IsDirty = false;
            notifiedDoc = document;
        }

        DocumentChanged?.Invoke(this, notifiedDoc);
        return true;
    }

    /// <summary>
    /// Updates the current session document state, advancing revision and marking dirty.
    /// Guarantees that CurrentDocument.Revision matches session.Revision and event notification.
    /// </summary>
    public bool TryUpdateDocument(DrawingDocumentState document, Guid? token = null)
    {
        DrawingDocumentState alignedDoc;
        lock (_lock)
        {
            if (document.Key != Key)
            {
                return false; // Mismatched document key
            }

            if (_activeOwnerToken.HasValue && token != _activeOwnerToken.Value)
            {
                return false; // Busy/Locked by another owner
            }

            Revision = Math.Max(Revision + 1, document.Revision);
            alignedDoc = new DrawingDocumentState(
                document.Version,
                document.Key,
                Revision,
                document.Contexts
            );

            CurrentDocument = alignedDoc;
            IsDirty = true;
        }

        DocumentChanged?.Invoke(this, alignedDoc);
        return true;
    }

    /// <summary>
    /// Marks the session as clean when a save operation completes successfully.
    /// </summary>
    public void MarkClean(long committedRevision)
    {
        lock (_lock)
        {
            if (committedRevision >= Revision)
            {
                IsDirty = false;
            }
        }
    }
}

/// <summary>
/// Singleton store for DrawingDocumentSession instances.
/// Guarantees that any caller requesting a session for the same DrawingDocumentKey
/// receives the exact same instance (ReferenceEquals == true).
/// </summary>
public sealed class DrawingDocumentSessionStore
{
    private readonly ConcurrentDictionary<DrawingDocumentKey, DrawingDocumentSession> _sessions = new();
    private readonly DrawingInteractionSettings _limits;

    public DrawingDocumentSessionStore(IStockAnalyzerSettings? settings = null)
    {
        _limits = settings?.DrawingInteraction ?? new DrawingInteractionSettings();
    }

    /// <summary>
    /// Gets the aggregate history payload bytes across all active sessions.
    /// </summary>
    public long TotalHistoryPayloadBytes => _sessions.Values.Sum(s => s.TotalHistoryPayloadBytes);

    /// <summary>
    /// Gets the existing session or creates a new one for the given document key.
    /// Guaranteed to return the identical reference for matching keys.
    /// </summary>
    public DrawingDocumentSession GetOrCreate(DrawingDocumentKey key)
    {
        return _sessions.GetOrAdd(key, k => new DrawingDocumentSession(k, _limits));
    }

    /// <summary>
    /// Checks whether an active session already exists for the given key.
    /// </summary>
    public bool TryGet(DrawingDocumentKey key, out DrawingDocumentSession? session)
    {
        return _sessions.TryGetValue(key, out session);
    }

    /// <summary>
    /// Removes a session from the store (typically upon closing or clearing).
    /// </summary>
    public bool TryRemove(DrawingDocumentKey key)
    {
        return _sessions.TryRemove(key, out _);
    }

    /// <summary>
    /// Enforces the global session history payload budget (default: the configured
    /// <see cref="DrawingInteractionSettings.MaxSessionHistoryPayloadBytes"/>).
    /// If aggregate history payload across all sessions exceeds the budget,
    /// deterministically evicts the globally oldest history entries until under budget.
    /// Returns the total number of entries evicted.
    /// </summary>
    public int EnforceGlobalHistoryBudget(long? maxGlobalBytes = null)
    {
        long budget = maxGlobalBytes ?? _limits.MaxSessionHistoryPayloadBytes;
        int evictedCount = 0;
        while (TotalHistoryPayloadBytes > budget)
        {
            DrawingDocumentSession? oldestSession = null;
            DateTime? oldestTimestamp = null;

            foreach (var s in _sessions.Values)
            {
                var candidate = s.GetOldestHistoryService();
                if (candidate.HasValue && (oldestTimestamp == null || candidate.Value.TimestampUtc < oldestTimestamp.Value))
                {
                    oldestTimestamp = candidate.Value.TimestampUtc;
                    oldestSession = s;
                }
            }

            if (oldestSession == null || !oldestSession.TryEvictOldestHistoryEntry(out var freed) || freed <= 0)
            {
                break;
            }

            evictedCount++;
        }

        return evictedCount;
    }
}
