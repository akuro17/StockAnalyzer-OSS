using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

public enum AttentionViewState
{
    /// <summary>No prediction produced attention for this chart.</summary>
    None = 0,
    Loading = 1,
    Ready = 2,
    Unsupported = 3,
    Failed = 4,
}

/// <summary>
/// State and presentation of the attention heat overlay (T24b): which of {nothing, loading, ready, unsupported, failed} the chart is in, the immutable
/// <see cref="Snapshot"/> the renderer draws, and the localized legend / status text. It holds no Avalonia or Skia type and does no model work. A result is
/// published only for the request key most recently begun (an older one is discarded), and always through <see cref="IDispatcherService"/>.
/// </summary>
public sealed partial class AttentionHeatmapViewModel : ObservableObject, IDisposable
{
    private readonly IDispatcherService _dispatcher;
    /// <summary>Line break of the multi-line tooltip texts (the same character the locale strings use).</summary>
    private const char NewLine = '\n';

    private readonly object _gate = new();
    private AttentionRequestKey? _pending;
    private bool _disposed;
    private string? _reason;

    [ObservableProperty] private AttentionViewState _state = AttentionViewState.None;
    [ObservableProperty] private AttentionSnapshot? _snapshot;
    [ObservableProperty] private bool _chartTypeSupported = true;
    [ObservableProperty] private double _legendLeft;
    [ObservableProperty] private double _legendTop;
    [ObservableProperty] private string? _legendText;
    [ObservableProperty] private string? _tooltip;
    [ObservableProperty] private string? _statusText;

    public AttentionHeatmapViewModel(IDispatcherService dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ChartTypeSupportedChanged();
    }

    /// <summary>The legend (swatch + text) is shown only when attention is ready and the chart type can carry it.</summary>
    public bool HasLegend => State == AttentionViewState.Ready && ChartTypeSupported;

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    /// <summary>The request key whose result is awaited (null = none).</summary>
    public AttentionRequestKey? Pending
    {
        get
        {
            lock (_gate) return _pending;
        }
    }

    /// <summary>Starts a request: any older request can no longer publish, and the previous snapshot stops being shown at once.</summary>
    public void Begin(AttentionRequestKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_disposed) return;
            _pending = key;
        }

        _dispatcher.Post(() =>
        {
            if (!IsCurrent(key)) return;
            Apply(AttentionViewState.Loading, null, null);
        });
    }

    /// <summary>Publishes the outcome of <paramref name="key"/>; it is discarded when a newer request began, or after <see cref="Clear"/> / <see cref="Dispose"/>.</summary>
    public void Publish(AttentionRequestKey key, AttentionResult result)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);
        _dispatcher.Post(() =>
        {
            if (!IsCurrent(key)) return;
            switch (result.Status)
            {
                case AttentionStatus.Ready when result.Snapshot is not null && result.Snapshot.Key == key:
                    Apply(AttentionViewState.Ready, null, result.Snapshot);
                    break;
                case AttentionStatus.Unsupported:
                    Apply(AttentionViewState.Unsupported, result.Reason, null);
                    break;
                default:
                    Apply(AttentionViewState.Failed, null, null);
                    break;
            }
        });
    }

    /// <summary>The displayed prediction has nothing a request can be made for (an unregistered model, or an ensemble without its service): unsupported, with no request.</summary>
    public void MarkUnsupported(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = null;
        }

        _dispatcher.Post(() => Apply(AttentionViewState.Unsupported, reason, null));
    }

    /// <summary>Drops the snapshot and any pending request (symbol / timeframe change, a new load, no prediction).</summary>
    public void Clear()
    {
        lock (_gate) _pending = null;
        _dispatcher.Post(() => Apply(AttentionViewState.None, null, null));
    }

    public void SetChartType(ChartType chartType)
    {
        bool supported = chartType.IsTimeBased();   // the one declaration of this decision is the chart type registry
        if (supported == ChartTypeSupported) return;
        ChartTypeSupported = supported;
    }

    public void SetLegendPosition(double left, double top)
    {
        LegendLeft = left;
        LegendTop = top;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = null;
        }
    }

    partial void OnChartTypeSupportedChanged(bool value) => ChartTypeSupportedChanged();

    private void ChartTypeSupportedChanged()
    {
        RefreshText();
        OnPropertyChanged(nameof(HasLegend));
    }

    private bool IsCurrent(AttentionRequestKey key)
    {
        lock (_gate) return !_disposed && Equals(_pending, key);
    }

    private void Apply(AttentionViewState state, string? reason, AttentionSnapshot? snapshot)
    {
        _reason = reason;
        Snapshot = snapshot;
        State = state;
        RefreshText();
        OnPropertyChanged(nameof(HasLegend));
    }

    /// <summary>Re-resolves the localized text (called on state changes; the legend binds to these strings).</summary>
    public void RefreshText()
    {
        var localization = LocalizationManager.Instance;
        StatusText = State switch
        {
            AttentionViewState.Loading => localization.Get("Attention_Loading"),
            AttentionViewState.Failed => localization.Get("Attention_Failed"),
            AttentionViewState.Unsupported => localization.Get(UnsupportedKey(_reason)),
            AttentionViewState.Ready when !ChartTypeSupported => localization.Get("Attention_Unsupported_ChartType"),
            _ => null,
        };
        var ensemble = Snapshot is { IsEnsemble: true } mixed ? mixed : null;
        LegendText = !HasLegend ? null
            : ensemble is null ? localization.Get("Attention_Legend")
            : string.Format(CultureInfo.CurrentCulture, localization.Get("Attention_Legend_Ensemble"), ensemble.CapableMembers, ensemble.MemberCount,
                ensemble.CoverageWeight);
        Tooltip = HasLegend && Snapshot is { } shot
            ? ensemble is null
                ? string.Format(CultureInfo.CurrentCulture, localization.Get("Attention_Tooltip"), shot.Heads, shot.Timestamps.Length, shot.Key.Anchor, shot.Key.ModelId)
                : string.Format(CultureInfo.CurrentCulture, localization.Get("Attention_Tooltip_Ensemble"), ensemble.CapableMembers, ensemble.MemberCount,
                    shot.Timestamps.Length, shot.Key.Anchor, ensemble.CoverageWeight)
                    + (ensemble.WindowsDiffer ? NewLine + localization.Get("Attention_Tooltip_WindowsDiffer") : "")
            : null;
        OnPropertyChanged(nameof(HasStatus));
    }

    private static string UnsupportedKey(string? reason) => reason switch
    {
        AttentionContract.UnsupportedReason.Timeframe => "Attention_Unsupported_Timeframe",
        AttentionContract.UnsupportedReason.NoModel => "Attention_Unsupported_Ensemble",
        AttentionContract.UnsupportedReason.NoAttentionMember => "Attention_Unsupported_NoAttentionMember",
        AttentionContract.UnsupportedReason.ChartType => "Attention_Unsupported_ChartType",
        _ => "Attention_Unsupported_NoSidecar",
    };
}
