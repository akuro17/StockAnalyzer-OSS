using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.MarketStructure;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// High-performance mathematical engine for Hough Transform line extraction from price series or pivot points.
/// Complies with ZeroAllocation in hot paths via ArrayPool and pre-allocated buffers.
/// </summary>
public static class HoughTransformEngine
{
    private const double Epsilon = 1e-9;
    private const double Sqrt2 = 1.4142135623730951;
    private const double DefaultMinRho = -Sqrt2;
    private const double DefaultMaxRho = Sqrt2;

    // Fast trigonometric LUT for standard angles (0 to 180 degrees)
    private static readonly (double Cos, double Sin)[] StandardTrigLut1Deg = BuildTrigLut(180);

    private static (double Cos, double Sin)[] BuildTrigLut(int steps)
    {
        var lut = new (double Cos, double Sin)[steps];
        double stepRad = Math.PI / steps;
        for (int i = 0; i < steps; i++)
        {
            double theta = i * stepRad;
            lut[i] = (Math.Cos(theta), Math.Sin(theta));
        }
        return lut;
    }

    /// <summary>
    /// Detects structural trend, support, and resistance lines from raw candlestick data using fractal pivots and Hough Transform.
    /// </summary>
    public static HoughTransformResult DetectLinesFromCandles(
        IReadOnlyList<CandleData> candles,
        int lookback = 100,
        int pivotWindow = 3,
        double thetaStepDegrees = 1.0,
        double rhoStep = 0.02,
        int voteThreshold = 3,
        int maxLines = 5,
        HoughNormalizationMode normalization = HoughNormalizationMode.MinMax,
        double rhoSuppression = 0.08,
        double thetaSuppressionDegrees = 10.0,
        bool useVolumeWeight = false)
    {
        if (candles == null || candles.Count < Math.Max(lookback, pivotWindow * 2 + 1))
        {
            return HoughTransformResult.Empty;
        }

        int startIndex = Math.Max(0, candles.Count - lookback);
        int endIndex = candles.Count - 1;
        int count = endIndex - startIndex + 1;

        // Sub-slice candles for analysis
        var windowCandles = new CandleData[count];
        for (int i = 0; i < count; i++)
        {
            windowCandles[i] = candles[startIndex + i];
        }

        // 1. Extract Fractal Pivots using existing PivotDetectionEngine
        var pivotBuffer = new List<FractalPivot>(count / 2);
        PivotDetectionEngine.ExtractPivots(windowCandles, pivotWindow, pivotWindow, pivotBuffer);

        // Filter: Causal assurance - only use confirmed pivots (index <= count - 1 - pivotWindow)
        int maxConfirmedBarIndex = count - 1 - pivotWindow;
        var causalPivots = new List<FractalPivot>(pivotBuffer.Count);
        for (int i = 0; i < pivotBuffer.Count; i++)
        {
            if (pivotBuffer[i].Index <= maxConfirmedBarIndex)
            {
                causalPivots.Add(pivotBuffer[i]);
            }
        }

        if (causalPivots.Count < 2)
        {
            return HoughTransformResult.Empty;
        }

        // 2. Map Pivots to HoughPoints (with optional median-normalized log volume weighting)
        double medianVolume = 1.0;
        if (useVolumeWeight && causalPivots.Count > 0)
        {
            var vols = new double[causalPivots.Count];
            for (int i = 0; i < causalPivots.Count; i++)
            {
                int bIdx = causalPivots[i].Index;
                vols[i] = (bIdx >= 0 && bIdx < windowCandles.Length) ? (double)windowCandles[bIdx].Volume : 1.0;
            }
            Array.Sort(vols);
            medianVolume = Math.Max(1.0, vols[vols.Length / 2]);
        }

        var points = new List<HoughPoint>(causalPivots.Count);
        for (int i = 0; i < causalPivots.Count; i++)
        {
            var p = causalPivots[i];
            double weight = 1.0;
            if (useVolumeWeight && p.Index >= 0 && p.Index < windowCandles.Length)
            {
                double vol = (double)windowCandles[p.Index].Volume;
                // Log-compressed relative volume to resist single massive volume outliers
                weight = 1.0 + Math.Log(1.0 + Math.Max(0.0, vol) / medianVolume);
            }
            points.Add(new HoughPoint(p.Index, p.Price, weight));
        }

        // 3. Run detection
        var result = DetectLines(
            points,
            count,
            thetaStepDegrees,
            rhoStep,
            voteThreshold,
            maxLines,
            normalization,
            rhoSuppression,
            thetaSuppressionDegrees);

        if (result.Lines.Count == 0)
        {
            return result;
        }

        // 4. Enrich lines with OLS refinement, ATR-normalized slope, touch counts, and line classification
        double atr = GeometricPatternDetector.ComputeATR(candles, startIndex, endIndex);
        decimal currentPrice = candles[endIndex].Close;

        var enrichedLines = new List<HoughDetectedLine>(result.Lines.Count);
        var pivotPointsForTouch = new List<PivotPoint>(causalPivots.Count);
        for (int i = 0; i < causalPivots.Count; i++)
        {
            pivotPointsForTouch.Add(new PivotPoint(
                causalPivots[i].Index,
                causalPivots[i].Timestamp,
                causalPivots[i].Price,
                causalPivots[i].Type == FractalPivotType.High));
        }

        double touchToleranceAtr = 0.15 * Math.Max(atr, 0.01);

        foreach (var line in result.Lines)
        {
            if (line.IsVertical)
            {
                enrichedLines.Add(line);
                continue;
            }

            // Inlier collection: find pivots within ATR tolerance of line
            var inliers = new List<PivotPoint>();
            int minBar = int.MaxValue;
            int maxBar = int.MinValue;

            for (int i = 0; i < pivotPointsForTouch.Count; i++)
            {
                var pt = pivotPointsForTouch[i];
                double expectedPrice = line.Slope * pt.Index + ((double)line.StartPrice - line.Slope * line.StartBar);
                double diff = Math.Abs((double)pt.Price - expectedPrice);
                if (diff <= touchToleranceAtr)
                {
                    inliers.Add(pt);
                    if (pt.Index < minBar) minBar = pt.Index;
                    if (pt.Index > maxBar) maxBar = pt.Index;
                }
            }

            double refinedSlope = line.Slope;
            decimal refinedStartPrice = line.StartPrice;
            decimal refinedEndPrice = line.EndPrice;
            double rSquared = 1.0;
            int span = (inliers.Count >= 2) ? (maxBar - minBar) : 0;

            // Refine with LinearRegression if 2 or more inliers found
            if (inliers.Count >= 2)
            {
                var (regSlope, regInterceptAtStart, regRSquared) = GeometricPatternDetector.LinearRegression(inliers);
                refinedSlope = regSlope;
                rSquared = regRSquared;
                double globalIntercept = (double)inliers[0].Price - refinedSlope * inliers[0].Index;
                refinedStartPrice = (decimal)(refinedSlope * 0 + globalIntercept);
                refinedEndPrice = (decimal)(refinedSlope * (count - 1) + globalIntercept);
            }

            int globalStartBar = startIndex + line.StartBar;
            int globalEndBar = startIndex + line.EndBar;
            double normSlope = (atr > Epsilon) ? refinedSlope / atr : 0.0;

            int touches = inliers.Count;
            if (touches == 0) touches = line.Votes;

            // Classify line
            decimal priceAtEnd = refinedStartPrice + (decimal)(refinedSlope * (count - 1));
            HoughLineType classification;
            if (Math.Abs(refinedSlope) < 0.05 * atr)
            {
                classification = priceAtEnd < currentPrice ? HoughLineType.Support : HoughLineType.Resistance;
            }
            else if (refinedSlope > 0)
            {
                classification = HoughLineType.TrendUp;
            }
            else
            {
                classification = HoughLineType.TrendDown;
            }

            // Saturated touch score [0, 100] * span coverage [0.1, 1.0]
            double touchScore = Math.Clamp((touches - 2) / 4.0, 0.0, 1.0) * 100.0;
            double spanCoverage = Math.Clamp(span / Math.Max(1.0, count * 0.5), 0.1, 1.0);
            double refinedStrength = touchScore * spanCoverage;

            enrichedLines.Add(new HoughDetectedLine(
                line.Rho,
                line.Theta,
                line.Votes,
                refinedSlope,
                normSlope,
                globalStartBar,
                globalEndBar,
                refinedStartPrice,
                refinedEndPrice,
                refinedStrength,
                touches,
                classification,
                IsVertical: false,
                Span: span,
                RSquared: rSquared));
        }

        // Detect parallel channels from detected lines
        var channels = DetectChannels(enrichedLines, atr);

        return new HoughTransformResult(
            enrichedLines,
            channels,
            result.TotalCandidatePoints,
            result.AccumulatorRows,
            result.AccumulatorCols);
    }

