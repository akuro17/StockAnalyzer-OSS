using System.Buffers;
using System.Globalization;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Pure static Core SSoT calculator for <c>DistributionRange</c> empirical statistics, histogram binning,
/// Gaussian kernel density estimation, and parametric reference PDF fitting via <see cref="DslProbabilityDistributionsMath"/>.
/// </summary>
public static class DistributionRangeCalculator
{
    private const double EulerMascheroni = 0.5772156649015328606;
    private const double GevShapeThreshold = 1e-8;
    private const double SilvermanIqrScale = 1.34;
    private static readonly double InverseSqrtTwoPi = 1.0 / Math.Sqrt(2.0 * Math.PI);

    /// <summary>
    /// Computes distribution statistics from the subset of ordered <paramref name="candles"/> whose
    /// timestamps fall inside [<paramref name="startTime"/>, <paramref name="endTime"/>] inclusive.
    /// Strict closed-range contract: bars strictly before <c>min(startTime, endTime)</c> are never accessed.
    /// </summary>
    public static DistributionRangeResult Calculate(
        IReadOnlyList<CoreCandleData> candles,
        DateTime startTime,
        DateTime endTime,
        DistributionRangeParameters? parameters = null)
    {
        if (candles == null || candles.Count == 0)
        {
            return DistributionRangeResult.Empty;
        }

        DateTime tStart = startTime <= endTime ? startTime : endTime;
        DateTime tEnd = startTime <= endTime ? endTime : startTime;
        if (!TryGetRangeIndices(candles, tStart, tEnd, out int startIdx, out int endIdx))
        {
            return DistributionRangeResult.Empty;
        }

        return CalculateRangeSlice(candles, startIdx, endIdx - startIdx + 1, parameters);
    }

    /// <summary>
    /// Computes distribution statistics directly over <paramref name="rangeCandles"/> (all supplied bars).
    /// </summary>
    public static DistributionRangeResult Calculate(
        IReadOnlyList<CoreCandleData> rangeCandles,
        DistributionRangeParameters? parameters = null)
    {
        if (rangeCandles == null || rangeCandles.Count == 0)
        {
            return DistributionRangeResult.Empty;
        }

        return CalculateRangeSlice(rangeCandles, 0, rangeCandles.Count, parameters);
    }

