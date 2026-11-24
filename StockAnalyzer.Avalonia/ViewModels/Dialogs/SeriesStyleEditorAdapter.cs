using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Rendering;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Editable row adapter for a multi-series entry in Color Settings dialogs.
/// Proxies <see cref="SeriesColorConfig"/> visibility, render primitive override, and color while
/// displaying the resolved legacy default when <see cref="SeriesColorConfig.RenderPrimitive"/> is null.
/// </summary>
public sealed partial class ActiveSeriesStyleRow : ObservableObject
{
    private readonly CoreIndicatorSettings _owner;
    private readonly SeriesColorConfig _config;
    private readonly string _primaryTargetSeries;

    public ActiveSeriesStyleRow(CoreIndicatorSettings owner, SeriesColorConfig config)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _primaryTargetSeries = config.TargetSeries is { Count: > 0 }
            ? config.TargetSeries[0]
            : config.Name;
    }

    public SeriesColorConfig Config => _config;
    public string DisplayName => _config.DisplayName;
    public IReadOnlyList<SeriesPrimitiveType> SelectableRenderPrimitives => SeriesStyleEditorAdapter.GetSortedSelectablePrimitives();
    public bool ShowStyleControls => SeriesRenderStyleCatalog.SupportsRenderStyleSelection(_owner.TypeEnum);

    public IndicatorColor Color
    {
        get => _config.Color;
        set
        {
            if (_config.Color.Equals(value)) return;
            _config.Color = value;
            OnPropertyChanged();
        }
    }

    public bool IsVisible
    {
        get => _config.IsVisible;
        set
        {
            if (_config.IsVisible == value) return;
            _config.IsVisible = value;
            OnPropertyChanged();
        }
    }

    public SeriesPrimitiveType EffectivePrimitive
    {
        get
        {
            if (_config.RenderPrimitive is { } explicitPrimitive &&
                SeriesRenderStyleCatalog.IsSelectablePrimitive(explicitPrimitive))
            {
                return explicitPrimitive;
            }

            return SeriesRenderStyleCatalog.ResolveDefaultPrimitive(_owner, _primaryTargetSeries);
        }
        set
        {
            var currentEffective = EffectivePrimitive;
            var normalized = SeriesRenderStyleCatalog.NormalizePrimitiveOverride(_owner, _primaryTargetSeries, value);
            if (_config.RenderPrimitive == normalized ||
                (!_config.RenderPrimitive.HasValue && currentEffective == value))
            {
                return;
            }

            _config.RenderPrimitive = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Refreshes <see cref="EffectivePrimitive"/> bindings when the owning indicator's default line style changes.
    /// </summary>
    internal void NotifyOwnerStyleChanged() => OnPropertyChanged(nameof(EffectivePrimitive));
}

/// <summary>
/// Shared adapter managing single-series visibility/render-style proxies and <see cref="ActiveSeriesStyleRow"/>
/// collections across both <see cref="IndicatorSettingsDialogViewModel"/> and <see cref="IndicatorPropertiesViewModel"/>.
/// Subscribes to the attached <see cref="CoreIndicatorSettings.Style"/> change so unpinned default primitives
/// stay synchronized when the base line style changes.
/// </summary>
internal sealed class SeriesStyleEditorAdapter : IDisposable
{
    private CoreIndicatorSettings? _indicator;
    private Action? _onDefaultStyleChanged;
    private bool _isRefreshing;
    private bool _disposed;

    private static readonly object SortedCacheLock = new();
    private static string? _cachedLanguage;
    private static IReadOnlyList<SeriesPrimitiveType>? _cachedSortedPrimitives;

    public IReadOnlyList<SeriesPrimitiveType> SelectableRenderPrimitives => GetSortedSelectablePrimitives();

    internal static IReadOnlyList<SeriesPrimitiveType> GetSortedSelectablePrimitives()
    {
        string lang = LocalizationManager.Instance.CurrentLanguage ?? string.Empty;
        lock (SortedCacheLock)
        {
            if (_cachedSortedPrimitives != null && string.Equals(_cachedLanguage, lang, StringComparison.Ordinal))
            {
                return _cachedSortedPrimitives;
            }

            var sorted = SeriesRenderStyleCatalog.SelectablePrimitives
                .OrderBy(v => EnumToLocalizedDisplayNameConverter.ResolveDisplayText(v), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(v => (int)v)
                .ToArray();
            _cachedLanguage = lang;
            _cachedSortedPrimitives = sorted;
            return sorted;
        }
    }
    public ObservableCollection<ActiveSeriesStyleRow> SeriesStyleRows { get; } = new();

    public bool ShowStyleControls =>
        _indicator != null &&
        SeriesRenderStyleCatalog.SupportsRenderStyleSelection(_indicator.TypeEnum);

    public bool MainSeriesVisible
    {
        get => _indicator?.IsMainSeriesVisible ?? true;
        set
        {
            if (_isRefreshing || _indicator == null) return;
            if (_indicator.IsMainSeriesVisible == value) return;
            _indicator.IsMainSeriesVisible = value;
        }
    }

    public SeriesPrimitiveType MainRenderPrimitive
    {
        get => _indicator == null
            ? SeriesPrimitiveType.Line
            : SeriesRenderStyleCatalog.ResolveEffectivePrimitive(_indicator, IndicatorResult.MainSeriesName);
        set
        {
            if (_isRefreshing || _indicator == null) return;
            var currentEffective = SeriesRenderStyleCatalog.ResolveEffectivePrimitive(_indicator, IndicatorResult.MainSeriesName);
            var normalized = SeriesRenderStyleCatalog.NormalizePrimitiveOverride(_indicator, IndicatorResult.MainSeriesName, value);
            if (_indicator.MainRenderPrimitive == normalized ||
                (!_indicator.MainRenderPrimitive.HasValue && currentEffective == value))
            {
                return;
            }

            _indicator.MainRenderPrimitive = normalized;
        }
    }

    public void Attach(CoreIndicatorSettings? indicator, Action? onDefaultStyleChanged = null)
    {
        if (_disposed) return;

        if (_indicator != null)
        {
            _indicator.PropertyChanged -= OnIndicatorPropertyChanged;
        }

        _onDefaultStyleChanged = onDefaultStyleChanged ?? _onDefaultStyleChanged;
        _indicator = indicator;

        if (_indicator != null)
        {
            _indicator.PropertyChanged += OnIndicatorPropertyChanged;
        }

        _isRefreshing = true;
        try
        {
            SeriesStyleRows.Clear();
            if (_indicator?.SeriesColors != null)
            {
                foreach (var seriesColor in _indicator.SeriesColors)
                {
                    SeriesStyleRows.Add(new ActiveSeriesStyleRow(_indicator, seriesColor));
                }
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnIndicatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CoreIndicatorSettings.Style))
        {
            return;
        }

        for (int i = 0; i < SeriesStyleRows.Count; i++)
        {
            SeriesStyleRows[i].NotifyOwnerStyleChanged();
        }

        _onDefaultStyleChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_indicator != null)
        {
            _indicator.PropertyChanged -= OnIndicatorPropertyChanged;
            _indicator = null;
        }

        _onDefaultStyleChanged = null;
    }
}
