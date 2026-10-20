using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.MarketStructure;
using StockAnalyzer.Core.Models.Validation;

namespace StockAnalyzer.Core.Models.GeometricPattern;

/// <summary>
/// Detects macro-level geometric chart formations (Channels, Flags, Pennants, Triangles)
/// using ZigZag-based pivot extraction and linear regression trendline fitting.
/// This is separate from the ML-based PatternRecognitionService and the micro-level CandlePatternDetector.
/// </summary>
public static class GeometricPatternDetector
{
    /// <summary>
    /// Detects geometric formations in the given candle data.
    /// </summary>
    /// <param name="candles">The candle data to analyze.</param>
    /// <param name="zigzagThreshold">ZigZag threshold percentage for pivot extraction (default: 5%).</param>
    /// <returns>A list of detected formations, ordered by start index.</returns>
    public static IReadOnlyList<DetectedFormation> Detect(
        IReadOnlyList<CandleData> candles,
        decimal zigzagThreshold,
        bool useRobustFit = false,
        bool promoteFlagsAndPennants = false)
    {
        return Detect(candles, new[] { zigzagThreshold }, useRobustFit, promoteFlagsAndPennants);
    }

    public static IReadOnlyList<DetectedFormation> Detect(
        IReadOnlyList<CandleData> candles,
        decimal[]? zigzagThresholds = null,
        bool useRobustFit = false,
        bool promoteFlagsAndPennants = false)
    {
        if (candles == null || candles.Count < ChartConstants.GeometricMinPivotCount * 2)
        {
            return Array.Empty<DetectedFormation>();
        }

        var thresholds = zigzagThresholds ?? ChartConstants.GeometricMultiScaleThresholds;
        if (thresholds.Length == 0) thresholds = ChartConstants.GeometricMultiScaleThresholds;

        var allFormations = new List<DetectedFormation>();

        foreach (var threshold in thresholds)
        {
            var pivots = MarketStructureDetector.ExtractPivots(candles, threshold);

            if (pivots.Count < ChartConstants.GeometricMinPivotCount)
            {
                continue;
            }

            int currentIndex = 0;
            while (currentIndex <= pivots.Count - ChartConstants.GeometricMinPivotCount)
            {
                DetectedFormation? bestFormation = null;
                int bestEndIndex = -1;

                int maxLen = pivots.Count - currentIndex;
                var highs = new List<PivotPoint>(maxLen);
                var lows = new List<PivotPoint>(maxLen);
                var highsStats = new RegressionStats();
                var lowsStats = new RegressionStats();

                for (int i = currentIndex; i < currentIndex + maxLen; i++)
                {
                    var p = pivots[i];
                    if (p.IsHigh)
                    {
                        highs.Add(p);
                        highsStats.Add(p.Index, (double)p.Price);
                    }
                    else
                    {
                        lows.Add(p);
                        lowsStats.Add(p.Index, (double)p.Price);
                    }
                }

                (double Slope, double Intercept, double RSquared)? cachedUpperFit = null;
                (double Slope, double Intercept, double RSquared)? cachedLowerFit = null;

                for (int length = maxLen; length >= ChartConstants.GeometricMinPivotCount; length--)
                {
                    if (highs.Count >= 2 && lows.Count >= 2)
                    {
                        if (useRobustFit)
                        {
                            var formation = AnalyzeFormation(highs, lows, candles, useRobustFit, promoteFlagsAndPennants);
                            if (formation != null && formation.ConfidenceScore >= ChartConstants.GeometricMinRSquared)
                            {
                                bestFormation = formation;
                                bestEndIndex = currentIndex + length - 1;
                                break;
                            }
                        }
                        else
                        {
                            double olsUpperRSquared = highsStats.RSquared;
                            double olsLowerRSquared = lowsStats.RSquared;

                            double effectiveLowerR2 = cachedLowerFit?.RSquared ?? olsLowerRSquared;

                            // Mathematical bound (Gauss-Markov): Theil-Sen R^2 <= OLS R^2 always.
                            // Prune if (upperBound_H + knownOrBound_L) / 2 < threshold.
                            if ((olsUpperRSquared + effectiveLowerR2) / 2.0 >= ChartConstants.GeometricMinRSquared - 1e-4)
                            {
                                cachedUpperFit ??= CalculateTheilSen(highs);

                                // Now upper fit is exact. Check if lower fit can possibly reach threshold.
                                if ((cachedUpperFit.Value.RSquared + olsLowerRSquared) / 2.0 >= ChartConstants.GeometricMinRSquared - 1e-4)
                                {
                                    cachedLowerFit ??= CalculateTheilSen(lows);

                                    double avgRSquared = (cachedUpperFit.Value.RSquared + cachedLowerFit.Value.RSquared) / 2.0;
                                    if (avgRSquared >= ChartConstants.GeometricMinRSquared)
                                    {
                                        var formation = AnalyzeFormation(
                                            highs, lows, candles, useRobustFit: false, promoteFlagsAndPennants,
                                            cachedUpperFit, cachedLowerFit);

                                        if (formation != null && formation.ConfidenceScore >= ChartConstants.GeometricMinRSquared)
                                        {
                                            bestFormation = formation;
                                            bestEndIndex = currentIndex + length - 1;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (length > ChartConstants.GeometricMinPivotCount)
                    {
                        var removed = pivots[currentIndex + length - 1];
                        if (removed.IsHigh)
                        {
                            highs.RemoveAt(highs.Count - 1);
                            highsStats.Remove(removed.Index, (double)removed.Price);
                            cachedUpperFit = null;
                        }
                        else
                        {
                            lows.RemoveAt(lows.Count - 1);
                            lowsStats.Remove(removed.Index, (double)removed.Price);
                            cachedLowerFit = null;
                        }
                    }
                }

                if (bestFormation != null)
                {
                    allFormations.Add(bestFormation);
                    
                    int nextPivotIdx = bestEndIndex;
                    while (nextPivotIdx < pivots.Count && pivots[nextPivotIdx].Index < bestFormation.EndIndex)
                    {
                        nextPivotIdx++;
                    }
                    currentIndex = Math.Max(bestEndIndex, nextPivotIdx - 1);
                }
                else
                {
                    currentIndex++;
                }
            }
        }

        return FilterOverlappingFormations(allFormations, candles.Count);
    }

    private static IReadOnlyList<DetectedFormation> FilterOverlappingFormations(List<DetectedFormation> formations, int totalCandles)
    {
        if (formations.Count <= 1) return formations;

        // Score based on size, fit, and recency
        var scoredFormations = formations.Select(f => 
        {
            double length = f.EndIndex - f.StartIndex + 1;
            double sizeScore = Math.Pow(length, 1.2); 
            double recentness = 1.0 + (f.EndIndex / (double)totalCandles) * 0.5;
            double score = sizeScore * f.ConfidenceScore * recentness;
            return (Formation: f, Score: score);
        })
        .OrderByDescending(x => x.Score)
        .ToList();

        var selected = new List<DetectedFormation>();
        foreach (var item in scoredFormations)
        {
            bool overlaps = false;
            foreach (var sel in selected)
            {
                int overlapStart = Math.Max(item.Formation.StartIndex, sel.StartIndex);
                int overlapEnd = Math.Min(item.Formation.EndIndex, sel.EndIndex);
                if (overlapStart <= overlapEnd)
                {
                    int overlapLength = overlapEnd - overlapStart + 1;
                    int itemLength = item.Formation.EndIndex - item.Formation.StartIndex + 1;
                    int selLength = sel.EndIndex - sel.StartIndex + 1;
                    
                    if (overlapLength > itemLength * 0.1 || overlapLength > selLength * 0.1)
                    {
                        overlaps = true;
                        break;
                    }
                }
            }
            if (!overlaps)
            {
                selected.Add(item.Formation);
            }
        }

        return selected.OrderBy(f => f.StartIndex).ToList();
    }

    /// <summary>
    /// Detects the most recent geometric formation (useful for screening).
    /// </summary>
    public static DetectedFormation? DetectLatest(
        IReadOnlyList<CandleData> candles,
        decimal zigzagThreshold,
        bool useRobustFit = false,
        bool promoteFlagsAndPennants = false)
    {
        return DetectLatest(candles, new[] { zigzagThreshold }, useRobustFit, promoteFlagsAndPennants);
    }

    public static DetectedFormation? DetectLatest(
        IReadOnlyList<CandleData> candles,
        decimal[]? zigzagThresholds = null,
        bool useRobustFit = false,
        bool promoteFlagsAndPennants = false)
    {
        var formations = Detect(candles, zigzagThresholds, useRobustFit, promoteFlagsAndPennants);
        return formations.Count > 0 ? formations.Last() : null;
    }

    /// <summary>
    /// Analyzes a set of swing highs and lows to determine the geometric formation.
    /// </summary>
    internal static DetectedFormation? AnalyzeFormation(
        IReadOnlyList<PivotPoint> highs,
        IReadOnlyList<PivotPoint> lows,
        IReadOnlyList<CandleData> candles,
        bool useRobustFit = false,
        bool promoteFlagsAndPennants = false,
        (double Slope, double Intercept, double RSquared)? precomputedUpperFit = null,
        (double Slope, double Intercept, double RSquared)? precomputedLowerFit = null)
    {
        if (highs.Count < 2 || lows.Count < 2)
            return null;

        // Determine formation boundaries
        int startIndex = Math.Min(highs[0].Index, lows[0].Index);
        int endIndex = Math.Max(highs[highs.Count - 1].Index, lows[lows.Count - 1].Index);

        if (startIndex >= endIndex || startIndex < 0 || endIndex >= candles.Count)
        {
            return null;
        }

        // Formation Process Validation (FR-60-7-01):
        // Skip formations that do not span enough bars to be visually meaningful before expensive regression.
        int span = endIndex - startIndex;
        if (!PatternFormationValidator.ValidateMinBars(span, ChartConstants.FormationMinBarsGeometric))
        {
            return null;
        }

        // Fit robust regression on highs and lows
        double upperSlope, upperIntercept, upperRSquared;
        double lowerSlope, lowerIntercept, lowerRSquared;

        if (precomputedUpperFit.HasValue && precomputedLowerFit.HasValue)
        {
            (upperSlope, upperIntercept, upperRSquared) = precomputedUpperFit.Value;
            (lowerSlope, lowerIntercept, lowerRSquared) = precomputedLowerFit.Value;
        }
        else if (useRobustFit)
        {
            (upperSlope, upperIntercept, upperRSquared) = CalculateRobustFit(highs, candles, startIndex, endIndex);
            (lowerSlope, lowerIntercept, lowerRSquared) = CalculateRobustFit(lows, candles, startIndex, endIndex);
        }
        else
        {
            // Mathematical bound (Gauss-Markov): OLS R^2 is the theoretical maximum R^2 of ANY linear fit.
            // Therefore, Theil-Sen R^2 <= OLS R^2 always.
            // If (OLS_H + OLS_L) / 2 < minRSquared, (TheilSen_H + TheilSen_L) / 2 >= minRSquared is mathematically impossible.
            // LinearRegression is O(N) single-pass while Theil-Sen is O(N^2 log N).
            var (_, _, olsUpperRSquared) = LinearRegression(highs);
            var (_, _, olsLowerRSquared) = LinearRegression(lows);
            if ((olsUpperRSquared + olsLowerRSquared) / 2.0 < ChartConstants.GeometricMinRSquared - 1e-9)
            {
                return null;
            }

            (upperSlope, upperIntercept, upperRSquared) = CalculateTheilSen(highs);
            // Quick reject: avgRSquared requires (upperRSquared + lowerRSquared) / 2 >= minRSquared.
            // Since lowerRSquared <= olsLowerRSquared, upperRSquared + olsLowerRSquared must be >= 2.0 * minRSquared.
            if ((upperRSquared + olsLowerRSquared) / 2.0 < ChartConstants.GeometricMinRSquared - 1e-9)
            {
                return null;
            }

            (lowerSlope, lowerIntercept, lowerRSquared) = CalculateTheilSen(lows);
        }

        // Require minimum fit quality
        double avgRSquared = (upperRSquared + lowerRSquared) / 2.0;
        if (avgRSquared < ChartConstants.GeometricMinRSquared)
        {
            return null;
        }

        // Compute average price to normalize slope thresholds and breakouts
        double avgPrice = (highs.Average(p => (double)p.Price) + lows.Average(p => (double)p.Price)) / 2.0;
        double atr = ComputeATR(candles, startIndex, endIndex);
        decimal breakoutTolerance = (decimal)Math.Max(
            avgPrice * (double)ChartConstants.GeometricBreakoutTolerance,
            atr * ChartConstants.GeometricAtrBreakoutMultiplier);

        // Adjust intercepts to align with the start of the formation index
        // so we can calculate the price as: Slope * (index - startIndex) + InterceptAtStart
        double upperInterceptAtStart = upperSlope * (startIndex - highs[0].Index) + upperIntercept;
        double lowerInterceptAtStart = lowerSlope * (startIndex - lows[0].Index) + lowerIntercept;

        // Robust Breakout Filter:
        // Allow a small percentage of candles to materially break the formation lines.
        // E.g., max 10% wick outliers and max 5% close outliers.
        int totalCandles = endIndex - startIndex + 1;
        int wickOutliers = 0;
        int closeOutliers = 0;

        for (int i = startIndex; i <= endIndex; i++)
        {
            decimal candleHigh = candles[i].High;
            decimal candleLow = candles[i].Low;
            decimal candleClose = candles[i].Close;
            
            // X-axis is the relative index from the formation start
            double relativeIndex = i - startIndex;

            decimal upperLinePrice = (decimal)(upperSlope * relativeIndex + upperInterceptAtStart);
            decimal lowerLinePrice = (decimal)(lowerSlope * relativeIndex + lowerInterceptAtStart);

            // Check if wick breaks resistance or support
            if (candleHigh > upperLinePrice + breakoutTolerance || candleLow < lowerLinePrice - breakoutTolerance)
            {
                wickOutliers++;
                if ((double)wickOutliers / totalCandles > 0.10)
                {
                    return null;
                }
            }

            // Check if close price breaks resistance or support
            if (candleClose > upperLinePrice + breakoutTolerance || candleClose < lowerLinePrice - breakoutTolerance)
            {
                closeOutliers++;
                if ((double)closeOutliers / totalCandles > 0.05)
                {
                    return null;
                }
            }
        }

        // Scan forward for a definitive breakout
        bool breakoutFound = false;
        int breakoutIndex = endIndex;
        for (int i = endIndex + 1; i < candles.Count; i++)
        {
            decimal candleClose = candles[i].Close;
            double relativeIndex = i - startIndex;
            decimal upperLinePrice = (decimal)(upperSlope * relativeIndex + upperInterceptAtStart);
            decimal lowerLinePrice = (decimal)(lowerSlope * relativeIndex + lowerInterceptAtStart);

            if (candleClose > upperLinePrice + breakoutTolerance || candleClose < lowerLinePrice - breakoutTolerance)
            {
                breakoutFound = true;
                breakoutIndex = i;
                break;
            }
        }

        // We no longer discard the pattern if it hasn't broken out yet.
        // Doing so would cause the detector to ignore the large forming pattern
        // and mistakenly identify smaller nested patterns that did "break out".
        if (breakoutFound)
        {
            endIndex = breakoutIndex;
        }

        // Touch verification: discard patterns that do not genuinely touch the boundaries at least twice
        int upperTouchCount = CountTouches(upperSlope, upperInterceptAtStart, highs, ChartConstants.GeometricTouchTolerance, startIndex);
        int lowerTouchCount = CountTouches(lowerSlope, lowerInterceptAtStart, lows, ChartConstants.GeometricTouchTolerance, startIndex);
        if (upperTouchCount < 2 || lowerTouchCount < 2)
        {
            return null;
        }

        // Classify the formation based on slope relationship and ATR normalized thresholds
        var formationType = ClassifyFormation(upperSlope, upperInterceptAtStart, lowerSlope, lowerInterceptAtStart, candles, startIndex, endIndex);
        if (formationType == null)
        {
            return null;
        }

        // Check for pole (flag/pennant)
        int poleDirection = DetectPoleDirection(candles, startIndex);
        bool hasPole = poleDirection != 0;

        // Refine classification based on pole presence
        var finalType = RefineClassification(formationType.Value, hasPole, upperSlope, lowerSlope, avgPrice);
        if (promoteFlagsAndPennants)
        {
            finalType = PromoteWithPole(finalType, poleDirection);
        }

        return new DetectedFormation(
            finalType,
            startIndex,
            endIndex,
            upperSlope,
            upperInterceptAtStart,
            lowerSlope,
            lowerInterceptAtStart,
            avgRSquared,
            hasPole,
            candles[startIndex].Time,
            candles[endIndex].Time)
        {
            IsBrokenOut = breakoutFound
        };
    }

    /// <summary>
    /// Classifies the formation type based on ATR-normalized slopes and Apex intercept verification.
    /// Uses absolute threshold values rather than simple slope comparisons to match human vision.
    /// </summary>
    internal static GeometricFormationType? ClassifyFormation(
        double upperSlope, double upperIntercept,
        double lowerSlope, double lowerIntercept,
        IReadOnlyList<CandleData> candles, int startIndex, int endIndex)
    {
        double normUpperSlope = NormalizeSlope(upperSlope, candles, startIndex, endIndex);
        double normLowerSlope = NormalizeSlope(lowerSlope, candles, startIndex, endIndex);

        // Normalized Thresholds
        const double flatThreshold = ChartConstants.GeometricFlatSlopeThreshold; // Practically horizontal

        bool upperFlat = Math.Abs(normUpperSlope) < flatThreshold;
        bool lowerFlat = Math.Abs(normLowerSlope) < flatThreshold;
        bool upperRising = normUpperSlope > flatThreshold;
        bool upperFalling = normUpperSlope < -flatThreshold;
        bool lowerRising = normLowerSlope > flatThreshold;
        bool lowerFalling = normLowerSlope < -flatThreshold;

        double apexBarOffset = ComputeApexBarOffset(upperSlope, upperIntercept, lowerSlope, lowerIntercept);
        // We consider the pattern relative to startIndex. A positive offset means the intersection is in the "future".
        bool isConverging = apexBarOffset > 0 && !double.IsPositiveInfinity(apexBarOffset);
        bool isDiverging = apexBarOffset < 0;
        bool isParallel = double.IsPositiveInfinity(apexBarOffset) || Math.Abs(apexBarOffset) > (endIndex - startIndex) * 4; // Apex is far away

        if (isParallel)
        {
            if (upperFlat && lowerFlat)
                return GeometricFormationType.HorizontalBox;
            
            // Both slopes are same sign and roughly parallel
            if (upperRising || lowerRising)
                return GeometricFormationType.AscendingChannel;
            if (upperFalling || lowerFalling)
                return GeometricFormationType.DescendingChannel;
                
            return GeometricFormationType.HorizontalBox; // Fallback for very slight parallel slopes
        }
        else if (isDiverging)
        {
            // STRICT MEGAPHONE CONDITIONS
            // 1. Upper line must be strictly rising, lower line strictly falling
            if (upperRising && lowerFalling)
            {
                // 2. Volatility (width between lines) must be expanding materially (e.g. 1.3x)
                double startWidth = Math.Abs(upperIntercept - lowerIntercept);
                int length = endIndex - startIndex;
                double endWidth = Math.Abs((upperSlope * length + upperIntercept) - (lowerSlope * length + lowerIntercept));
                
                // Protect against divide-by-zero if lines cross exactly at startIndex
                if (startWidth > 0 && (endWidth / startWidth) >= 1.3)
                {
                    return GeometricFormationType.Megaphone;
                }
            }
            
            // If it diverges but doesn't meet strict criteria (e.g., upper is flat and lower is falling rapidly),
            // it's not a valid recognizable pattern. We do not fallback for diverging shapes.
            return null;
        }
        else if (isConverging)
        {
             if (upperRising && lowerRising)
             {
                 return normLowerSlope > normUpperSlope ? GeometricFormationType.RisingWedge : GeometricFormationType.AscendingChannel;
             }
             if (upperFalling && lowerFalling)
             {
                 return normUpperSlope < normLowerSlope ? GeometricFormationType.FallingWedge : GeometricFormationType.DescendingChannel;
             }
             
             if (upperFlat && lowerRising)
                return GeometricFormationType.AscendingTriangle;
             if (lowerFlat && upperFalling)
                return GeometricFormationType.DescendingTriangle;
                
             // Slopes are opposite signs
             return GeometricFormationType.SymmetricalTriangle;
        }

        return null;
    }

    /// <summary>
    /// Refines the classification, e.g., downgrading flags/pennants to channels/triangles if no pole is present.
    /// </summary>
    internal static GeometricFormationType RefineClassification(
        GeometricFormationType baseType, bool hasPole, double upperSlope, double lowerSlope, double avgPrice)
    {
        double dynamicFlatThreshold = avgPrice * 0.001;
        bool isUpwardTrend = (upperSlope + lowerSlope) > 0;

        switch (baseType)
        {
            case GeometricFormationType.BullishFlag:
            case GeometricFormationType.BearishFlag:
                if (!hasPole)
                {
                    // Downgrade to channel
                    if (Math.Abs(upperSlope) < dynamicFlatThreshold && Math.Abs(lowerSlope) < dynamicFlatThreshold)
                        return GeometricFormationType.HorizontalBox;
                        
                    return isUpwardTrend ? GeometricFormationType.AscendingChannel : GeometricFormationType.DescendingChannel;
                }
                return baseType;

            case GeometricFormationType.Pennant:
                if (!hasPole)
                {
                    // Downgrade to triangle
                    if (Math.Abs(upperSlope) < dynamicFlatThreshold)
                        return GeometricFormationType.AscendingTriangle;
                    if (Math.Abs(lowerSlope) < dynamicFlatThreshold)
                        return GeometricFormationType.DescendingTriangle;
                        
                    return GeometricFormationType.SymmetricalTriangle;
                }
                return baseType;

            default:
                return baseType;
        }
    }

    /// <summary>
    /// Detects if there is a strong directional price movement (pole) immediately preceding the formation start,
    /// returning +1 for an upward pole, -1 for a downward pole, or 0 if no pole exists.
    /// </summary>
    internal static int DetectPoleDirection(IReadOnlyList<CandleData> candles, int startIndex)
    {
        int lookback = ChartConstants.GeometricPoleLookbackBars;
        if (startIndex < lookback)
            return 0;

        int searchStart = startIndex - lookback;
        decimal startPrice = candles[searchStart].Close;
        decimal endPrice = candles[startIndex].Close;

        if (startPrice <= 0m) return 0;

        // Check if the total move meets the minimum percentage requirement (pure decimal)
        decimal movePct = Math.Abs((endPrice - startPrice) / startPrice) * 100m;
        if (movePct < ChartConstants.GeometricPoleMinPercent)
            return 0;

        // Check for directional consistency (e.g. at least 60% of candles moved in the same direction)
        int sameDirectionCount = 0;
        for (int i = searchStart; i < startIndex; i++)
        {
            if (candles[i].Close > candles[i].Open && endPrice > startPrice)
                sameDirectionCount++;
            else if (candles[i].Close < candles[i].Open && endPrice < startPrice)
                sameDirectionCount++;
        }

        double consistencyRatio = (double)sameDirectionCount / lookback;
        if (consistencyRatio < 0.6)
            return 0;

        return endPrice > startPrice ? 1 : -1;
    }

    /// <summary>
    /// Detects if there is a strong directional price movement (pole) immediately preceding the formation start.
    /// Verifies both the sheer magnitude of the move and directional consistency.
    /// </summary>
    internal static bool DetectPole(IReadOnlyList<CandleData> candles, int startIndex)
    {
        return DetectPoleDirection(candles, startIndex) != 0;
    }

    /// <summary>
    /// Promotes channels, boxes, or triangles to Flag or Pennant formations based on preceding pole direction.
    /// </summary>
    internal static GeometricFormationType PromoteWithPole(GeometricFormationType baseType, int poleDirection)
    {
        if (poleDirection == 1) // Bullish pole
        {
            if (baseType == GeometricFormationType.DescendingChannel || baseType == GeometricFormationType.HorizontalBox)
                return GeometricFormationType.BullishFlag;
        }
        else if (poleDirection == -1) // Bearish pole
        {
            if (baseType == GeometricFormationType.AscendingChannel || baseType == GeometricFormationType.HorizontalBox)
                return GeometricFormationType.BearishFlag;
        }

        if (poleDirection != 0)
        {
            if (baseType == GeometricFormationType.SymmetricalTriangle ||
                baseType == GeometricFormationType.AscendingTriangle ||
                baseType == GeometricFormationType.DescendingTriangle)
            {
                return GeometricFormationType.Pennant;
            }
        }

        return baseType;
    }

    /// <summary>
    /// Determines if two slopes are approximately parallel using the dynamic flat threshold.
    /// </summary>
    internal static bool AreSlopesParallel(double slopeA, double slopeB, double dynamicFlatThreshold)
    {
        // Same sign check
        if (Math.Sign(slopeA) != Math.Sign(slopeB) && Math.Abs(slopeA) > dynamicFlatThreshold && Math.Abs(slopeB) > dynamicFlatThreshold)
            return false;

        double maxAbs = Math.Max(Math.Abs(slopeA), Math.Abs(slopeB));
        if (maxAbs < dynamicFlatThreshold)
        {
            // Both near-zero: consider parallel (horizontal channel)
            return true;
        }

        double ratio = Math.Abs(slopeA - slopeB) / maxAbs;
        return ratio < ChartConstants.GeometricParallelSlopeRatio;
    }

    /// <summary>
    /// Determines if two trendlines are converging (narrowing).
    /// Upper slope must be negative or less than lower slope (gap is decreasing).
    /// </summary>
    internal static bool AreSlopesConverging(double upperSlope, double lowerSlope, double dynamicFlatThreshold)
    {
        // Converging: upper line going down (or less upward) and lower line going up (or less downward)
        // The gap between the lines should be decreasing over time
        return upperSlope < lowerSlope && !AreSlopesParallel(upperSlope, lowerSlope, dynamicFlatThreshold);
    }

    /// <summary>
    /// Determines if two trendlines are diverging (expanding).
    /// Upper slope must be greater than lower slope by a significant margin.
    /// </summary>
    internal static bool AreSlopesDiverging(double upperSlope, double lowerSlope, double dynamicFlatThreshold)
    {
        // Diverging: gap is increasing over time
        double diff = upperSlope - lowerSlope;
        return diff > 0 && !AreSlopesParallel(upperSlope, lowerSlope, dynamicFlatThreshold);
    }

    /// <summary>
    /// Performs simple linear regression on pivot points using Ordinary Least Squares.
    /// Returns (slope, intercept, r-squared) where the x-axis is the pivot index.
    /// </summary>
    internal static (double Slope, double Intercept, double RSquared) LinearRegression(
        IReadOnlyList<PivotPoint> pivots)
    {
        int n = pivots.Count;
        if (n < 2)
            return (0.0, pivots.Count > 0 ? (double)pivots[0].Price : 0.0, 0.0);

        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;

        for (int i = 0; i < n; i++)
        {
            double x = pivots[i].Index;
            double y = (double)pivots[i].Price;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
            sumY2 += y * y;
        }

        double denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < 1e-10)
        {
            return (0.0, sumY / n, 0.0);
        }

        double numerator = n * sumXY - sumX * sumY;
        double slope = numerator / denominator;
        double intercept = (sumY - slope * sumX) / n;

        // R-squared calculation: Single pass algebraic identity
        // R^2 = Sxy^2 / (Sxx * Syy) = numerator^2 / (denominator * varY)
        double varY = n * sumY2 - sumY * sumY;
        double rSquared = (varY > 1e-10) ? Math.Clamp((numerator * numerator) / (denominator * varY), 0.0, 1.0) : 0.0;

        // Adjust intercept to be relative to the first pivot's index
        double adjustedIntercept = slope * pivots[0].Index + intercept;

        return (slope, adjustedIntercept, rSquared);
    }

    /// <summary>
    /// Performs robust Theil-Sen regression on pivot points to ignore outliers (wicks).
    /// Returns (slope, intercept, pseudo-r-squared) where the x-axis is the pivot index.
    /// </summary>
    internal static (double Slope, double InterceptAtStart, double RSquared) CalculateTheilSen(
        IReadOnlyList<PivotPoint> pivots)
    {
        if (pivots == null || pivots.Count < 2)
            return (0.0, pivots != null && pivots.Count > 0 ? (double)pivots[0].Price : 0.0, 0.0);

        var (slope, _, startIntercept, ssTot, ssRes) = ComputeTheilSenLine(pivots);
        double rSquared = ssTot > 0 ? Math.Max(0.0, 1.0 - (ssRes / ssTot)) : 0.0;
        return (slope, startIntercept, rSquared);
    }

    /// <summary>
    /// Computes the median slope, global intercept, adjusted start intercept, total sum of squares,
    /// and residual sum of squares for a set of pivot points using Theil-Sen regression.
    /// </summary>
    private static (double MedianSlope, double MedianGlobalIntercept, double StartIntercept, double SsTot, double SsRes) ComputeTheilSenLine(
        IReadOnlyList<PivotPoint> pivots)
    {
        int n = pivots?.Count ?? 0;
        if (n < 2)
        {
            double fallbackPrice = pivots != null && pivots.Count > 0 ? (double)pivots[0].Price : 0.0;
            return (0.0, fallbackPrice, fallbackPrice, 0.0, 0.0);
        }

        int maxPairs = n * (n - 1) / 2;
        double[] rented = ArrayPool<double>.Shared.Rent(maxPairs);
        try
        {
            int slopeCount = 0;
            for (int i = 0; i < n; i++)
            {
                int x1 = pivots[i].Index;
                decimal p1 = pivots[i].Price;
                for (int j = i + 1; j < n; j++)
                {
                    int dx = pivots[j].Index - x1;
                    if (dx != 0)
                    {
                        double dy = (double)(pivots[j].Price - p1);
                        rented[slopeCount++] = dy / dx;
                    }
                }
            }

            if (slopeCount == 0)
            {
                double fallbackPrice = (double)pivots[0].Price;
                return (0.0, fallbackPrice, fallbackPrice, 0.0, 0.0);
            }

            Array.Sort(rented, 0, slopeCount);
            double medianSlope = rented[slopeCount / 2];

            double[]? rentedIntercepts = null;
            Span<double> intercepts = n <= 256
                ? stackalloc double[n]
                : (rentedIntercepts = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);
            try
            {
                double sumY = 0;
                for (int i = 0; i < n; i++)
                {
                    double price = (double)pivots[i].Price;
                    sumY += price;
                    intercepts[i] = price - medianSlope * pivots[i].Index;
                }
                intercepts.Sort();
                double medianGlobalIntercept = intercepts[n / 2];
                double startIntercept = medianSlope * pivots[0].Index + medianGlobalIntercept;

                double ssTot = 0, ssRes = 0;
                double yMean = sumY / n;

                for (int i = 0; i < n; i++)
                {
                    double predicted = medianSlope * pivots[i].Index + medianGlobalIntercept;
                    double actual = (double)pivots[i].Price;

                    ssTot += (actual - yMean) * (actual - yMean);
                    ssRes += (actual - predicted) * (actual - predicted);
                }

                return (medianSlope, medianGlobalIntercept, startIntercept, ssTot, ssRes);
            }
            finally
            {
                if (rentedIntercepts != null)
                {
                    ArrayPool<double>.Shared.Return(rentedIntercepts);
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }


    /// <summary>
    /// Calculates Average True Range (ATR) over a specific range to normalize price scales.
    /// </summary>
    internal static double ComputeATR(IReadOnlyList<CandleData> candles, int startIndex, int endIndex)
    {
        if (endIndex <= startIndex) return 0;
        
        double trSum = 0;
        int count = 0;
        
        for (int i = startIndex; i <= endIndex && i < candles.Count; i++)
        {
            if (i < 1) continue; // Need previous close to calculate TR
            
            double high = (double)candles[i].High;
            double low = (double)candles[i].Low;
            double prevClose = (double)candles[i - 1].Close;
            
            double tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trSum += tr;
            count++;
        }
        
        return count > 0 ? trSum / count : 0;
    }

    /// <summary>
    /// Normalizes a raw price slope into an ATR-based slope (change in ATR per candle).
    /// This makes thresholds independent of the underlying asset's price or timeframe.
    /// </summary>
    internal static double NormalizeSlope(double rawSlope, IReadOnlyList<CandleData> candles, int startIndex, int endIndex)
    {
        double atr = ComputeATR(candles, startIndex, endIndex);
        if (atr < 1e-9) return 0;
        
        // Slope is price change per 1 candle.
        // We divide by ATR to get how many ATRs the price changes per candle.
        return rawSlope / atr;
    }

    /// <summary>
    /// Calculates the X coordinate (bar offset) where the upper and lower regression lines intersect.
    /// Used to determine if lines are converging (apex in the future) or diverging (apex in the past).
    /// </summary>
    internal static double ComputeApexBarOffset(double upperSlope, double upperIntercept, double lowerSlope, double lowerIntercept)
    {
        double slopeDiff = upperSlope - lowerSlope;
        if (Math.Abs(slopeDiff) < 1e-10) return double.PositiveInfinity; // Parallel lines never intersect
        
        // Eq: upperSlope * x + upperIntercept = lowerSlope * x + lowerIntercept
        // x(upperSlope - lowerSlope) = lowerIntercept - upperIntercept
        return (lowerIntercept - upperIntercept) / slopeDiff;
    }

    /// <summary>
    /// Counts how many pivots genuinely touch the regression line within a given tolerance.
    /// Used to discard patterns drawn through only one extreme point.
    /// </summary>
    internal static int CountTouches(double slope, double intercept, IReadOnlyList<PivotPoint> pivots, double touchTolerancePercent, int originIndex = 0)
    {
        int count = 0;
        foreach (var pivot in pivots)
        {
            double expectedPrice = slope * (pivot.Index - originIndex) + intercept;
            if (expectedPrice == 0) continue;
            
            double diffPercent = Math.Abs((double)pivot.Price - expectedPrice) / expectedPrice;
            if (diffPercent <= touchTolerancePercent)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Calculates a robust Theil-Sen fit with an ATR-normalized lower bound for R-squared.
    /// Prevents horizontal flat lines (SS_tot ≈ 0) from collapsing R-squared to 0.
    /// </summary>
    internal static (double Slope, double StartIntercept, double RSquared) CalculateRobustFit(
        IReadOnlyList<PivotPoint> pivots,
        IReadOnlyList<CandleData> candles,
        int startIndex,
        int endIndex)
    {
        if (pivots == null || pivots.Count < 2)
            return (0.0, pivots != null && pivots.Count > 0 ? (double)pivots[0].Price : 0.0, 0.0);

        var (slope, _, startIntercept, ssTot, ssRes) = ComputeTheilSenLine(pivots);
        if (ssRes < 1e-12)
        {
            return (slope, startIntercept, 1.0);
        }

        int n = pivots.Count;
        double atr = ComputeATR(candles, startIndex, endIndex);
        double denom = Math.Max(ssTot, n * atr * atr);

        double rSquared = denom > 0 ? Math.Max(0.0, 1.0 - (ssRes / denom)) : 0.0;
        return (slope, startIntercept, rSquared);
    }

    /// <summary>
    /// Tracks online running sums for ordinary least squares regression in O(1) time.
    /// Provides mathematical upper-bound R^2 to prune futile non-linear or robust fit computations.
    /// </summary>
    internal struct RegressionStats
    {
        public int Count;
        public double SumX;
        public double SumY;
        public double SumXY;
        public double SumX2;
        public double SumY2;

        public void Add(int x, double y)
        {
            Count++;
            SumX += x;
            SumY += y;
            SumXY += x * y;
            SumX2 += x * x;
            SumY2 += y * y;
        }

        public void Remove(int x, double y)
        {
            Count--;
            SumX -= x;
            SumY -= y;
            SumXY -= x * y;
            SumX2 -= x * x;
            SumY2 -= y * y;
        }

        public readonly double RSquared
        {
            get
            {
                if (Count < 2) return 0.0;
                double denominator = Count * SumX2 - SumX * SumX;
                if (Math.Abs(denominator) < 1e-10) return 0.0;
                double numerator = Count * SumXY - SumX * SumY;
                double varY = Count * SumY2 - SumY * SumY;
                if (varY <= 1e-10) return 0.0;
                double r2 = (numerator * numerator) / (denominator * varY);
                return Math.Clamp(r2, 0.0, 1.0);
            }
        }
    }
}