    private static DistributionRangeResult CalculateRangeSlice(
        IReadOnlyList<CoreCandleData> candles,
        int startIndex,
        int barCount,
        DistributionRangeParameters? parameters)
    {
        var p = (parameters ?? DistributionRangeParameters.Default).Normalize();
        int maxSamples = p.DataMode == DistributionDataMode.Price ? barCount : Math.Max(0, barCount - 1);
        if (maxSamples <= 0)
        {
            return DistributionRangeResult.Empty;
        }

        double[]? rentedSamples = null;
        double[]? rentedScratch = null;
        try
        {
            Span<double> sampleBuffer = maxSamples <= MathBufferLimits.StackAllocThreshold
                ? stackalloc double[maxSamples]
                : (rentedSamples = ArrayPool<double>.Shared.Rent(maxSamples)).AsSpan(0, maxSamples);

            int n = ExtractSamples(candles, startIndex, barCount, p.DataMode, p.PriceSource, sampleBuffer);
            if (n == 0)
            {
                return DistributionRangeResult.Empty;
            }

            Span<double> samples = sampleBuffer[..n];
            decimal? singleMean = IndicatorCalculationHelper.ToDecimalOrNull(samples[0]);
            if (n == 1)
            {
                return new DistributionRangeResult
                {
                    SampleCount = 1,
                    Mean = singleMean,
                    Median = singleMean,
                    MinValue = singleMean,
                    MaxValue = singleMean,
                    ReferenceStatus = ReferenceFitStatus.InsufficientData,
                    ReferenceParameterSummary = "Insufficient data"
                };
            }

            // Compute chronological moments BEFORE any in-place sorting in OrderStatisticsMath.
            FlatWindowRule flatRule = p.DataMode == DistributionDataMode.Price
                ? FlatWindowRule.Series
                : FlatWindowRule.Returns;
            bool momentsOk = WindowMoments.TryCompute(samples, flatRule, out WindowMoments moments);

            FindMinMax(samples, out double minVal, out double maxVal);
            decimal? minDec = IndicatorCalculationHelper.ToDecimalOrNull(minVal);
            decimal? maxDec = IndicatorCalculationHelper.ToDecimalOrNull(maxVal);

            // Copy to scratch before destructive OrderStatisticsMath operations.
            Span<double> orderScratch = n <= MathBufferLimits.StackAllocThreshold
                ? stackalloc double[n]
                : (rentedScratch = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);
            samples.CopyTo(orderScratch);

            double? medianOpt = OrderStatisticsMath.Median(orderScratch);
            samples.CopyTo(orderScratch);
            double? iqrOpt = OrderStatisticsMath.InterquartileRange(orderScratch);
            double medianVal = medianOpt ?? moments.Mean;
            double iqrVal = iqrOpt ?? double.NaN;

            decimal? meanDec = IndicatorCalculationHelper.ToDecimalOrNull(moments.Mean);
            decimal? stdDevDec = IndicatorCalculationHelper.ToDecimalOrNull(moments.SampleStandardDeviation);
            decimal? medianDec = medianOpt.HasValue ? IndicatorCalculationHelper.ToDecimalOrNull(medianOpt.Value) : null;
            decimal? iqrDec = iqrOpt.HasValue ? IndicatorCalculationHelper.ToDecimalOrNull(iqrOpt.Value) : null;
            decimal? skewnessDec = n >= 3 && momentsOk
                ? IndicatorCalculationHelper.ToDecimalOrNull(moments.SampleSkewness)
                : null;
            decimal? excessKurtosisDec = n >= 4 && momentsOk
                ? IndicatorCalculationHelper.ToDecimalOrNull(moments.SampleExcessKurtosis)
                : null;

            bool rangeCollapsed = !double.IsFinite(minVal) || !double.IsFinite(maxVal) || maxVal <= minVal
                || minDec is null || maxDec is null;
            if (!momentsOk || moments.IsFlat || rangeCollapsed
                || !double.IsFinite(moments.SampleStandardDeviation) || moments.SampleStandardDeviation <= 0.0)
            {
                return new DistributionRangeResult
                {
                    SampleCount = n,
                    Mean = meanDec ?? singleMean,
                    StdDev = stdDevDec ?? 0m,
                    Median = medianDec ?? singleMean,
                    Iqr = iqrDec ?? 0m,
                    Skewness = null,
                    ExcessKurtosis = null,
                    MinValue = minDec,
                    MaxValue = maxDec,
                    ReferenceStatus = ReferenceFitStatus.FlatSeries,
                    ReferenceParameterSummary = "Flat series"
                };
            }

            int binCount = ResolveBinCount(p, n, minVal, maxVal, moments.SampleStandardDeviation, iqrVal);
            double binWidth = (maxVal - minVal) / binCount;
            decimal? binWidthDec = IndicatorCalculationHelper.ToDecimalOrNull(binWidth);
            if (!double.IsFinite(binWidth) || binWidth <= 0.0 || binWidthDec is null || binWidthDec.Value <= 0m)
            {
                return new DistributionRangeResult
                {
                    SampleCount = n,
                    Mean = meanDec,
                    StdDev = stdDevDec,
                    Median = medianDec,
                    Iqr = iqrDec,
                    Skewness = skewnessDec,
                    ExcessKurtosis = excessKurtosisDec,
                    MinValue = minDec,
                    MaxValue = maxDec,
                    ReferenceStatus = ReferenceFitStatus.FlatSeries,
                    ReferenceParameterSummary = "Flat series"
                };
            }

            decimal minDecVal = minDec!.Value;
            decimal maxDecVal = maxDec!.Value;
            var bins = BuildBins(samples, n, minVal, maxVal, minDecVal, maxDecVal, binCount, binWidth);
            var kdeCurve = p.IncludeKde
                ? BuildKdeCurve(samples, n, minVal, maxVal, minDecVal, maxDecVal, moments.SampleStandardDeviation, iqrVal, p.BandwidthMultiplier)
                : Array.Empty<DistributionCurvePoint>();

            var (refStatus, refSummary, refCurve) = BuildReferenceCurve(
                samples, orderScratch, n, minVal, maxVal, minDecVal, maxDecVal, in moments, medianVal, iqrVal, p);

            return new DistributionRangeResult
            {
                SampleCount = n,
                Mean = meanDec,
                StdDev = stdDevDec,
                Median = medianDec,
                Iqr = iqrDec,
                Skewness = skewnessDec,
                ExcessKurtosis = excessKurtosisDec,
                MinValue = minDec,
                MaxValue = maxDec,
                BinWidth = binWidthDec,
                Bins = bins,
                KdeCurve = kdeCurve,
                ReferenceCurve = refCurve,
                ReferenceStatus = refStatus,
                ReferenceParameterSummary = refSummary
            };
        }
        finally
        {
            if (rentedSamples is not null) ArrayPool<double>.Shared.Return(rentedSamples);
            if (rentedScratch is not null) ArrayPool<double>.Shared.Return(rentedScratch);
        }
    }

