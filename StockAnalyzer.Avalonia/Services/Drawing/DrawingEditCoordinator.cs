using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Interface for the central coordinator mediating drawing mutations,
/// in-memory undo/redo history, and disk persistence events.
/// </summary>
public interface IDrawingEditCoordinator : IDisposable
{
    DrawingDocumentKey Key { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    bool IsEditing { get; }
    int UndoCount { get; }
    int RedoCount { get; }

    /// <summary>
    /// Raised whenever an edit operation or undo/redo successfully commits an immutable change.
    /// This is the SOLE authoritative save trigger for the V2 drawing persistence pipeline.
    /// </summary>
    event EventHandler<DrawingDocumentState>? CommitSucceeded;

    /// <summary>
    /// Raised whenever the history availability (CanUndo/CanRedo/IsEditing) changes.
    /// </summary>
    event EventHandler? StateChanged;

    DrawingBeginResult BeginEdit(DrawingOperationKind kind);
    DrawingCommandResult Commit(DrawingEditToken token);
    DrawingCommandResult Cancel(DrawingEditToken token);
    DrawingCommandResult ExecuteEdit(DrawingOperationKind kind, Action mutateAction);
    DrawingCommandResult Undo();
    DrawingCommandResult Redo();
    bool TryResolveCurrentRuntimeId(Guid runtimeId, out Guid currentRuntimeId);
}

/// <summary>
/// Single mediation gateway bridging drawing mutation entry points (UI drag, inspector, settings dialog)
/// with the transactional <see cref="DrawingHistoryService"/> and <see cref="DrawingDocumentSession"/>.
/// Strictly enforces:
/// - Single transaction per document key (Begin -> Commit / Cancel).
/// - Exact immutable Before and After capture via <see cref="DrawingDocumentCodec.Capture"/>.
/// - Single authoritative persistence notification via <see cref="CommitSucceeded"/> on change.
/// - Fresh runtime object materialization and deferred recalculation on Undo/Redo (P3-04/S07).
/// </summary>
public sealed class DrawingEditCoordinator : IDrawingEditCoordinator
{
    private readonly object _lock = new();
    private readonly ChartObjectManager _manager;
    private readonly IDrawingLayerService? _layerService;
    private readonly DrawingHistoryService _historyService;
    private readonly DrawingDocumentSession _session;
    private readonly Func<IReadOnlyList<CoreCandleData>?>? _candlesProvider;
    private readonly IReadOnlyDictionary<ChartDrawingContextType, DrawingCoordinateMetadata>? _coordinateMetadata;
    private readonly DrawingDocumentSessionStore? _sessionStore;
    private readonly Action? _beforeMutation;
    private Guid? _sessionOwnerToken;
    private DrawingEditToken? _activeEditToken;
    private bool _isDisposed;

    public DrawingDocumentKey Key => _historyService.Key;
    public bool CanUndo => _historyService.CanUndo;
    public bool CanRedo => _historyService.CanRedo;
    public bool IsEditing => _historyService.IsEditing;
    public int UndoCount => _historyService.UndoCount;
    public int RedoCount => _historyService.RedoCount;

    public event EventHandler<DrawingDocumentState>? CommitSucceeded;
    public event EventHandler? StateChanged;

    public DrawingEditCoordinator(
        ChartObjectManager manager,
        DrawingHistoryService historyService,
        DrawingDocumentSession session,
        IDrawingLayerService? layerService = null,
        Func<IReadOnlyList<CoreCandleData>?>? candlesProvider = null,
        IReadOnlyDictionary<ChartDrawingContextType, DrawingCoordinateMetadata>? coordinateMetadata = null,
        DrawingDocumentSessionStore? sessionStore = null,
        Action? beforeMutation = null)
    {
        _beforeMutation = beforeMutation;
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _layerService = layerService;
        _candlesProvider = candlesProvider;
        _coordinateMetadata = coordinateMetadata;
        _sessionStore = sessionStore;

        _historyService.HistoryChanged += OnHistoryChanged;
    }

    /// <summary>
    /// Begins a mutation transaction by capturing the current immutable Before state.
    /// Returns Busy if an edit transaction is already active.
    /// </summary>
    public DrawingBeginResult BeginEdit(DrawingOperationKind kind)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            // F02: If coordinator already has an active edit in progress, reject as Busy immediately
            // without touching session lock or history state.
            if (_activeEditToken.HasValue)
            {
                return DrawingBeginResult.Busy();
            }

            bool acquiredSessionThisCall = false;
            if (!_sessionOwnerToken.HasValue)
            {
                if (!_session.TryAcquireEdit(out var sToken))
                {
                    return DrawingBeginResult.Busy();
                }
                _sessionOwnerToken = sToken;
                acquiredSessionThisCall = true;
            }

