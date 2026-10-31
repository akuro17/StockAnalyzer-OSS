using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>Detailed comparison evaluation for one leaf of a condition tree at a specific bar.</summary>
public sealed record LeafComparisonDetail(
    string Path,
    decimal? Left,
    decimal? Right,
    bool Result);

/// <summary>
/// Pure series evaluator for condition trees and individual nodes, computing bar-by-bar booleans and leaf comparisons
/// while reusing <see cref="BacktestConditionTreeEvaluator"/> and <see cref="BacktestConditionEvaluator"/> (SSoT).
/// </summary>
public static class ConditionSeriesEvaluator
{
    /// <summary>
    /// Builds the deduplicated <see cref="StrategyIndicatorRequest"/> list and the side-to-key mapping for any condition node,
    /// identical to the strategy's own deduplication rules.
    /// </summary>
    public static (IReadOnlyList<StrategyIndicatorRequest> Requests, IReadOnlyDictionary<BacktestConditionSide, string> Mapping)
        BuildIndicatorRequests(IBacktestConditionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return BuildIndicatorRequests(CollectActiveSides(node));
    }

    /// <summary>Collects all active sides from an arbitrary condition node.</summary>
    public static IReadOnlyList<BacktestConditionSide> CollectActiveSides(IBacktestConditionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sides = new List<BacktestConditionSide>();
        TraverseSides(node, sides);
        return sides;
    }

    private static void TraverseSides(IBacktestConditionNode node, List<BacktestConditionSide> sides)
    {
        switch (node)
        {
            case BacktestConditionLeaf leaf:
                sides.Add(leaf.Comparison.Left);
                if (leaf.Comparison.TargetMode == RightHandTargetMode.Indicator && leaf.Comparison.Right != null)
                {
                    sides.Add(leaf.Comparison.Right);
                }
                break;

            case BacktestConditionGroup group:
                foreach (IBacktestConditionNode child in group.Children)
                {
                    TraverseSides(child, sides);
                }
                break;
        }
    }

    /// <summary>Builds deduplicated indicator requests from an active side sequence.</summary>
    public static (IReadOnlyList<StrategyIndicatorRequest> Requests, IReadOnlyDictionary<BacktestConditionSide, string> Mapping)
        BuildIndicatorRequests(IEnumerable<BacktestConditionSide> activeSides)
    {
        var dedup = new Dictionary<(IndicatorType Type, string ParametersKey, string OutputName, TimeFrame? Frame, PriceType? PriceSource), string>();
        var mapping = new Dictionary<BacktestConditionSide, string>();
        var requests = new List<StrategyIndicatorRequest>();

        foreach (BacktestConditionSide side in activeSides)
        {
            RegisterSide(side, dedup, mapping, requests);
        }

        return (requests, mapping);
    }

    /// <summary>
    /// Prepares all indicator series for the given requests across primary and foreign timeframe bars,
    /// enforcing causality guards and foreign timeframe alignment.
    /// </summary>
    public static IndicatorSeriesSet PrepareIndicators(
        IIndicatorFactory indicatorFactory,
        IReadOnlyList<CandleData> primaryBars,
        TimeFrame primaryFrame,
        IReadOnlyList<StrategyIndicatorRequest> requests,
        IReadOnlyDictionary<TimeFrame, IReadOnlyList<CandleData>>? additionalTimeframeBars = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indicatorFactory);
        ArgumentNullException.ThrowIfNull(primaryBars);
        ArgumentNullException.ThrowIfNull(requests);

        var series = new Dictionary<string, ImmutableArray<decimal?>>(requests.Count);
        if (requests.Count == 0) return new IndicatorSeriesSet(series);

        var coreCandles = new CoreCandleData[primaryBars.Count];
        for (int i = 0; i < coreCandles.Length; i++)
        {
            CandleData bar = primaryBars[i];
            coreCandles[i] = new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        }

        ImmutableArray<CandleData> baseBarsImmutable = primaryBars as ImmutableArray<CandleData>?
            ?? primaryBars.ToImmutableArray();

        var foreignCoreCandlesByFrame = new Dictionary<TimeFrame, CoreCandleData[]>();