    private static bool TryGetRangeIndices(
        IReadOnlyList<CoreCandleData> candles,
        DateTime startTime,
        DateTime endTime,
        out int startIndex,
        out int endIndex)
    {
        int lo = 0;
        int hi = candles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (candles[mid].Timestamp < startTime) lo = mid + 1;
            else hi = mid;
        }
        startIndex = lo;

        lo = 0;
        hi = candles.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (candles[mid].Timestamp <= endTime) lo = mid + 1;
            else hi = mid;
        }
        endIndex = lo - 1;

        return startIndex <= endIndex;
    }

    private static int ExtractSamples(
        IReadOnlyList<CoreCandleData> candles,
        int startIndex,
        int barCount,
        DistributionDataMode mode,
        PriceType priceSource,
        Span<double> destination)
    {
        var walker = new RangePriceWalker(priceSource);
        bool hasPrevPrice = false;
        double prevPrice = 0.0;
        int count = 0;

        for (int i = 0; i < barCount; i++)
        {
            CoreCandleData candle = candles[startIndex + i];
            decimal priceDec = walker.Next(in candle);

            double price = (double)priceDec;
            if (!double.IsFinite(price))
            {
                hasPrevPrice = false;
                continue;
            }

            if (mode == DistributionDataMode.Price)
            {
                destination[count++] = price;
                continue;
            }

            bool currentValidForLog = mode != DistributionDataMode.LogReturn || price > 0.0;
            if (!currentValidForLog)
            {
                hasPrevPrice = false;
                continue;
            }

            if (hasPrevPrice)
            {
                bool validPair = mode == DistributionDataMode.LogReturn
                    ? (prevPrice > 0.0 && price > 0.0)
                    : (prevPrice != 0.0);

                if (validPair)
                {
                    double ret = mode == DistributionDataMode.LogReturn
                        ? Math.Log(price / prevPrice)
                        : (price - prevPrice) / prevPrice;

                    if (double.IsFinite(ret))
                    {
                        destination[count++] = ret;
                    }
                }
            }

            prevPrice = price;
            hasPrevPrice = mode == DistributionDataMode.LogReturn ? price > 0.0 : price != 0.0;
        }

        return count;
    }

    private static void FindMinMax(ReadOnlySpan<double> values, out double min, out double max)
    {
        min = values[0];
        max = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            double v = values[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
    }

    private static int ResolveBinCount(
        DistributionRangeParameters parameters,
        int n,
        double minVal,
        double maxVal,
        double stdDev,
        double iqr)
    {
        int sturges = (int)Math.Ceiling(Math.Log2(n) + 1.0);
        double range = maxVal - minVal;
        int raw = parameters.BinningRule switch
        {
            DistributionBinningRule.Sturges => sturges,
            DistributionBinningRule.FixedCount => parameters.FixedBinCount,
            DistributionBinningRule.FreedmanDiaconis => ComputeWidthBasedBinCount(range, 2.0 * iqr * Math.Pow(n, -1.0 / 3.0), sturges),
            DistributionBinningRule.Scott => ComputeWidthBasedBinCount(range, 3.5 * stdDev * Math.Pow(n, -1.0 / 3.0), sturges),
            _ => sturges
        };

        return Math.Clamp(raw, DistributionRangeParameters.MinFixedBinCount, DistributionRangeParameters.MaxFixedBinCount);
    }

    private static int ComputeWidthBasedBinCount(double range, double h, int fallback)
    {
        if (!double.IsFinite(h) || h <= 0.0) return fallback;
        double ratio = range / h;
        if (!double.IsFinite(ratio) || ratio <= 0.0) return fallback;
        double ceiled = Math.Ceiling(ratio);
        if (ceiled >= DistributionRangeParameters.MaxFixedBinCount) return DistributionRangeParameters.MaxFixedBinCount;
        return Math.Max(1, (int)ceiled);
    }

    private static DistributionBin[] BuildBins(
        ReadOnlySpan<double> samples,
        int n,
        double minVal,
        double maxVal,
        decimal minDec,
        decimal maxDec,
        int binCount,
        double binWidth)
    {
        Span<int> counts = stackalloc int[binCount];
        counts.Clear();

        for (int i = 0; i < n; i++)
        {
            int idx = (int)((samples[i] - minVal) / binWidth);
            if (idx < 0) idx = 0;
            else if (idx >= binCount) idx = binCount - 1;
            counts[idx]++;
        }

        double denom = n * binWidth;
        double maxDensity = 0.0;
        Span<double> densities = stackalloc double[binCount];
        for (int j = 0; j < binCount; j++)
        {
            double d = counts[j] / denom;
            densities[j] = d;
            if (d > maxDensity) maxDensity = d;
        }

        var bins = new DistributionBin[binCount];
        for (int j = 0; j < binCount; j++)
        {
            decimal lower = j == 0
                ? minDec
                : (IndicatorCalculationHelper.ToDecimalOrNull(minVal + j * binWidth) ?? minDec);
            decimal upper = j == binCount - 1
                ? maxDec
                : (IndicatorCalculationHelper.ToDecimalOrNull(minVal + (j + 1) * binWidth) ?? maxDec);
            decimal densityDec = IndicatorCalculationHelper.ToDecimalOrNull(densities[j]) ?? 0m;
            decimal normDec = maxDensity > 0.0
                ? (IndicatorCalculationHelper.ToDecimalOrNull(densities[j] / maxDensity) ?? 0m)
                : 0m;
            bins[j] = new DistributionBin(lower, upper, counts[j], densityDec, normDec);
        }

        return bins;
    }

    private static DistributionCurvePoint[] BuildKdeCurve(
        ReadOnlySpan<double> samples,
        int n,
        double minVal,
        double maxVal,
        decimal minDec,
        decimal maxDec,
        double stdDev,
        double iqr,
        double bwMultiplier)
    {
        double sEff = (double.IsFinite(iqr) && iqr > 0.0)
            ? Math.Min(stdDev, iqr / SilvermanIqrScale)
            : stdDev;
        if (!double.IsFinite(sEff) || sEff <= 0.0) sEff = stdDev;

        double bandwidth = 0.9 * sEff * Math.Pow(n, -0.2) * bwMultiplier;
        if (!double.IsFinite(bandwidth) || bandwidth <= 0.0)
        {
            return Array.Empty<DistributionCurvePoint>();
        }

        int pointCount = DistributionRangeParameters.CurveSamplePoints;
        var curve = new DistributionCurvePoint[pointCount];
        double span = maxVal - minVal;
        double norm = InverseSqrtTwoPi / (n * bandwidth);
        double invBandwidth = 1.0 / bandwidth;

        for (int g = 0; g < pointCount; g++)
        {
            double x = g == 0
                ? minVal
                : (g == pointCount - 1 ? maxVal : minVal + span * (g / (double)(pointCount - 1)));

            double kernelSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double z = (x - samples[i]) * invBandwidth;
                kernelSum += Math.Exp(-0.5 * z * z);
            }

            double density = kernelSum * norm;
            decimal xDec = g == 0
                ? minDec
                : (g == pointCount - 1 ? maxDec : (IndicatorCalculationHelper.ToDecimalOrNull(x) ?? minDec));
            decimal dDec = double.IsFinite(density) && density > 0.0
                ? (IndicatorCalculationHelper.ToDecimalOrNull(density) ?? 0m)
                : 0m;
            curve[g] = new DistributionCurvePoint(xDec, dDec);
        }

        return curve;
    }

    private static (ReferenceFitStatus Status, string Summary, DistributionCurvePoint[] Curve) BuildReferenceCurve(
        ReadOnlySpan<double> samples,
        Span<double> orderScratch,
        int n,
        double minVal,
        double maxVal,
        decimal minDec,
        decimal maxDec,
        in WindowMoments moments,
        double medianVal,
        double iqrVal,
        DistributionRangeParameters p)
    {
        if (p.ReferenceDistribution == ReferenceDistributionType.None)
        {
            return (ReferenceFitStatus.None, "None", Array.Empty<DistributionCurvePoint>());
        }

        // Guard strictly positive domains (D-6 = A)
        if (p.ReferenceDistribution == ReferenceDistributionType.Exponential)
        {
            if (minVal < 0.0 || moments.Mean <= 0.0)
            {
                return (ReferenceFitStatus.DomainViolation, "Domain N/A (x >= 0 required)", Array.Empty<DistributionCurvePoint>());
            }
        }
        else if (DistributionRangeParameters.IsPositiveOnlyDistribution(p.ReferenceDistribution) && minVal <= 0.0)
        {
            return (ReferenceFitStatus.DomainViolation, "Domain N/A (x > 0 required)", Array.Empty<DistributionCurvePoint>());
        }

        CultureInfo culture = DrawingValueFormat.Culture;
        double mean = moments.Mean;
        double stdDev = moments.SampleStandardDeviation;
        string fmt = p.DataMode == DistributionDataMode.Price ? "F2" : "F5";

        switch (p.ReferenceDistribution)
        {
            case ReferenceDistributionType.Normal:
            {
                decimal? mu = IndicatorCalculationHelper.ToDecimalOrNull(mean);
                decimal? sigma = IndicatorCalculationHelper.ToDecimalOrNull(stdDev);
                if (mu is null || sigma is null || sigma.Value <= 0m)
                {
                    return FlatResult();
                }

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.NormPdf(x, mu.Value, sigma.Value));
                return (ReferenceFitStatus.Fitted, $"Normal (μ={mu.Value.ToString(fmt, culture)}, σ={sigma.Value.ToString(fmt, culture)})", curve);
            }

            case ReferenceDistributionType.StudentT:
            {
                decimal? mu = IndicatorCalculationHelper.ToDecimalOrNull(mean);
                if (mu is null) return FlatResult();

                decimal nu;
                if (p.OverrideStudentTDegreesOfFreedom)
                {
                    nu = p.StudentTDegreesOfFreedom;
                }
                else
                {
                    double rawNu = (n >= 4 && double.IsFinite(moments.SampleExcessKurtosis) && moments.SampleExcessKurtosis > 0.0)
                        ? 6.0 / moments.SampleExcessKurtosis + 4.0
                        : (double)DistributionRangeParameters.DefaultFallbackStudentTDegreesOfFreedom;
                    decimal? nuDec = IndicatorCalculationHelper.ToDecimalOrNull(rawNu);
                    nu = nuDec is null
                        ? DistributionRangeParameters.DefaultFallbackStudentTDegreesOfFreedom
                        : DistributionRangeParameters.ClampStudentTDegreesOfFreedom(nuDec.Value);
                }

                double scaleDouble = stdDev * Math.Sqrt(((double)nu - 2.0) / (double)nu);
                decimal? scale = IndicatorCalculationHelper.ToDecimalOrNull(scaleDouble);
                if (scale is null || scale.Value <= 0m) return FlatResult();

                decimal sVal = scale.Value;
                decimal mVal = mu.Value;
                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x =>
                {
                    var pdf = DslProbabilityDistributionsMath.StudentTPdf((x - mVal) / sVal, nu);
                    return pdf.IsOk ? MathResult<decimal>.Ok(pdf.Value / sVal) : pdf;
                });
                return (ReferenceFitStatus.Fitted, $"Student-t (df={nu.ToString("F1", culture)})", curve);
            }

            case ReferenceDistributionType.Laplace:
            {
                double bSum = 0.0;
                for (int i = 0; i < n; i++) bSum += Math.Abs(samples[i] - medianVal);
                double bDouble = bSum / n;
                if (!double.IsFinite(bDouble) || bDouble <= 0.0) bDouble = stdDev / Math.Sqrt(2.0);

                decimal? mu = IndicatorCalculationHelper.ToDecimalOrNull(medianVal);
                decimal? b = IndicatorCalculationHelper.ToDecimalOrNull(bDouble);
                if (mu is null || b is null || b.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.LaplacePdf(x, mu.Value, b.Value));
                return (ReferenceFitStatus.Fitted, $"Laplace (μ={mu.Value.ToString(fmt, culture)}, b={b.Value.ToString(fmt, culture)})", curve);
            }

            case ReferenceDistributionType.Logistic:
            {
                decimal? mu = IndicatorCalculationHelper.ToDecimalOrNull(mean);
                double sDouble = stdDev * Math.Sqrt(3.0) / Math.PI;
                decimal? s = IndicatorCalculationHelper.ToDecimalOrNull(sDouble);
                if (mu is null || s is null || s.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.LogisticPdf(x, mu.Value, s.Value));
                return (ReferenceFitStatus.Fitted, $"Logistic (μ={mu.Value.ToString(fmt, culture)}, s={s.Value.ToString(fmt, culture)})", curve);
            }

            case ReferenceDistributionType.Cauchy:
            {
                double gammaDouble = iqrVal / 2.0;
                if (!double.IsFinite(gammaDouble) || gammaDouble <= 0.0)
                {
                    samples.CopyTo(orderScratch);
                    double mad = ComputeMad(orderScratch);
                    gammaDouble = mad * OrderStatisticsMath.MadToSigma;
                }
                if (!double.IsFinite(gammaDouble) || gammaDouble <= 0.0)
                {
                    gammaDouble = stdDev;
                }

                decimal? x0 = IndicatorCalculationHelper.ToDecimalOrNull(medianVal);
                decimal? gamma = IndicatorCalculationHelper.ToDecimalOrNull(gammaDouble);
                if (x0 is null || gamma is null || gamma.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.CauchyPdf(x, x0.Value, gamma.Value));
                return (ReferenceFitStatus.Fitted, $"Cauchy (x0={x0.Value.ToString(fmt, culture)}, γ={gamma.Value.ToString(fmt, culture)})", curve);
            }

            case ReferenceDistributionType.Gev:
            {
                decimal xi = p.GevShapeParameter;
                double xiDouble = (double)xi;
                double muDouble;
                double sigmaDouble;

                if (Math.Abs(xiDouble) < GevShapeThreshold)
                {
                    sigmaDouble = stdDev * Math.Sqrt(6.0) / Math.PI;
                    muDouble = mean - EulerMascheroni * sigmaDouble;
                }
                else
                {
                    double g1 = Math.Exp(DslSpecialFunctionsMath.LogAbsGammaDouble(1.0 - xiDouble, out int _));
                    double g2 = Math.Exp(DslSpecialFunctionsMath.LogAbsGammaDouble(1.0 - 2.0 * xiDouble, out int _));
                    double varFactor = g2 - g1 * g1;
                    if (!double.IsFinite(varFactor) || varFactor <= 0.0) return FlatResult();
                    sigmaDouble = stdDev * Math.Abs(xiDouble) / Math.Sqrt(varFactor);
                    muDouble = mean - sigmaDouble * (g1 - 1.0) / xiDouble;
                }

                decimal? mu = IndicatorCalculationHelper.ToDecimalOrNull(muDouble);
                decimal? sigma = IndicatorCalculationHelper.ToDecimalOrNull(sigmaDouble);
                if (mu is null || sigma is null || sigma.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.GevPdf(x, mu.Value, sigma.Value, xi));
                return (ReferenceFitStatus.Fitted, $"GEV (ξ={xi.ToString("F2", culture)}, μ={mu.Value.ToString(fmt, culture)}, σ={sigma.Value.ToString(fmt, culture)})", curve);
            }

            case ReferenceDistributionType.Uniform:
            {
                if (maxDec - minDec < 1e-12m) return FlatResult();
                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.UnifPdf(x, minDec, maxDec));
                return (ReferenceFitStatus.Fitted, $"Uniform ([{minDec.ToString(fmt, culture)}, {maxDec.ToString(fmt, culture)}])", curve);
            }

            case ReferenceDistributionType.LogNormal:
            {
                if (!TryComputeLogNormalParams(samples, out double muLog, out double sigmaLog)) return FlatResult();
                decimal? muY = IndicatorCalculationHelper.ToDecimalOrNull(muLog);
                decimal? sigmaY = IndicatorCalculationHelper.ToDecimalOrNull(sigmaLog);
                if (muY is null || sigmaY is null || sigmaY.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.LogNormPdf(x, muY.Value, sigmaY.Value));
                return (ReferenceFitStatus.Fitted, $"LogNormal (μ={muY.Value.ToString("F3", culture)}, σ={sigmaY.Value.ToString("F3", culture)})", curve);
            }

            case ReferenceDistributionType.Gamma:
            {
                double var = stdDev * stdDev;
                double alphaDouble = (mean * mean) / var;
                double betaDouble = mean / var;
                decimal? alpha = IndicatorCalculationHelper.ToDecimalOrNull(alphaDouble);
                decimal? beta = IndicatorCalculationHelper.ToDecimalOrNull(betaDouble);
                if (alpha is null || beta is null || alpha.Value <= 0m || beta.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.GammaPdf(x, alpha.Value, beta.Value));
                return (ReferenceFitStatus.Fitted, $"Gamma (α={alpha.Value.ToString("F2", culture)}, β={beta.Value.ToString("F3", culture)})", curve);
            }

            case ReferenceDistributionType.Weibull:
            {
                double cv = stdDev / mean;
                if (!double.IsFinite(cv) || cv <= 0.0) return FlatResult();
                double kDouble = Math.Pow(cv, -1.086);
                double gammaTerm = Math.Exp(DslSpecialFunctionsMath.LogAbsGammaDouble(1.0 + 1.0 / kDouble, out int _));
                double lambdaDouble = mean / gammaTerm;

                decimal? k = IndicatorCalculationHelper.ToDecimalOrNull(kDouble);
                decimal? lambda = IndicatorCalculationHelper.ToDecimalOrNull(lambdaDouble);
                if (k is null || lambda is null || k.Value <= 0m || lambda.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.WeibullPdf(x, k.Value, lambda.Value));
                return (ReferenceFitStatus.Fitted, $"Weibull (k={k.Value.ToString("F2", culture)}, λ={lambda.Value.ToString(fmt, culture)})", curve);
            }

            case ReferenceDistributionType.Exponential:
            {
                decimal? lambda = IndicatorCalculationHelper.ToDecimalOrNull(1.0 / mean);
                if (lambda is null || lambda.Value <= 0m) return FlatResult();

                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x => DslProbabilityDistributionsMath.ExpPdf(x, lambda.Value));
                return (ReferenceFitStatus.Fitted, $"Exponential (λ={lambda.Value.ToString("F3", culture)})", curve);
            }

            case ReferenceDistributionType.Pareto:
            {
                double sumLog = 0.0;
                for (int i = 0; i < n; i++) sumLog += Math.Log(samples[i] / minVal);
                if (!double.IsFinite(sumLog) || sumLog <= 0.0) return FlatResult();

                double alphaDouble = n / sumLog;
                decimal xm = minDec;
                decimal? alpha = IndicatorCalculationHelper.ToDecimalOrNull(alphaDouble);
                if (xm <= 0m || alpha is null || alpha.Value <= 0m) return FlatResult();

                // Left-endpoint & rounding guard (D-6 = A): gridX[0] == xm and xEval >= xm.
                var curve = EvaluatePdfGrid(minVal, maxVal, minDec, maxDec, x =>
                {
                    decimal xSafe = x < xm ? xm : x;
                    return DslProbabilityDistributionsMath.ParetoPdf(xSafe, xm, alpha.Value);
                });
                return (ReferenceFitStatus.Fitted, $"Pareto (xm={xm.ToString(fmt, culture)}, α={alpha.Value.ToString("F2", culture)})", curve);
            }

            default:
                return (ReferenceFitStatus.None, "None", Array.Empty<DistributionCurvePoint>());
        }
    }

    private static (ReferenceFitStatus Status, string Summary, DistributionCurvePoint[] Curve) FlatResult()
        => (ReferenceFitStatus.FlatSeries, "Flat series", Array.Empty<DistributionCurvePoint>());

    private static DistributionCurvePoint[] EvaluatePdfGrid(
        double minVal,
        double maxVal,
        decimal minDec,
        decimal maxDec,
        Func<decimal, MathResult<decimal>> pdfEvaluator)
    {
        int pointCount = DistributionRangeParameters.CurveSamplePoints;
        var curve = new DistributionCurvePoint[pointCount];
        double span = maxVal - minVal;

        for (int g = 0; g < pointCount; g++)
        {
            decimal xDec = g == 0
                ? minDec
                : (g == pointCount - 1
                    ? maxDec
                    : (IndicatorCalculationHelper.ToDecimalOrNull(minVal + span * (g / (double)(pointCount - 1))) ?? minDec));

            if (xDec < minDec) xDec = minDec;
            if (xDec > maxDec) xDec = maxDec;

            decimal density = 0m;
            try
            {
                MathResult<decimal> eval = pdfEvaluator(xDec);
                if (eval.IsOk && eval.Value > 0m)
                {
                    density = eval.Value;
                }
            }
            catch (OverflowException)
            {
                density = 0m;
            }
            catch (DivideByZeroException)
            {
                density = 0m;
            }

            curve[g] = new DistributionCurvePoint(xDec, density);
        }

        return curve;
    }

    private static double ComputeMad(Span<double> scratch)
    {
        int n = scratch.Length;
        double[]? rented = null;
        try
        {
            Span<double> madScratch = n <= MathBufferLimits.StackAllocThreshold
                ? stackalloc double[n]
                : (rented = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);
            return OrderStatisticsMath.MedianAbsoluteDeviation(scratch, madScratch) ?? double.NaN;
        }
        finally
        {
            if (rented is not null) ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static bool TryComputeLogNormalParams(ReadOnlySpan<double> samples, out double muLog, out double sigmaLog)
    {
        int n = samples.Length;
        double[]? rented = null;
        try
        {
            Span<double> logs = n <= MathBufferLimits.StackAllocThreshold
                ? stackalloc double[n]
                : (rented = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

            for (int i = 0; i < n; i++)
            {
                logs[i] = Math.Log(samples[i]);
            }

            if (!WindowMoments.TryCompute(logs, FlatWindowRule.Returns, out WindowMoments lm))
            {
                muLog = 0.0;
                sigmaLog = 0.0;
                return false;
            }

            muLog = lm.Mean;
            sigmaLog = lm.SampleStandardDeviation;
            return double.IsFinite(muLog) && double.IsFinite(sigmaLog) && sigmaLog > 0.0;
        }
        finally
        {
            if (rented is not null) ArrayPool<double>.Shared.Return(rented);
        }
    }
}
