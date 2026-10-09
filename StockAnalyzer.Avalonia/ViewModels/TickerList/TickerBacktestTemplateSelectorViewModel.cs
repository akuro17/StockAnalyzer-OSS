using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>
/// Backs the Tickers tab's "Backtest Template" dropdown placed next to "Active Columns".
/// The first entry is the sentinel <see cref="DefaultEntry"/> (Id == Guid.Empty, "Backtest Template").
/// Selecting the sentinel resets/clears backtest results on the Ticker list to "-".
/// Selecting a saved template runs the backtest against the displayed tickers.
/// Re-fetches from <see cref="ITemplateService"/> on dropdown open.
/// </summary>
public sealed partial class TickerBacktestTemplateSelectorViewModel : ObservableObject, IDisposable
{
    private readonly ITemplateService? _templateService;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger _logger;
    private bool _isDisposed;
    private bool _isSyncingSelection;
    private int _selectionVersion;

    /// <summary>Sentinel first entry (<c>Id == Guid.Empty</c>): clear backtest results.</summary>
    public BacktestConditionTemplate DefaultEntry { get; }

    public BulkObservableCollection<BacktestConditionTemplate> Entries { get; } = new();

    [ObservableProperty]
    private BacktestConditionTemplate? _selectedEntry;

    /// <summary>Two-way bound to the ComboBox's IsDropDownOpen: opening it re-fetches the saved templates.</summary>
    [ObservableProperty]
    private bool _isDropDownOpen;

    /// <summary>Raised when the user (or a restore) changes the selected entry.</summary>
    public event EventHandler? SelectionChanged;

    public TickerBacktestTemplateSelectorViewModel(
        ITemplateService? templateService,
        string defaultEntryName,
        IDispatcherService dispatcher,
        ILogger? logger = null)
    {
        _templateService = templateService;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger.Instance;

        DefaultEntry = new BacktestConditionTemplate { Id = Guid.Empty, Name = defaultEntryName };
        Entries.Add(DefaultEntry);
        SelectedEntry = DefaultEntry;
    }

    public bool IsDefaultEntry(BacktestConditionTemplate? entry) => entry == null || entry.Id == Guid.Empty;

    partial void OnIsDropDownOpenChanged(bool value)
    {
        if (value)
        {
            _ = ReloadEntriesAsync();
        }
    }

    partial void OnSelectedEntryChanged(BacktestConditionTemplate? oldValue, BacktestConditionTemplate? newValue)
    {
        if (_isSyncingSelection) return;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReloadEntriesAsync()
    {
        if (_templateService == null || _isDisposed) return;
        int version = ++_selectionVersion;

        try
        {
            IReadOnlyList<BacktestConditionTemplate> templates = await _templateService
                .GetAllAsync<BacktestConditionTemplate>(TemplateType.BacktestCondition)
                .ConfigureAwait(false);

            if (_isDisposed || version != _selectionVersion) return;

            await _dispatcher.PostAsync(() =>
            {
                if (_isDisposed || version != _selectionVersion) return Task.CompletedTask;

                var currentSelectedId = SelectedEntry?.Id ?? Guid.Empty;
                var ordered = templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

                _isSyncingSelection = true;
                try
                {
                    Entries.BeginBulkUpdate();
                    Entries.Clear();
                    Entries.Add(DefaultEntry);
                    foreach (var t in ordered)
                    {
                        Entries.Add(t);
                    }
                    Entries.EndBulkUpdate();

                    var match = Entries.FirstOrDefault(e => e.Id == currentSelectedId) ?? DefaultEntry;
                    SelectedEntry = match;
                }
                finally
                {
                    _isSyncingSelection = false;
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload backtest condition templates.");
        }
    }

    public void ResetToDefault()
    {
        SelectedEntry = DefaultEntry;
    }

    public void Dispose()
    {
        _isDisposed = true;
        SelectionChanged = null;
    }
}
