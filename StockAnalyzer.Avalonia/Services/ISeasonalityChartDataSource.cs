using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>DI boundary between the active chart symbol and the seasonality polar chart view.</summary>
public interface ISeasonalityChartDataSource
{
    /// <summary>The most recent completed analysis, or null when none has run for the active symbol.</summary>
    SeasonalityChartResult? Current { get; }

    /// <summary>Symbol last published by the active chart, or null before the first chart load.</summary>
    string? ActiveSymbol { get; }

    /// <summary>True when <see cref="ActiveSymbol"/> is a non-empty symbol.</summary>
    bool HasActiveSymbol { get; }

    /// <summary>Raised after <see cref="ActiveSymbol"/> changes or a new <see cref="Current"/> is published.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Called by the chart boundary when the displayed symbol changes. Clears any stale result so the
    /// view does not show another symbol's seasonality while a fresh analysis is pending.
    /// </summary>
    void SetActiveSymbol(string? symbol);

    /// <summary>
    /// Per-series radius-mode overrides chosen in the Seasonality Clock UI, keyed by
    /// <see cref="SeasonalityYearTrace.SeriesId"/>. The seasonality chart draws only the base
    /// (active) symbol, so the map holds at most one entry for series 0; it replaces the
    /// <see cref="SeasonalityRadiusMode.PercentVsYearStart"/> default on the next analysis. Empty
    /// until the view pushes a map.
    /// </summary>
    IReadOnlyDictionary<int, SeasonalityRadiusMode> RadiusModeOverrides { get; }

    /// <summary>
    /// Called by the Seasonality Clock view when the user changes the base symbol's radius mode.
    /// Stores a copy of the map so the next <see cref="AnalyzeAsync"/> applies it. An override of
    /// <see cref="SeasonalityRadiusMode.SignedUnitFromBounds"/> is ignored for the price series (it
    /// carries no fixed numeric scale), so the engine's bounds precondition can never be violated.
    /// Does not itself re-run analysis or raise <see cref="Changed"/> — the caller triggers the
    /// re-analysis.
    /// </summary>
    void SetRadiusModeOverrides(IReadOnlyDictionary<int, SeasonalityRadiusMode>? overridesBySeriesId);

    /// <summary>
    /// Fetches daily candles for <see cref="ActiveSymbol"/> (enough for
    /// <c>parameters.YearsToOverlay</c> calendar years) and runs <see cref="SeasonalityChartEngine"/>
    /// on the base symbol alone. Updates <see cref="Current"/> and raises <see cref="Changed"/> on
    /// completion.
    /// </summary>
    Task AnalyzeAsync(SeasonalityChartParameters parameters, CancellationToken cancellationToken = default);
}
