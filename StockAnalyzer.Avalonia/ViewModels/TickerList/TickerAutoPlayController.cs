using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>Settings read by <see cref="TickerAutoPlayController"/> at each timer arm / tick.</summary>
public readonly record struct TickerAutoPlaySettingsSnapshot(int IntervalSeconds, bool StopAtListEnd, bool StopOnListChange);

/// <summary>
/// Timer-driven sequential ticker selection of the Tickers tab (Y:\Temp\sa_implementation_plan_TickerAutoPlay.md).
/// Avalonia-free and decoupled from <c>TickerListViewModel</c> (SA_ARCHITECTURE_RULES: Composed Child ViewModel
/// Decoupling): everything it needs arrives as constructor delegates. Every public member is called on the UI
/// thread; the scheduled callback only posts to the dispatcher, so state is mutated on the UI thread only.
/// One-shot timers are re-armed after each tick, so ticks never overlap.
/// </summary>
public sealed class TickerAutoPlayController : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _getOrderedSymbols;
    private readonly Func<string?> _getSelectedSymbol;
    private readonly Func<string, bool> _selectSymbol;
    private readonly Func<TickerAutoPlaySettingsSnapshot> _getSettings;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger _logger;

    private IDisposable? _timerHandle;
    private int _armVersion;
    private bool _isSelecting;
    private bool _isDisposed;

    // Last symbol this controller tried to select. Guarantees progress when a selection fails (the selected
    // ticker then does not move); cleared by any external selection.
    private string? _cursor;

    public bool IsRunning { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="IsRunning"/> changes.</summary>
    public event EventHandler? IsRunningChanged;

    public TickerAutoPlayController(
        Func<IReadOnlyList<string>> getOrderedSymbols,
        Func<string?> getSelectedSymbol,
        Func<string, bool> selectSymbol,
        Func<TickerAutoPlaySettingsSnapshot> getSettings,
        Func<TimeSpan, Action, IDisposable> schedule,
        IDispatcherService dispatcher,
        ILogger? logger = null)
    {
        _getOrderedSymbols = getOrderedSymbols ?? throw new ArgumentNullException(nameof(getOrderedSymbols));
        _getSelectedSymbol = getSelectedSymbol ?? throw new ArgumentNullException(nameof(getSelectedSymbol));
        _selectSymbol = selectSymbol ?? throw new ArgumentNullException(nameof(selectSymbol));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Production timer: a one-shot <see cref="System.Threading.Timer"/> (same primitive as LayoutSaveScheduler).</summary>
    public static IDisposable ScheduleWithThreadingTimer(TimeSpan due, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new Timer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Starts auto play. Returns <c>false</c> (and stays stopped) when the list is empty.</summary>
    public bool Start()
    {
        if (_isDisposed) return false;
        if (IsRunning) return true;

        var ordered = _getOrderedSymbols();
        if (ordered.Count == 0) return false;

        _cursor = null;
        SetRunning(true);

        // Nothing selected (or the selection is not in the list): start from the top, displayed at once.
        var selected = _getSelectedSymbol();
        if (string.IsNullOrEmpty(selected) || IndexOf(ordered, selected) < 0)
        {
            AttemptSelect(ordered[0]);
        }

        Arm();
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _armVersion++;
        DisposeTimer();
        SetRunning(false);
    }

    /// <summary>The selected ticker changed for a reason other than this controller: restart the countdown
    /// and continue from the new selection.</summary>
    public void NotifyExternalSelection()
    {
        if (_isSelecting || _isDisposed) return;

        _cursor = null;
        if (IsRunning) Arm();
    }

    /// <summary>The user moved to a different ticker list.</summary>
    public void NotifyListTransition()
    {
        if (!IsRunning || _isDisposed) return;

        if (_getSettings().StopOnListChange)
        {
            Stop();
            return;
        }

        // Full interval again, so a tick never runs against a half-rebuilt list.
        _cursor = null;
        Arm();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        Stop();
        _isDisposed = true;
    }

    private void Arm()
    {
        DisposeTimer();
        var version = ++_armVersion;
        var interval = TimeSpan.FromSeconds(_getSettings().IntervalSeconds);
        _timerHandle = _schedule(interval, () => _dispatcher.Post(() => OnTimer(version)));
    }

    private void OnTimer(int version)
    {
        if (_isDisposed || !IsRunning || version != _armVersion) return;

        var ordered = _getOrderedSymbols();
        if (ordered.Count == 0)
        {
            Stop();
            return;
        }

        var current = _cursor ?? _getSelectedSymbol();
        var index = IndexOf(ordered, current);

        string next;
        if (index < 0)
        {
            next = ordered[0];
        }
        else if (index + 1 < ordered.Count)
        {
            next = ordered[index + 1];
        }
        else if (_getSettings().StopAtListEnd)
        {
            Stop();
            return;
        }
        else
        {
            next = ordered[0];
        }

        if (!string.Equals(next, current, StringComparison.Ordinal))
        {
            AttemptSelect(next);
        }

        // AttemptSelect can re-enter (a selection may stop or restart auto play); only re-arm a live run.
        if (IsRunning && version == _armVersion) Arm();
    }

    private void AttemptSelect(string symbol)
    {
        _isSelecting = true;
        try
        {
            _cursor = symbol;
            if (!_selectSymbol(symbol))
            {
                _logger.LogWarning("Auto play could not select ticker {Symbol}; continuing with the next one.", symbol);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto play failed to select ticker {Symbol}; continuing with the next one.", symbol);
        }
        finally
        {
            _isSelecting = false;
        }
    }

    private static int IndexOf(IReadOnlyList<string> ordered, string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i], symbol, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private void DisposeTimer()
    {
        _timerHandle?.Dispose();
        _timerHandle = null;
    }

    private void SetRunning(bool value)
    {
        if (IsRunning == value) return;
        IsRunning = value;
        IsRunningChanged?.Invoke(this, EventArgs.Empty);
    }
}
