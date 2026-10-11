using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>The three model inputs of one graph (batch 1) and how many nodes are valid.</summary>
internal sealed record GraphInput(float[] NodeFeatures, float[] Adjacency, float[] NodeMask, int ValidNodes);

/// <summary>
/// Pure construction of the graph model inputs from per-symbol <see cref="CandleData"/> (decimal) at one as-of date; Python mirrors it
/// (<c>train_gnn.py</c>: <c>GraphBuilder.sample</c>) and <c>Tests/GraphParity.json</c> keeps both sides identical. It is deliberately independent of
/// <c>PredictionService</c>: only the feature processor (<see cref="IMLDataProcessor"/>) and the existing Pearson definition
/// (<see cref="IndicatorCalculationHelper.CalculateRollingPearsonCorrelation"/>) are reused.
/// </summary>
/// <remarks>
/// Definitions (S19/S20): a node's one-bar log return is built from its OWN two consecutive rows and dated by the later row; a calendar date the node does not
/// have is a missing value (nothing is carried forward); ONE missing value in the correlation window, or a constant series, means no edge; an edge exists when
/// <c>|rho| &gt;= edge_threshold</c> and weighs <c>|rho|</c>. Only rows dated at or before the as-of date are read.
/// </remarks>
internal static class GraphInputBuilder
{
    /// <summary>The first problem of a supplied history (strictly increasing dates are required), or <see langword="null"/>.</summary>
    internal static string? ValidateHistory(IReadOnlyList<CandleData> candles)
    {
        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].Timestamp.Date <= candles[i - 1].Timestamp.Date)
                return "candle dates must be strictly increasing (one bar per calendar date)";
        }

        return null;
    }

    /// <summary>The newest calendar date over all histories, or <see langword="null"/> when none has a candle.</summary>
    internal static DateTime? LatestDate(IEnumerable<IReadOnlyList<CandleData>> histories)
    {
        DateTime? latest = null;
        foreach (var history in histories)
        {
            if (history.Count == 0) continue;
            var last = history[history.Count - 1].Timestamp.Date;
            if (latest is null || last > latest) latest = last;
        }

        return latest;
    }

    /// <summary>Number of rows dated at or before <paramref name="date"/> (the histories are strictly increasing).</summary>
    internal static int CountUpTo(IReadOnlyList<CandleData> candles, DateTime date)
    {
        int low = 0, high = candles.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (candles[mid].Timestamp.Date <= date) low = mid + 1; else high = mid;
        }

        return low;
    }

    /// <summary>
    /// Builds the inputs for <paramref name="asOf"/>. <paramref name="histories"/> has one entry per node of <paramref name="info"/> (an empty list for a node
    /// without data). Returns <see langword="null"/> and a reason when no node has a candle on the as-of date.
    /// </summary>
    internal static GraphInput? Build(GraphModelInfo info, IReadOnlyList<IReadOnlyList<CandleData>> histories, DateTime asOf, IMLDataProcessor processor,
        out string? failureReason)
    {
        failureReason = null;
        int n = info.NodeOrder.Length, window = info.Window, channels = info.Channels, corr = info.CorrWindow;
        if (histories.Count != n) throw new ArgumentException("One history per node is required.", nameof(histories));

        var counts = new int[n];
        var asOfRow = new int[n];
        bool anyOnDate = false;
        for (int i = 0; i < n; i++)
        {
            counts[i] = CountUpTo(histories[i], asOf);
            asOfRow[i] = counts[i] > 0 && histories[i][counts[i] - 1].Timestamp.Date == asOf ? counts[i] - 1 : -1;
            anyOnDate |= asOfRow[i] >= 0;
        }

        if (!anyOnDate)
        {
            failureReason = GraphUnavailableReason.AsOfNotInData;
            return null;
        }

        var features = new float[checked(n * window * channels)];
        var mask = new float[n];
        int valid = 0;
        for (int i = 0; i < n; i++)
        {
            int row = asOfRow[i];
            if (row < window - 1 || histories[i][row].Close <= 0m) continue;
            int start = row - window + 1;
            var destination = features.AsSpan(i * window * channels, window * channels);
            switch (info.FeatureMode)
            {
                case PredictionFeatureMode.OhlcvMinMax: processor.NormalizeCandles(histories[i], start, window, destination); break;
                case PredictionFeatureMode.LogReturn: processor.ComputeLogReturns(histories[i], start, window, destination); break;
                case PredictionFeatureMode.LogReturnOhlc: processor.ComputeLogReturnsOhlc(histories[i], start, window, destination); break;
                case PredictionFeatureMode.ZScoreStandardized: processor.NormalizeZScoreOhlcv(histories[i], start, window, destination); break;
                case PredictionFeatureMode.ZScoreOhlcvJoint: processor.ComputeJointZScoreOhlcv(histories[i], start, window, destination); break;
                default: throw new InvalidOperationException($"Feature mode '{info.FeatureMode}' is not a per-window mode of the graph model.");
            }

            mask[i] = 1f;
            valid++;
        }

        var adjacency = new float[checked(n * n)];
        var dates = CalendarTail(histories, counts, corr);
        if (dates is not null && valid >= 2)
        {
            var returns = new decimal?[n][];
            for (int i = 0; i < n; i++)
            {
                if (mask[i] != 0f) returns[i] = ReturnColumn(histories[i], counts[i], dates);
            }

            for (int i = 0; i < n; i++)
            {
                if (returns[i] is null) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (returns[j] is null) continue;
                    var rho = IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(returns[i], returns[j], corr)[corr - 1];
                    if (rho is null) continue;
                    double weight = Math.Abs((double)rho.Value);
                    if (weight < info.EdgeThreshold) continue;
                    adjacency[i * n + j] = (float)weight;
                    adjacency[j * n + i] = (float)weight;
                }
            }
        }

        return new GraphInput(features, adjacency, mask, valid);
    }

    /// <summary>
    /// The last <paramref name="corr"/> calendar dates (union of the node dates up to the as-of date), or <see langword="null"/> when the calendar is shorter than the
    /// window (no edge can exist). Only each node's own last <paramref name="corr"/> dates can belong to that tail, so no node's full history is merged.
    /// </summary>
    private static DateTime[]? CalendarTail(IReadOnlyList<IReadOnlyList<CandleData>> histories, int[] counts, int corr)
    {
        var union = new SortedSet<DateTime>();
        for (int i = 0; i < histories.Count; i++)
        {
            for (int k = Math.Max(0, counts[i] - corr); k < counts[i]; k++) union.Add(histories[i][k].Timestamp.Date);
        }

        if (union.Count < corr) return null;
        var all = new DateTime[union.Count];
        union.CopyTo(all);
        return all.AsSpan(all.Length - corr).ToArray();
    }

    /// <summary>One node's log-return column over the calendar tail (null = missing).</summary>
    private static decimal?[] ReturnColumn(IReadOnlyList<CandleData> candles, int count, DateTime[] dates)
    {
        var column = new decimal?[dates.Length];
        int cursor = 0;
        for (int k = 0; k < dates.Length; k++)
        {
            while (cursor < count && candles[cursor].Timestamp.Date < dates[k]) cursor++;
            if (cursor >= count || candles[cursor].Timestamp.Date != dates[k] || cursor < 1) continue;
            decimal previous = candles[cursor - 1].Close, current = candles[cursor].Close;
            if (previous <= 0m || current <= 0m) continue;
            column[k] = (decimal)Math.Log((double)current / (double)previous);
        }

        return column;
    }
}