            RunBeforeMutation();
            var before = CaptureActiveContextState();
            var beginResult = _historyService.BeginEdit(kind, before);
            if (!beginResult.IsSuccess)
            {
                if (acquiredSessionThisCall && _sessionOwnerToken.HasValue)
                {
                    _session.ReleaseEdit(_sessionOwnerToken.Value);
                    _sessionOwnerToken = null;
                }
                return beginResult;
            }

            _activeEditToken = beginResult.Token;
            return beginResult;
        }
    }

    /// <summary>
    /// Commits the active transaction by capturing the After state.
    /// If changes are detected and accepted, updates the session and fires <see cref="CommitSucceeded"/>.
    /// </summary>
    public DrawingCommandResult Commit(DrawingEditToken token)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            // F02: Token mismatch or not active: reject without releasing session lock!
            if (!_activeEditToken.HasValue || _activeEditToken.Value != token)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Edit token mismatch or not active.");
            }

            // F05: Validate session update acceptance BEFORE committing history destructively
            if (!_session.CanUpdateDocument(Key, _sessionOwnerToken))
            {
                var cancelRes = _historyService.Cancel(token);
                _activeEditToken = null;
                if (cancelRes.IsSuccess && cancelRes.RestoredState.HasValue)
                {
                    RestoreContextState(cancelRes.RestoredState.Value, notifyPersistence: false);
                }
                if (_sessionOwnerToken.HasValue)
                {
                    _session.ReleaseEdit(_sessionOwnerToken.Value);
                    _sessionOwnerToken = null;
                }
                return DrawingCommandResult.Fail(DrawingCommandStatus.Busy, "Session rejected update.");
            }

            DrawingCommandResult result;
            try
            {
                var after = CaptureActiveContextState();
                result = _historyService.Commit(token, after);
            }
            catch
            {
                _activeEditToken = null;
                if (_sessionOwnerToken.HasValue)
                {
                    _session.ReleaseEdit(_sessionOwnerToken.Value);
                    _sessionOwnerToken = null;
                }
                throw;
            }
            _activeEditToken = null;

            if (result.IsSuccess)
            {
                _sessionStore?.EnforceGlobalHistoryBudget();

                var fullDoc = CaptureFullDocument();
                _session.TryUpdateDocument(fullDoc, _sessionOwnerToken);

                if (_sessionOwnerToken.HasValue)
                {
                    _session.ReleaseEdit(_sessionOwnerToken.Value);
                    _sessionOwnerToken = null;
                }
                try
                {
                    CommitSucceeded?.Invoke(this, fullDoc);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"CommitSucceeded subscriber error: {ex.Message}");
                }
            }
            else
            {
                if (result.Status == DrawingCommandStatus.CapacityExceeded && result.RestoredState.HasValue)
                {
                    RestoreContextState(result.RestoredState.Value, notifyPersistence: false);
                }
                if (_sessionOwnerToken.HasValue)
                {
                    _session.ReleaseEdit(_sessionOwnerToken.Value);
                    _sessionOwnerToken = null;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Cancels the active transaction, rolls back live objects to Before state, and releases the edit token.
    /// </summary>
    public DrawingCommandResult Cancel(DrawingEditToken token)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            // F02: Token mismatch or not active: reject without releasing session lock!
            if (!_activeEditToken.HasValue || _activeEditToken.Value != token)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidArgument, "Edit token mismatch or not active.");
            }

            var result = _historyService.Cancel(token);
            _activeEditToken = null;

            if (result.IsSuccess && result.RestoredState.HasValue)
            {
                RestoreContextState(result.RestoredState.Value, notifyPersistence: false);
            }
            if (_sessionOwnerToken.HasValue)
            {
                _session.ReleaseEdit(_sessionOwnerToken.Value);
                _sessionOwnerToken = null;
            }
            return result;
        }
    }

    /// <summary>
    /// Executes an in-memory mutation within a single transactional Begin/Commit scope.
    /// Automatically cancels the transaction if the mutation throws an exception.
    /// </summary>
    public DrawingCommandResult ExecuteEdit(DrawingOperationKind kind, Action mutateAction)
    {
        if (mutateAction == null) throw new ArgumentNullException(nameof(mutateAction));

        DrawingBeginResult begin;
        lock (_lock)
        {
            ThrowIfDisposed();
            begin = BeginEdit(kind);
            if (!begin.IsSuccess)
            {
                return DrawingCommandResult.Fail(begin.Status);
            }
        }

        try
        {
            mutateAction();
            return Commit(begin.Token);
        }
        catch
        {
            Cancel(begin.Token);
            throw;
        }
    }

    /// <summary>
    /// Reverses the most recently committed operation, restores fresh runtime objects,
    /// re-runs deferred computations, and notifies persistence.
    /// </summary>
    public DrawingCommandResult Undo()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_activeEditToken.HasValue)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Busy, "Cannot undo while an edit transaction is active.");
            }

            if (!_session.TryAcquireEdit(out var tempToken))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Busy, "Cannot undo while session is busy.");
            }

            try
            {
                RunBeforeMutation();
                var result = _historyService.Undo();
                if (result.IsSuccess && result.RestoredState.HasValue)
                {
                    try
                    {
                        RestoreContextState(result.RestoredState.Value, notifyPersistence: true, ownerToken: tempToken);
                    }
                    catch (Exception ex)
                    {
                        // Law H6 / NFR-02: Restoration failure is All-or-Nothing.
                        // Roll history pointer back forward so state doesn't diverge.
                        _historyService.Redo();
                        return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidState, ex.Message);
                    }
                }
                return result;
            }
            finally
            {
                _session.ReleaseEdit(tempToken);
            }
        }
    }

    /// <summary>
    /// Re-applies the most recently undone operation, restores fresh runtime objects,
    /// re-runs deferred computations, and notifies persistence.
    /// </summary>
    public DrawingCommandResult Redo()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_activeEditToken.HasValue)
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Busy, "Cannot redo while an edit transaction is active.");
            }

            if (!_session.TryAcquireEdit(out var tempToken))
            {
                return DrawingCommandResult.Fail(DrawingCommandStatus.Busy, "Cannot redo while session is busy.");
            }

            try
            {
                RunBeforeMutation();
                var result = _historyService.Redo();
                if (result.IsSuccess && result.RestoredState.HasValue)
                {
                    try
                    {
                        RestoreContextState(result.RestoredState.Value, notifyPersistence: true, ownerToken: tempToken);
                    }
                    catch (Exception ex)
                    {
                        // Law H6 / NFR-02: Restoration failure is All-or-Nothing.
                        // Roll history pointer back so state doesn't diverge.
                        _historyService.Undo();
                        return DrawingCommandResult.Fail(DrawingCommandStatus.InvalidState, ex.Message);
                    }
                }
                return result;
            }
            finally
            {
                _session.ReleaseEdit(tempToken);
            }
        }
    }

    private FrozenContextState CaptureActiveContextState()
    {
        var fullDoc = CaptureFullDocument();
        string activeContextName = _manager.CurrentContext.ToString();

        var matched = fullDoc.Contexts.FirstOrDefault(c => c.ContextName == activeContextName);
        if (matched.ContextName != null)
        {
            return FrozenContextState.FromContextState(matched);
        }

        return new FrozenContextState(
            activeContextName,
            Array.Empty<DrawingLayerRecord>(),
            Array.Empty<DrawingObjectRecord>());
    }

    /// <summary>
    /// Lets the owner bring this tab's objects up to date with what other tabs already persisted. Called only after
    /// the session edit ownership is held, so no other tab can commit concurrently. A failing callback must not block editing.
    /// </summary>
    private void RunBeforeMutation()
    {
        try
        {
            _beforeMutation?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Before-mutation refresh error: {ex.Message}");
        }
    }

    private DrawingDocumentState CaptureFullDocument()
    {
        var layersByContext = new Dictionary<ChartDrawingContextType, IReadOnlyList<DrawingLayerRecord>>();
        if (_layerService != null)
        {
            layersByContext[_manager.CurrentContext] = _layerService.GetAllLayers().ToList();
        }

        // F07: Retain non-active context layers from session document
        if (_session.CurrentDocument.HasValue)
        {
            foreach (var ctx in _session.CurrentDocument.Value.Contexts)
            {
                if (Enum.TryParse<ChartDrawingContextType>(ctx.ContextName, out var ctxType) &&
                    ctxType != _manager.CurrentContext &&
                    ctx.Layers != null)
                {
                    layersByContext[ctxType] = ctx.Layers;
                }
            }
        }

        var doc = DrawingDocumentCodec.Capture(
            _manager,
            Key,
            revision: _session.Revision,
            runtimeToPersistentIds: _session.RuntimeToPersistentIds,
            coordinateMetadata: _coordinateMetadata,
            layers: layersByContext.GetValueOrDefault(_manager.CurrentContext),
            layersByContext: layersByContext);

        // Synchronize newly generated persistent IDs back to the session store for all contexts
        var snapshot = _manager.GetSnapshot();
        foreach (var ctx in doc.Contexts)
        {
            if (Enum.TryParse<ChartDrawingContextType>(ctx.ContextName, out var cType) &&
                snapshot.TryGetValue(cType, out var cObjs) &&
                ctx.Objects != null)
            {
                for (int i = 0; i < Math.Min(cObjs.Count, ctx.Objects.Count); i++)
                {
                    _session.RegisterIdMapping(cObjs[i].Id, ctx.Objects[i].ObjectId);
                }
            }
        }

        return doc;
    }

    private void RestoreContextState(FrozenContextState restoredState, bool notifyPersistence = true, Guid? ownerToken = null)
    {
        if (!Enum.TryParse<ChartDrawingContextType>(restoredState.ContextName, out var targetContextType))
        {
            targetContextType = _manager.CurrentContext;
        }

        var contexts = new List<DrawingContextState>
        {
            restoredState.ToContextState()
        };

        // Retain any other non-active contexts already recorded in the session document
        if (_session.CurrentDocument.HasValue)
        {
            foreach (var ctx in _session.CurrentDocument.Value.Contexts)
            {
                if (!string.Equals(ctx.ContextName, restoredState.ContextName, StringComparison.Ordinal))
                {
                    contexts.Add(ctx);
                }
            }
        }

        var docToMaterialize = new DrawingDocumentState(
            DrawingDocumentState.CurrentSchemaVersion,
            Key,
            revision: _session.Revision + 1,
            contexts: contexts);

        // Preserve ephemeral Snapshot of InformationObject across state restoration
        var prevInfoSnapshot = _manager.InformationObject?.Snapshot;

        // Materialize fresh runtime objects (P3-04: zero live reference reuse)
        var materialized = DrawingDocumentCodec.Materialize(docToMaterialize);
        bool succeeded = false;
        try
        {
            // Stage recalculation of deferred derived states before exposing to _manager (R04)
            var candles = _candlesProvider?.Invoke();
            if (candles != null && candles.Count > 0)
            {
                foreach (var obj in materialized.Objects.Values.SelectMany(x => x))
                {
                    DeferredComputationRecalculator.TryRecalculate(obj, candles);
                }
            }

            // 1. Update ChartObjectManager using LoadSnapshot (fires Synced only, does not trigger Changed)
            _manager.LoadSnapshot(materialized.Objects);

            // Ownership transferred to _manager! Detach immediately so Dispose in finally will never dispose live objects (F04).
            materialized.DetachObjects();
            succeeded = true;

            if (prevInfoSnapshot != null && _manager.InformationObject != null && _manager.InformationObject.Snapshot == null)
            {
                _manager.InformationObject.Snapshot = prevInfoSnapshot;
            }

            // 2. Re-register runtime <-> persistent ID mappings in session
            foreach (var (pid, rid) in materialized.PersistentToRuntimeIds)
            {
                _session.RegisterIdMapping(rid, pid);
            }

            // 2b. Restore link groups (persistent ids -> freshly materialized runtime ids)
            foreach (var (linkContext, linkRecords) in materialized.LinkGroups)
            {
                _manager.LoadLinkGroups(linkContext, linkRecords, materialized.PersistentToRuntimeIds);
            }

            // 3. Restore layers if layer service is active
            if (_layerService != null && materialized.Layers.TryGetValue(targetContextType, out var restoredLayers))
            {
                _layerService.InitializeLayers(restoredLayers);
            }

            // 4. Update session document and notify persistence listeners ONLY if requested (F06)
            if (notifyPersistence)
            {
                var freshDoc = CaptureFullDocument();
                _session.TryUpdateDocument(freshDoc, ownerToken ?? _sessionOwnerToken);
                try
                {
                    CommitSucceeded?.Invoke(this, freshDoc);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"CommitSucceeded subscriber error: {ex.Message}");
                }
            }
        }
        finally
        {
            if (!succeeded)
            {
                materialized.Dispose();
            }
        }
    }

    public bool TryResolveCurrentRuntimeId(Guid runtimeId, out Guid currentRuntimeId)
    {
        return _session.TryResolveCurrentRuntimeId(runtimeId, out currentRuntimeId);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(DrawingEditCoordinator));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // F03: Safely cancel active edit in history so history is not stuck in Busy upon rebind
            if (_activeEditToken.HasValue)
            {
                try
                {
                    _historyService.Cancel(_activeEditToken.Value);
                }
                catch
                {
                    // Best effort cleanup during disposal
                }
                _activeEditToken = null;
            }

            if (_sessionOwnerToken.HasValue)
            {
                _session.ReleaseEdit(_sessionOwnerToken.Value);
                _sessionOwnerToken = null;
            }

            _historyService.HistoryChanged -= OnHistoryChanged;
            // Note: _historyService is owned and preserved by DrawingDocumentSession across view bindings (R06).
        }
    }
}