    /// <summary>
    /// Executes core Hough Transform line detection on normalized 2D points.
    /// </summary>
    public static HoughTransformResult DetectLines(
        IReadOnlyList<HoughPoint> points,
        int totalBars,
        double thetaStepDegrees = 1.0,
        double rhoStep = 0.02,
        int voteThreshold = 3,
        int maxLines = 5,
        HoughNormalizationMode normalization = HoughNormalizationMode.MinMax,
        double rhoSuppression = 0.08,
        double thetaSuppressionDegrees = 10.0)
    {
        if (points == null || points.Count < 2 || totalBars <= 1)
        {
            return HoughTransformResult.Empty;
        }

        // 1. Angle resolution and Trig LUT
        int thetaBins = Math.Max(18, (int)Math.Round(180.0 / Math.Max(0.1, thetaStepDegrees)));
        double thetaStepRad = Math.PI / thetaBins;
        var trigLut = (thetaBins == 180) ? StandardTrigLut1Deg : BuildTrigLut(thetaBins);

        // 2. Coordinate normalization
        int pointCount = points.Count;
        double minPriceD = double.MaxValue;
        double maxPriceD = double.MinValue;
        double priceSum = 0;

        for (int i = 0; i < pointCount; i++)
        {
            double p = (double)points[i].Price;
            if (p < minPriceD) minPriceD = p;
            if (p > maxPriceD) maxPriceD = p;
            priceSum += p;
        }

        double priceRangeD = maxPriceD - minPriceD;
        bool isFlatPrice = priceRangeD < Epsilon;
        double effectivePriceRange = isFlatPrice ? 1.0 : priceRangeD;

        double meanPrice = priceSum / pointCount;
        double stdDev = 0;
        if (normalization == HoughNormalizationMode.ZScore && !isFlatPrice)
        {
            double varianceSum = 0;
            for (int i = 0; i < pointCount; i++)
            {
                double diff = (double)points[i].Price - meanPrice;
                varianceSum += diff * diff;
            }
            stdDev = Math.Sqrt(varianceSum / pointCount);
            if (stdDev < Epsilon) stdDev = 1.0;
        }

        // Normalized 2D coordinates [0, 1] x [0, 1]
        var normX = ArrayPool<double>.Shared.Rent(pointCount);
        var normY = ArrayPool<double>.Shared.Rent(pointCount);

        try
        {
            double maxBarIndex = totalBars - 1;
            for (int i = 0; i < pointCount; i++)
            {
                normX[i] = points[i].BarIndex / maxBarIndex;

                double rawP = (double)points[i].Price;
                normY[i] = isFlatPrice ? 0.5 : normalization switch
                {
                    HoughNormalizationMode.MinMax => (rawP - minPriceD) / effectivePriceRange,
                    HoughNormalizationMode.Log => Math.Log(Math.Max(1e-4, rawP / minPriceD)),
                    HoughNormalizationMode.Relative => rawP / minPriceD,
                    HoughNormalizationMode.ZScore => (rawP - meanPrice) / stdDev,
                    _ => rawP // Raw
                };
            }

            // In MinMax [0, 1] space, rho = x*cos + y*sin ranges from -1.0 to +1.42
            double minRho = (normalization == HoughNormalizationMode.MinMax) ? DefaultMinRho : -2.0;
            double maxRho = (normalization == HoughNormalizationMode.MinMax) ? DefaultMaxRho : 2.0;
            double rhoRange = maxRho - minRho;
            int rhoBins = Math.Max(20, (int)Math.Ceiling(rhoRange / Math.Max(0.001, rhoStep)));
            double actualRhoStep = rhoRange / rhoBins;

            // 3. Accumulator Allocation
            int totalBins = rhoBins * thetaBins;
            int[] accumulator = ArrayPool<int>.Shared.Rent(totalBins);
            Array.Clear(accumulator, 0, totalBins);

            try
            {
                // 4. Voting Loop
                for (int i = 0; i < pointCount; i++)
                {
                    double xi = normX[i];
                    double yi = normY[i];
                    int weight = Math.Max(1, (int)Math.Round(points[i].Weight));

                    for (int t = 0; t < thetaBins; t++)
                    {
                        var (cosT, sinT) = trigLut[t];
                        double rho = xi * cosT + yi * sinT;

                        int rIdx = (int)Math.Floor((rho - minRho) / actualRhoStep);
                        if (rIdx >= 0 && rIdx < rhoBins)
                        {
                            accumulator[rIdx * thetaBins + t] += weight;
                        }
                    }
                }

                // 5. Peak Detection with 3x3 Local Maxima and Non-Maximum Suppression (NMS)
                var candidatePeaks = new List<(int RIdx, int TIdx, int Votes)>();

                for (int r = 1; r < rhoBins - 1; r++)
                {
                    int rowOffset = r * thetaBins;
                    for (int t = 0; t < thetaBins; t++)
                    {
                        int votes = accumulator[rowOffset + t];
                        if (votes < voteThreshold) continue;

                        // Check 8-neighborhood
                        bool isLocalMax = true;
                        for (int dr = -1; dr <= 1 && isLocalMax; dr++)
                        {
                            int neighborRow = (r + dr) * thetaBins;
                            for (int dt = -1; dt <= 1; dt++)
                            {
                                if (dr == 0 && dt == 0) continue;
                                int nt = (t + dt + thetaBins) % thetaBins; // Circular wrap in theta
                                if (accumulator[neighborRow + nt] > votes)
                                {
                                    isLocalMax = false;
                                    break;
                                }
                            }
                        }

                        if (isLocalMax)
                        {
                            candidatePeaks.Add((r, t, votes));
                        }
                    }
                }

                // Sort candidate peaks by vote count descending
                candidatePeaks.Sort((a, b) => b.Votes.CompareTo(a.Votes));

                // 6. Non-Maximum Suppression (Clustering)
                double thetaSuppRad = (thetaSuppressionDegrees * Math.PI) / 180.0;
                var selectedPeaks = new List<(int RIdx, int TIdx, int Votes)>();

                foreach (var peak in candidatePeaks)
                {
                    double peakRho = minRho + (peak.RIdx + 0.5) * actualRhoStep;
                    double peakTheta = peak.TIdx * thetaStepRad;

                    bool isSuppressed = false;
                    foreach (var selected in selectedPeaks)
                    {
                        double selRho = minRho + (selected.RIdx + 0.5) * actualRhoStep;
                        double selTheta = selected.TIdx * thetaStepRad;

                        double dRho = Math.Abs(peakRho - selRho);
                        double dTheta = Math.Abs(peakTheta - selTheta);
                        if (dTheta > Math.PI / 2) dTheta = Math.PI - dTheta;

                        if (dRho <= rhoSuppression && dTheta <= thetaSuppRad)
                        {
                            isSuppressed = true;
                            break;
                        }
                    }

                    if (!isSuppressed)
                    {
                        selectedPeaks.Add(peak);
                        if (selectedPeaks.Count >= maxLines) break;
                    }
                }

                // 7. Reconstruct Lines back to Chart Coordinates
                var detectedLines = new List<HoughDetectedLine>(selectedPeaks.Count);
                foreach (var peak in selectedPeaks)
                {
                    double rho = minRho + (peak.RIdx + 0.5) * actualRhoStep;
                    double theta = peak.TIdx * thetaStepRad;
                    var (cosT, sinT) = trigLut[peak.TIdx];

                    // Guard against vertical line division by zero
                    if (Math.Abs(sinT) < 1e-4)
                    {
                        double normXIntercept = rho / cosT;
                        int barX = (int)Math.Round(normXIntercept * (totalBars - 1));
                        barX = Math.Clamp(barX, 0, totalBars - 1);

                        detectedLines.Add(new HoughDetectedLine(
                            rho,
                            theta,
                            peak.Votes,
                            double.PositiveInfinity,
                            0.0,
                            barX,
                            barX,
                            (decimal)minPriceD,
                            (decimal)maxPriceD,
                            (double)peak.Votes / pointCount,
                            peak.Votes,
                            HoughLineType.Neutral,
                            IsVertical: true,
                            Span: 0,
                            RSquared: 1.0));
                        continue;
                    }

                    // Normalised line: y_norm = (-cosT / sinT) * x_norm + (rho / sinT)
                    double normSlope = -cosT / sinT;
                    double normIntercept = rho / sinT;

                    // Convert back to price space
                    // x_norm = x / (totalBars - 1)
                    // y_norm = (price - minPrice) / priceRange
                    // price = priceRange * (normSlope * (x / (totalBars - 1)) + normIntercept) + minPrice
                    double slopePrice = isFlatPrice ? 0.0 : (priceRangeD / Math.Max(1, totalBars - 1)) * normSlope;
                    double interceptPrice = isFlatPrice ? minPriceD : priceRangeD * normIntercept + minPriceD;

                    decimal startPrice = isFlatPrice ? (decimal)minPriceD : (decimal)(slopePrice * 0 + interceptPrice);
                    decimal endPrice = isFlatPrice ? (decimal)minPriceD : (decimal)(slopePrice * (totalBars - 1) + interceptPrice);

                    double strength = (double)peak.Votes / pointCount;

                    detectedLines.Add(new HoughDetectedLine(
                        rho,
                        theta,
                        peak.Votes,
                        slopePrice,
                        0.0, // Will be enriched with ATR in DetectLinesFromCandles
                        0,
                        totalBars - 1,
                        startPrice,
                        endPrice,
                        strength,
                        peak.Votes,
                        HoughLineType.Neutral,
                        IsVertical: false,
                        Span: totalBars - 1,
                        RSquared: 1.0));
                }

                return new HoughTransformResult(
                    detectedLines,
                    Array.Empty<HoughChannel>(),
                    pointCount,
                    rhoBins,
                    thetaBins);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(accumulator);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(normX);
            ArrayPool<double>.Shared.Return(normY);
        }
    }

    /// <summary>
    /// Detects parallel channel structures from pairs of extracted Hough lines.
    /// </summary>
    public static IReadOnlyList<HoughChannel> DetectChannels(IReadOnlyList<HoughDetectedLine> lines, double atr)
    {
        if (lines == null || lines.Count < 2) return Array.Empty<HoughChannel>();

        var channels = new List<HoughChannel>();
        for (int i = 0; i < lines.Count; i++)
        {
            for (int j = i + 1; j < lines.Count; j++)
            {
                var l1 = lines[i];
                var l2 = lines[j];

                // Check angle/slope similarity (parallelism)
                double slopeDiff = Math.Abs(l1.Slope - l2.Slope);
                double maxAllowedDiff = Math.Max(0.05 * atr, 0.1 * Math.Max(Math.Abs(l1.Slope), Math.Abs(l2.Slope)));

                if (slopeDiff <= maxAllowedDiff)
                {
                    // Parallel pair found
                    HoughDetectedLine upper = l1.StartPrice > l2.StartPrice ? l1 : l2;
                    HoughDetectedLine lower = l1.StartPrice > l2.StartPrice ? l2 : l1;
                    decimal width = upper.StartPrice - lower.StartPrice;

                    // Ensure channel width is statistically meaningful (at least 0.1 * ATR)
                    decimal minChannelWidth = (decimal)(Math.Max(0.01, atr) * 0.1);
                    if (width >= minChannelWidth)
                    {
                        double avgSlope = (l1.Slope + l2.Slope) * 0.5;
                        double channelScore = Math.Min(1.0, (l1.Strength + l2.Strength) * 0.5 * Math.Max(0.5, (l1.RSquared + l2.RSquared) * 0.5));
                        channels.Add(new HoughChannel(
                            upper,
                            lower,
                            width,
                            avgSlope,
                            channelScore));
                    }
                }
            }
        }

        return channels;
    }
}