        foreach (StrategyIndicatorRequest request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BacktestIndicatorViolationReason violation = BacktestIndicatorEligibility.Check(request.Type, request.OutputName);
            if (violation != BacktestIndicatorViolationReason.None)
            {
                throw new InvalidOperationException(
                    $"StrategyIndicatorRequest '{request.Key}': {BacktestIndicatorEligibility.Describe(request.Type, request.OutputName, violation)}");
            }

            ICoreIndicator indicator = indicatorFactory.Create(request.Type, request.Parameters)
                ?? throw new InvalidOperationException($"Indicator type '{request.Type}' is not registered in IIndicatorFactory.");

            if (request.PriceSource is { } priceSource && indicator is CoreIndicatorBase baseIndicator)
            {
                baseIndicator.PriceSource = priceSource;
            }

            if (request.Frame is null || request.Frame == primaryFrame)
            {
                IIndicatorResult result = indicator.Calculate(coreCandles);
                EnsureCalculationSucceeded(request, result);
                series[request.Key] = ExtractRequestedSeries(result, request.OutputName, request.StrictOutputName).ToImmutableArray();
                continue;
            }

            TimeFrame foreignFrame = request.Frame.Value;
            if (additionalTimeframeBars is null ||
                !additionalTimeframeBars.TryGetValue(foreignFrame, out IReadOnlyList<CandleData>? foreignBarsList))
            {
                throw new InvalidOperationException(
                    $"StrategyIndicatorRequest '{request.Key}' requires Frame '{foreignFrame}', but additional bars were not supplied.");
            }

            ImmutableArray<CandleData> foreignBarsImmutable = foreignBarsList as ImmutableArray<CandleData>?
                ?? foreignBarsList.ToImmutableArray();

            if (!foreignCoreCandlesByFrame.TryGetValue(foreignFrame, out CoreCandleData[]? foreignCoreCandles))
            {
                foreignCoreCandles = new CoreCandleData[foreignBarsImmutable.Length];
                for (int i = 0; i < foreignCoreCandles.Length; i++)
                {
                    CandleData bar = foreignBarsImmutable[i];
                    foreignCoreCandles[i] = new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
                }
                foreignCoreCandlesByFrame[foreignFrame] = foreignCoreCandles;
            }

            IIndicatorResult foreignResult = indicator.Calculate(foreignCoreCandles);
            EnsureCalculationSucceeded(request, foreignResult);
            ImmutableArray<decimal?> foreignValues = ExtractRequestedSeries(foreignResult, request.OutputName, request.StrictOutputName)
                .Take(foreignBarsImmutable.Length)
                .ToImmutableArray();
            series[request.Key] = TimeframeAlignment.Align(baseBarsImmutable, foreignBarsImmutable, foreignValues);
        }

