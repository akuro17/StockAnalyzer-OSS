using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models.UI;

namespace StockAnalyzer.Avalonia.Services;

public sealed class LayoutSaveScheduler : ILayoutSaveScheduler
{
    private readonly LayoutStateStore _stateStore;
    private readonly ILogger<LayoutSaveScheduler> _logger;
    private readonly int _throttleMs;
    
    private Func<Task>? _saveAction;
    private Timer? _debounceTimer;
    private bool _isDisposed;
    
    private readonly object _timerLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public LayoutSaveScheduler(
        LayoutStateStore stateStore,
        ILogger<LayoutSaveScheduler>? logger = null,
        int throttleMs = 500)
    {
        if (throttleMs <= 0 || throttleMs > 60000)
            throw new ArgumentOutOfRangeException(nameof(throttleMs), "Throttle milliseconds must be between 1 and 60000.");

        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? NullLogger<LayoutSaveScheduler>.Instance;
        _throttleMs = throttleMs;
        
        // Initialize the reusable timer (does not tick initially)
        _debounceTimer = new Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void RegisterSaveAction(Func<Task> saveAction)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_saveAction != null)
            throw new InvalidOperationException("Save action has already been registered and cannot be overwritten.");

        _saveAction = saveAction ?? throw new ArgumentNullException(nameof(saveAction));
    }

    public void RequestSave(LayoutChangeReason reason)
    {
        if (_isDisposed) return;

        // Ignore requests from states other than Ready, except during Shutdown when immediate save is permitted.
        if (_stateStore.LifecycleState != WorkspaceLifecycleState.Ready && reason != LayoutChangeReason.Shutdown)
        {
            _logger.LogDebug("Save request ignored. Lifecycle state is: {State}", _stateStore.LifecycleState);
            return;
        }

        if (reason == LayoutChangeReason.Shutdown)
        {
            _logger.LogInformation("Immediate save triggered due to Shutdown.");
            _ = ForceSaveImmediateAsync();
            return;
        }

        _logger.LogInformation("Save layout requested due to: {Reason}. Scheduling debouncing timer ({Delay}ms)...", reason, _throttleMs);

        lock (_timerLock)
        {
            if (_isDisposed) return;
            // Slide/reschedule the execution window with absolutely zero allocation!
            _debounceTimer?.Change(_throttleMs, Timeout.Infinite);
        }
    }

    private void OnTimerCallback(object? state)
    {
        if (_isDisposed) return;

        _logger.LogDebug("Debounce timer elapsed. Triggering layout save operation.");
        _ = ForceSaveImmediateAsync();
    }

    public async Task ForceSaveImmediateAsync()
    {
        if (_isDisposed || _saveAction == null)
        {
            _logger.LogWarning("Cannot save. Scheduler is either disposed or save action is not registered.");
            return;
        }

        // Acquire serialization exclusive gate control.
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Executing safe serialized layout write operation...");
            await _saveAction().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during actual layout save execution.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Cancel()
    {
        lock (_timerLock)
        {
            if (_isDisposed) return;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        // Wait for any active save action to complete before releasing resources.
        await _saveGate.WaitAsync().ConfigureAwait(false);
        _saveGate.Dispose();
        
        _logger.LogInformation("LayoutSaveScheduler disposed cleanly.");
    }
}
