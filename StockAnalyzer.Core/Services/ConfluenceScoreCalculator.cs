using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Confluence;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Provides pure mathematical functions to calculate confluence scores from a collection of signals.
/// Implements "Formula #1: Weighted Aggregation with Decorrelation".
/// </summary>
public static class ConfluenceScoreCalculator
{
    private const double Epsilon = 1e-10;

    /// <summary>
    /// Calculates the confluence score (0-100) for a given set of signals.
    /// This overload is provided for backward compatibility.
    /// </summary>
    public static ConfluenceResult Calculate(int index, IReadOnlyList<ConfluenceSignal> signals)
    {
        return Calculate(index, signals, new List<ConfluenceSignal>());
    }

    /// <summary>
    /// Calculates the confluence score (0-100) for a given set of signals using a provided workspace list.
    /// Reusing the workspace list enables ZeroAllocation processing during rendering loops.
    /// </summary>
    public static ConfluenceResult Calculate(int index, IReadOnlyList<ConfluenceSignal> signals, IList<ConfluenceSignal> workspace)
    {
        if (signals == null || signals.Count == 0)
        {
            return new ConfluenceResult(index, 50, SignalDirection.Neutral, 0, "No signals provided");
        }

        // 1. Deduplication (Same Type, Same Direction, Proximity ±1 bar)
        Deduplicate(signals, workspace);

        if (workspace.Count == 0)
        {
            return new ConfluenceResult(index, 50, SignalDirection.Neutral, 0, "No unique signals remain after deduplication");
        }

        // 2. Group Correction & Weight Aggregation
        double sBullish = 0;
        double sBearish = 0;

        // Group counts for correction factor (Decorrelation)
        // MaxGroups (32) is a safe upper bound for DecorrelationGroup enum members (currently 6).
        const int MaxGroups = 32;
        Span<int> groupCounts = stackalloc int[MaxGroups]; 
        groupCounts.Clear();

        for (int i = 0; i < workspace.Count; i++)
        {
            var s = workspace[i];
            int groupIdx = (int)s.Group;
            if (groupIdx >= 0 && groupIdx < groupCounts.Length)
            {
                groupCounts[groupIdx]++;
            }
        }

        for (int i = 0; i < workspace.Count; i++)
        {
            var s = workspace[i];
            double groupFactor = 1.0;
            int groupIdx = (int)s.Group;
            
            if (s.Group != DecorrelationGroup.None && groupIdx < groupCounts.Length)
            {
                int count = groupCounts[groupIdx];
                if (count > 1)
                {
                    // multi-collinearity reduction: signals in same group have their influence divided
                    groupFactor = 1.0 / count;
                }
            }

            // Effective weight combines the specific indicator's weight, the signal strength, and the group correction
            double effectiveWeight = s.Weight * s.Strength * groupFactor;

            if (s.Direction == SignalDirection.Bullish)
            {
                sBullish += effectiveWeight;
            }
            else if (s.Direction == SignalDirection.Bearish)
            {
                sBearish += effectiveWeight;
            }
        }

        // 3. Raw Score (-1.0 to 1.0)
        double combinedWeight = sBullish + sBearish;
        if (combinedWeight < Epsilon)
        {
            return new ConfluenceResult(index, 50, SignalDirection.Neutral, workspace.Count, "Insufficient signal weight");
        }

        // raw score is the net pressure normalized by total pressure
        double rawScore = (sBullish - sBearish) / combinedWeight;

        // 4. Normalize (0 to 100)
        // -1.0 -> 0% (Extremely Bearish)
        // 0.0 -> 50% (Neutral)
        // 1.0 -> 100% (Extremely Bullish)
        int finalScore = (int)Math.Round((rawScore + 1.0) / 2.0 * 100.0);

        // Clamping just in case of floating point precision issues
        finalScore = Math.Clamp(finalScore, 0, 100);

        // Direction thresholds: > 55% Bullish, < 45% Bearish. 
        // A ±5% buffer is used around the 50% midpoint to prevent flickering in neutral zones.
        SignalDirection finalDirection = SignalDirection.Neutral;
        if (finalScore > 55) finalDirection = SignalDirection.Bullish;
        else if (finalScore < 45) finalDirection = SignalDirection.Bearish;

        return new ConfluenceResult(index, finalScore, finalDirection, workspace.Count);
    }

    /// <summary>
    /// Removes duplicate signals based on IndicatorType, Direction, and Proximity (±1 bar).
    /// Keeps the signal with the highest strength when duplicates are found.
    /// Reuses the provided result list to avoid heap allocations.
    /// </summary>
    private static void Deduplicate(IReadOnlyList<ConfluenceSignal> signals, IList<ConfluenceSignal> result)
    {
        result.Clear();
        if (signals.Count == 0) return;
        
        if (signals.Count == 1)
        {
            result.Add(signals[0]);
            return;
        }

        // Manual deduplication to be explicit and control allocations.
        // Using stackalloc for the tracking array to avoid heap allocation for typical signal counts (< 256).
        Span<bool> processed = signals.Count < 256 ? stackalloc bool[signals.Count] : new bool[signals.Count];

        for (int i = 0; i < signals.Count; i++)
        {
            if (processed[i]) continue;

            ConfluenceSignal best = signals[i];
            
            // Look ahead for duplicates
            for (int j = i + 1; j < signals.Count; j++)
            {
                if (processed[j]) continue;

                if (signals[i].IndicatorType == signals[j].IndicatorType &&
                    signals[i].Direction == signals[j].Direction &&
                    Math.Abs(signals[i].Index - signals[j].Index) <= 1)
                {
                    // Overlap found
                    processed[j] = true;
                    if (signals[j].Strength > best.Strength)
                    {
                        best = signals[j];
                    }
                }
            }

            result.Add(best);
            processed[i] = true;
        }
    }
}
