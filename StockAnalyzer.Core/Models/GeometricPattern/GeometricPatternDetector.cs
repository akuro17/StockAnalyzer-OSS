using System;
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
        decimal zigzagThreshold)
    {
        return Detect(candles, new[] { zigzagThreshold });
    }

    public static IReadOnlyList<DetectedFormation> Detect(
        IReadOnlyList<CandleData> candles,
        decimal[]? zigzagThresholds = null)
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

                for (int length = pivots.Count - currentIndex; length >= ChartConstants.GeometricMinPivotCount; length--)
                {
                    var subset = pivots.Skip(currentIndex).Take(length).ToList();
                    var highs = subset.Where(p => p.IsHigh).ToList();
                    var lows = subset.Where(p => !p.IsHigh).ToList();

                    if (highs.Count >= 2 && lows.Count >= 2)
                    {
                        var formation = AnalyzeFormation(highs, lows, candles);
                        if (formation != null)
                        {
                            if (formation.ConfidenceScore >= ChartConstants.GeometricMinRSquared)
                            {
                                bestFormation = formation;
                                bestEndIndex = currentIndex + length - 1;
                                break; 
                            }
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
        decimal zigzagThreshold)
    {
        return DetectLatest(candles, new[] { zigzagThreshold });
    }

    public static DetectedFormation? DetectLatest(
        IReadOnlyList<CandleData> candles,
        decimal[]? zigzagThresholds = null)
    {
        var formations = Detect(candles, zigzagThresholds);
        return formations.Count > 0 ? formations.Last() : null;
    }

    /// <summary>
    /// Analyzes a set of swing highs and lows to determine the geometric formation.
    /// </summary>
    internal static DetectedFormation? AnalyzeFormation(
        IReadOnlyList<PivotPoint> highs,
        IReadOnlyList<PivotPoint> lows,
        IReadOnlyList<CandleData> candles)
    {
        if (highs.Count < 2 || lows.Count < 2)
            return null;

        // Fit robust regression (Theil-Sen) on highs and lows to ignore wicks/outliers
        var (upperSlope, upperIntercept, upperRSquared) = CalculateTheilSen(highs);
        var (lowerSlope, lowerIntercept, lowerRSquared) = CalculateTheilSen(lows);

        // Require minimum fit quality
        double avgRSquared = (upperRSquared + lowerRSquared) / 2.0;
        if (avgRSquared < ChartConstants.GeometricMinRSquared)
        {
            return null;
        }

        // Determine formation boundaries
        int startIndex = Math.Min(highs[0].Index, lows[0].Index);
        int endIndex = Math.Max(highs[highs.Count - 1].Index, lows[lows.Count - 1].Index);

        if (startIndex >= endIndex || startIndex < 0 || endIndex >= candles.Count)
        {
            return null;
        }

        // Formation Process Validation (FR-60-7-01):
        // Skip formations that do not span enough bars to be visually meaningful.
        int span = endIndex - startIndex;
        if (!PatternFormationValidator.ValidateMinBars(span, ChartConstants.FormationMinBarsGeometric))
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
            }

            // Check if close price breaks resistance or support
            if (candleClose > upperLinePrice + breakoutTolerance || candleClose < lowerLinePrice - breakoutTolerance)
            {
                closeOutliers++;
            }
        }

        // Invalidate if too many outliers exist
        if ((double)wickOutliers / totalCandles > 0.10)
        {
            return null;
        }

        if ((double)closeOutliers / totalCandles > 0.05)
        {
            return null; // Reject if closes frequently break the structure
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
        int upperTouchCount = CountTouches(upperSlope, upperInterceptAtStart, highs, ChartConstants.GeometricTouchTolerance);
        int lowerTouchCount = CountTouches(lowerSlope, lowerInterceptAtStart, lows, ChartConstants.GeometricTouchTolerance);
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
        bool hasPole = DetectPole(candles, startIndex);

        // Refine classification based on pole presence
        var finalType = RefineClassification(formationType.Value, hasPole, upperSlope, lowerSlope, avgPrice);

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
        const double flatThreshold = 0.10; // Practically horizontal

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
    /// Detects if there is a strong directional price movement (pole) immediately preceding the formation start.
    /// Verifies both the sheer magnitude of the move and directional consistency.
    /// </summary>
    internal static bool DetectPole(IReadOnlyList<CandleData> candles, int startIndex)
    {
        int lookback = ChartConstants.GeometricPoleLookbackBars;
        if (startIndex < lookback)
            return false;

        int searchStart = startIndex - lookback;
        decimal startPrice = candles[searchStart].Close;
        decimal endPrice = candles[startIndex].Close;

        if (startPrice == 0) return false;

        // Check if the total move meets the minimum percentage requirement
        double movePct = (double)Math.Abs((endPrice - startPrice) / startPrice) * 100.0;
        if (movePct < (double)ChartConstants.GeometricPoleMinPercent)
            return false;

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
        return consistencyRatio >= 0.6; // Assuming 60% directional consistency is required for an active pole
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

        double slope = (n * sumXY - sumX * sumY) / denominator;
        double intercept = (sumY - slope * sumX) / n;

        // R-squared calculation
        double ssTot = sumY2 - (sumY * sumY) / n;
        double ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            double predicted = slope * pivots[i].Index + intercept;
            double residual = (double)pivots[i].Price - predicted;
            ssRes += residual * residual;
        }

        double rSquared = ssTot > 0 ? 1.0 - ssRes / ssTot : 0.0;

        // Adjust intercept to be relative to the first pivot's index
        double adjustedIntercept = slope * pivots[0].Index + intercept;

        return (slope, adjustedIntercept, Math.Max(0.0, rSquared));
    }

    /// <summary>
    /// Performs robust Theil-Sen regression on pivot points to ignore outliers (wicks).
    /// Returns (slope, intercept, pseudo-r-squared) where the x-axis is the pivot index.
    /// </summary>
    internal static (double Slope, double InterceptAtStart, double RSquared) CalculateTheilSen(
        IReadOnlyList<PivotPoint> pivots)
    {
        int n = pivots.Count;
        if (n < 2)
            return (0.0, pivots.Count > 0 ? (double)pivots[0].Price : 0.0, 0.0);

        var slopes = new List<double>();

        // Calculate slopes of all pairs
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double dx = pivots[j].Index - pivots[i].Index;
                if (Math.Abs(dx) > 0)
                {
                    double dy = (double)pivots[j].Price - (double)pivots[i].Price;
                    slopes.Add(dy / dx);
                }
            }
        }

        if (slopes.Count == 0)
            return (0.0, (double)pivots[0].Price, 0.0);

        // Slope is the median of all pairwise slopes
        slopes.Sort();
        double medianSlope = slopes[slopes.Count / 2];

        // Intercept is the median of (y - mx)
        var intercepts = new List<double>();
        for (int i = 0; i < n; i++)
        {
            intercepts.Add((double)pivots[i].Price - medianSlope * pivots[i].Index);
        }
        intercepts.Sort();
        double medianGlobalIntercept = intercepts[intercepts.Count / 2];

        // Adjusted intercept for startIndex
        double startIntercept = medianSlope * pivots[0].Index + medianGlobalIntercept;

        // Pseudo R-squared (using median-based predictions)
        double ssTot = 0, ssRes = 0;
        double yMean = pivots.Average(p => (double)p.Price);

        for (int i = 0; i < n; i++)
        {
            double predicted = medianSlope * pivots[i].Index + medianGlobalIntercept;
            double actual = (double)pivots[i].Price;
            
            ssTot += (actual - yMean) * (actual - yMean);
            ssRes += (actual - predicted) * (actual - predicted);
        }

        double rSquared = ssTot > 0 ? Math.Max(0.0, 1.0 - (ssRes / ssTot)) : 0.0;

        return (medianSlope, startIntercept, rSquared);
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
    internal static int CountTouches(double slope, double intercept, IReadOnlyList<PivotPoint> pivots, double touchTolerancePercent)
    {
        int count = 0;
        foreach (var pivot in pivots)
        {
            double expectedPrice = slope * pivot.Index + intercept;
            if (expectedPrice == 0) continue;
            
            double diffPercent = Math.Abs((double)pivot.Price - expectedPrice) / expectedPrice;
            if (diffPercent <= touchTolerancePercent)
            {
                count++;
            }
        }
        return count;
    }
}