        return new IndicatorSeriesSet(series);
    }

    /// <summary>Evaluates the condition node across a bar index range, returning the boolean series.</summary>
    public static bool[] EvaluateSeries(
        IBacktestConditionNode node,
        IReadOnlyDictionary<BacktestConditionSide, string> mapping,
        IndicatorSeriesSet indicators,
        int startIndex,
        int count)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(indicators);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var results = new bool[count];
        for (int i = 0; i < count; i++)
        {
            results[i] = BacktestConditionTreeEvaluator.Evaluate(node, mapping, indicators, startIndex + i);
        }

        return results;
    }

    /// <summary>Extracts the leaf comparison breakdown for each leaf in the condition node at barIndex.</summary>
    public static IReadOnlyList<LeafComparisonDetail> EvaluateLeaves(
        IBacktestConditionNode node,
        IReadOnlyDictionary<BacktestConditionSide, string> mapping,
        IndicatorSeriesSet indicators,
        int barIndex)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(indicators);

        var list = new List<LeafComparisonDetail>();
        TraverseLeaves(node, string.Empty, mapping, indicators, barIndex, list);
        return list;
    }

    private static void TraverseLeaves(
        IBacktestConditionNode node,
        string path,
        IReadOnlyDictionary<BacktestConditionSide, string> mapping,
        IndicatorSeriesSet indicators,
        int barIndex,
        List<LeafComparisonDetail> list)
    {
        switch (node)
        {
            case BacktestConditionLeaf leaf:
                string currentPath = string.IsNullOrEmpty(path) ? "children[0]" : path;
                decimal? left = ReadSideValue(leaf.Comparison.Left, mapping, indicators, barIndex);
                decimal? right = leaf.Comparison.TargetMode == RightHandTargetMode.NumericValue
                    ? leaf.Comparison.RightNumericValue
                    : (leaf.Comparison.Right != null ? ReadSideValue(leaf.Comparison.Right, mapping, indicators, barIndex) : null);
                bool result = BacktestConditionEvaluator.EvaluateComparison(leaf.Comparison, mapping, indicators, barIndex);
                list.Add(new LeafComparisonDetail(currentPath, left, right, result));
                break;

            case BacktestConditionGroup group:
                for (int i = 0; i < group.Children.Length; i++)
                {
                    string childPath = string.IsNullOrEmpty(path) ? $"children[{i}]" : $"{path}.children[{i}]";
                    TraverseLeaves(group.Children[i], childPath, mapping, indicators, barIndex, list);
                }
                break;

            default:
                throw new ArgumentException($"Unsupported node type: {node.GetType().Name}", nameof(node));
        }
    }

    private static decimal? ReadSideValue(
        BacktestConditionSide side,
        IReadOnlyDictionary<BacktestConditionSide, string> mapping,
        IndicatorSeriesSet indicators,
        int barIndex)
    {
        if (!mapping.TryGetValue(side, out string? key))
        {
            throw new KeyNotFoundException("BacktestConditionSide was not mapped to an indicator key.");
        }
        return indicators.ValueAt(key, barIndex - side.Offset);
    }

    private static void RegisterSide(
        BacktestConditionSide side,
        Dictionary<(IndicatorType Type, string ParametersKey, string OutputName, TimeFrame? Frame, PriceType? PriceSource), string> dedup,
        Dictionary<BacktestConditionSide, string> mapping,
        List<StrategyIndicatorRequest> requests)
    {
        (IndicatorType, string, string, TimeFrame?, PriceType?) dedupKey = (side.IndicatorType, ParametersContentKey(side.Parameters), side.OutputName, side.Frame, side.PriceSource);
        if (!dedup.TryGetValue(dedupKey, out string? key))
        {
            key = $"cond{dedup.Count}";
            dedup[dedupKey] = key;
            requests.Add(new StrategyIndicatorRequest(
                key,
                side.IndicatorType,
                side.Parameters?.Clone(),
                side.Frame,
                BacktestConditionValidator.NormalizeOutputName(side),
                side.PriceSource,
                StrictOutputName: true));
        }
        mapping[side] = key;
    }

    private static string ParametersContentKey(CoreIndicatorParameterBase? parameters)
        => parameters is null ? string.Empty : JsonSerializer.Serialize(parameters, parameters.GetType());

    private static void EnsureCalculationSucceeded(StrategyIndicatorRequest request, IIndicatorResult result)
    {
        if (!result.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"StrategyIndicatorRequest '{request.Key}': indicator '{request.Type}' calculation failed: {result.ErrorMessage}");
        }
    }

    private static IReadOnlyList<decimal?> ExtractRequestedSeries(IIndicatorResult result, string outputName, bool strictOutputName)
    {
        if (!strictOutputName)
        {
            return (!string.IsNullOrWhiteSpace(outputName) && result.HasSeries(outputName))
                ? result.GetSeries(outputName)
                : result.MainValues;
        }

        string normalized = string.IsNullOrWhiteSpace(outputName) ? IndicatorResult.MainSeriesName : outputName;
        if (string.Equals(normalized, IndicatorResult.MainSeriesName, StringComparison.Ordinal))
        {
            return result.MainValues;
        }

        string? exactName = result.SeriesNamesList.FirstOrDefault(name => string.Equals(name, normalized, StringComparison.Ordinal));
        if (exactName is null)
        {
            throw new InvalidOperationException($"Indicator result does not contain the requested series '{normalized}'.");
        }

        return result.GetSeries(exactName);
    }
}
