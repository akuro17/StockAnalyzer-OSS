using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Immutable parameters for calculating Heikin-Ashi and Non-Time-Series chart series via <see cref="Services.Analysis.IChartSeriesCalculationService"/>.
/// </summary>
public sealed record ChartSeriesRequest(
    ChartType ChartType,
    ChartSizingMode SizingMode = ChartSizingMode.AutoAtr,
    decimal ManualSize = 1.0m,
    decimal Percentage = 1.0m,
    int AtrPeriod = ChartConstants.DefaultAtrPeriod,
    decimal AtrMultiplier = ChartConstants.DefaultAtrMultiplier,
    ChartRoundingMode RoundingMode = ChartRoundingMode.None,
    AutoFallbackMode FallbackMode = AutoFallbackMode.Percentage,
    int Reversal = 0,
    int InitialColumn = 0,
    int LineCount = ChartConstants.DefaultThreeLineBreakLineCount,
    decimal MinimumMove = 0m,
    decimal Tolerance = ChartConstants.ThreeLineBreakTrendThreshold,
    bool IncludeMultiWave = false,
    bool IncludePatterns = false,
    bool ShowDoubleBreakout = true,
    bool ShowTripleBreakout = true,
    bool ShowTrendlineBreakout = true,
    bool ShowTriangleBreakout = true,
    bool ShowCatapultBreakout = true);

/// <summary>
/// A single converted element (brick, column, segment, block, or bar) in a chart series.
/// </summary>
public sealed record ChartSeriesElement(
    int Index,
    DateTime Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string Direction,
    int? ColumnIndex = null,
    string? ColumnType = null,
    bool? IsYang = null,
    string? LineState = null,
    decimal? ColumnSpan = null,
    int? RunCount = null,
    int? BoxCount = null,
    decimal? ReversalPrice = null,
    decimal? NextTargetPrice = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    DateTime? ReversalDate = null,
    DateTime? UpdateDate = null,
    int? TrendDays = null);

/// <summary>
/// The complete calculated result of a chart series.
/// </summary>
public sealed record ChartSeriesCalculationResult(
    ChartType ChartType,
    int SourceBarCount,
    int ElementCount,
    decimal? EffectiveSize,
    int? TotalColumns,
    IReadOnlyList<ChartSeriesElement> Elements,
    IReadOnlyList<MultiWaveSignal>? MultiWaveSignals,
    PnfAnalysisResult? PnfAnalysis);
