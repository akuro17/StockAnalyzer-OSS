using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Cointegration Spread Indicator.
/// Calculates the rolling OLS cointegration spread: Spread_t = Y_t - (alpha_t + beta_t * X_t),
/// where Y is the primary asset and X is the comparison asset.
/// Also provides Spread Z-Score (sample standard deviation), Hedge Ratio (Beta),
/// Intercept (Alpha), and ADF Stationarity Test Statistic.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.CointegrationSpread)]
public class CoreCointegrationSpreadIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private int _period = IndicatorDefaultConstants.CointegrationSpreadPeriod;
    private int _zScorePeriod = IndicatorDefaultConstants.CointegrationSpreadZScorePeriod;
    private readonly IReadOnlyList<decimal?>? _explicitSeriesB;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period
    {
        get => _period;
        set => _period = value;
    }

    public int ZScorePeriod
    {
        get => _zScorePeriod;
        set => _zScorePeriod = value;
    }

    public string ComparisonSymbol { get; set; } = string.Empty;
    public PriceType ComparisonPriceSource { get; set; } = PriceType.Close;
    public CointegrationPriceMode PriceMode { get; set; } = CointegrationPriceMode.PriceLevel;

    public override string Name
    {
        get
        {
            string modeSuffix = PriceMode == CointegrationPriceMode.LogPrice ? ", Log" : string.Empty;
            return !string.IsNullOrWhiteSpace(ComparisonSymbol)
                ? $"Cointegration Spread({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix}, Z:{ZScorePeriod})"
                : $"Cointegration Spread({Period}{modeSuffix}, Z:{ZScorePeriod})";
        }
    }

    public override bool IsOverlay => false;

    // ── Output Series ──
    public List<decimal?> ZScore { get; } = new();
    public List<decimal?> HedgeRatio { get; } = new();
    public List<decimal?> Intercept { get; } = new();
    public List<decimal?> ADFStatistic { get; } = new();

    // ── Series Name Constants ──
    public const string ZScoreSeriesName = "ZScore";
    public const string HedgeRatioSeriesName = "HedgeRatio";
    public const string InterceptSeriesName = "Intercept";
    public const string ADFStatisticSeriesName = "ADFStatistic";

    // ── Constructors ──
    public CoreCointegrationSpreadIndicator()
    {
        _period = IndicatorDefaultConstants.CointegrationSpreadPeriod;
        _zScorePeriod = IndicatorDefaultConstants.CointegrationSpreadZScorePeriod;
    }

    public CoreCointegrationSpreadIndicator(int period)
    {
        _period = period;
        _zScorePeriod = IndicatorDefaultConstants.CointegrationSpreadZScorePeriod;
    }

    public CoreCointegrationSpreadIndicator(int period, int zScorePeriod)
    {
        _period = period;
        _zScorePeriod = zScorePeriod;
    }

    public CoreCointegrationSpreadIndicator(int period, IEnumerable<CoreCandleData> comparisonSeries)
    {
        _period = period;
        _zScorePeriod = IndicatorDefaultConstants.CointegrationSpreadZScorePeriod;
        _explicitSeriesB = comparisonSeries?.Select(c => (decimal?)c.Close).ToList();
    }

    public CoreCointegrationSpreadIndicator(int period, IEnumerable<decimal?> comparisonSeries)
    {
        _period = period;
        _zScorePeriod = IndicatorDefaultConstants.CointegrationSpreadZScorePeriod;
        _explicitSeriesB = comparisonSeries?.ToList();
    }

    public CoreCointegrationSpreadIndicator(int period, int zScorePeriod, IEnumerable<CoreCandleData> comparisonSeries)
    {
        _period = period;
        _zScorePeriod = zScorePeriod;
        _explicitSeriesB = comparisonSeries?.Select(c => (decimal?)c.Close).ToList();
    }

    public CoreCointegrationSpreadIndicator(int period, int zScorePeriod, IEnumerable<decimal?> comparisonSeries)
    {
        _period = period;
        _zScorePeriod = zScorePeriod;
        _explicitSeriesB = comparisonSeries?.ToList();
    }

    public CoreCointegrationSpreadIndicator(int period, int zScorePeriod, CointegrationPriceMode priceMode, IEnumerable<decimal?> comparisonSeries)
    {
        _period = period;
        _zScorePeriod = zScorePeriod;
        PriceMode = priceMode;
        _explicitSeriesB = comparisonSeries?.ToList();
    }

    // ── ICrossTickerIndicator ──
    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    // ── Configuration ──
    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreCointegrationSpreadParameter spreadParam)
        {
            Period = spreadParam.Period;
            ZScorePeriod = spreadParam.ZScorePeriod;
            ComparisonSymbol = spreadParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = spreadParam.ComparisonPriceSource;
            PriceMode = spreadParam.PriceMode;
        }
        else if (parameters is CoreSmaParameter smaParam)
        {
            Period = smaParam.Period;
        }
    }

    // ── Calculation ──
    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        ClearAllSeries();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        IReadOnlyList<decimal?> seriesA;
        IReadOnlyList<decimal?> seriesB;

        // Resolve comparison series with date-based alignment
        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            if (_secondaryCandles != null && _secondaryCandles.Count > 0)
            {
                // Strict Timestamp Date Alignment: match secondary candles to primary candles by calendar date
                var secondaryByDate = new Dictionary<DateTime, CoreCandleData>();
                for (int i = 0; i < _secondaryCandles.Count; i++)
                {
                    var sc = _secondaryCandles[i];
                    if (sc != null)
                    {
                        secondaryByDate[sc.Timestamp.Date] = sc;
                    }
                }

                var matchedA = new List<decimal?>(candles.Count);
                var matchedB = new List<decimal?>(candles.Count);

                for (int i = 0; i < candles.Count; i++)
                {
                    var candleA = candles[i];
                    matchedA.Add(PriceDataHelper.ExtractPrice(candleA, PriceSource));

                    if (secondaryByDate.TryGetValue(candleA.Timestamp.Date, out var candleB))
                    {
                        matchedB.Add(PriceDataHelper.ExtractPrice(candleB, ComparisonPriceSource));
                    }
                    else
                    {
                        // Holiday or trading halt in secondary symbol -> null prevents erroneous cross-date regression
                        matchedB.Add(null);
                    }
                }

                seriesA = matchedA;
                seriesB = matchedB;
            }
            else if (_explicitSeriesB != null)
            {
                seriesA = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
                seriesB = _explicitSeriesB;
            }
            else
            {
                // Comparison symbol specified but secondary data not yet loaded
                return FillNulls(candles.Count);
            }
        }
        else if (_explicitSeriesB != null)
        {
            seriesA = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            seriesB = _explicitSeriesB;
        }
        else
        {
            // Comparison symbol not specified: Cointegration Spread is undefined without a pair asset.
            return FillNulls(candles.Count);
        }

        return ComputeSpread(seriesA, seriesB);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        ClearAllSeries();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        IReadOnlyList<decimal?>? seriesB = _explicitSeriesB ?? dynamicPeriods;
        if (seriesB == null)
        {
            return FillNulls(series.Count);
        }

        return ComputeSpread(series, seriesB);
    }

    private IIndicatorResult ComputeSpread(IReadOnlyList<decimal?> seriesA, IReadOnlyList<decimal?> seriesB)
    {
        // Align series lengths from end if needed
        IReadOnlyList<decimal?> alignedA = seriesA;
        IReadOnlyList<decimal?> alignedB = seriesB;
        int outputCount = seriesA.Count;

        if (seriesA.Count != seriesB.Count)
        {
            int commonCount = Math.Min(seriesA.Count, seriesB.Count);
            int offsetA = seriesA.Count - commonCount;
            int offsetB = seriesB.Count - commonCount;

            var subA = new List<decimal?>(seriesA.Count);
            var subB = new List<decimal?>(seriesA.Count);

            for (int i = 0; i < seriesA.Count; i++)
            {
                subA.Add(seriesA[i]);
                if (i < offsetA)
                {
                    subB.Add(null);
                }
                else
                {
                    int bIdx = i - offsetA + offsetB;
                    subB.Add(bIdx < seriesB.Count ? seriesB[bIdx] : null);
                }
            }
            alignedA = subA;
            alignedB = subB;
        }

        // Apply PriceMode transformation (LogPrice vs PriceLevel)
        IReadOnlyList<decimal?> calcSeriesA = alignedA;
        IReadOnlyList<decimal?> calcSeriesB = alignedB;

        if (PriceMode == CointegrationPriceMode.LogPrice)
        {
            var logA = new List<decimal?>(alignedA.Count);
            var logB = new List<decimal?>(alignedB.Count);

            for (int i = 0; i < alignedA.Count; i++)
            {
                if (alignedA[i].HasValue && alignedA[i]!.Value > 0m)
                {
                    logA.Add((decimal)Math.Log((double)alignedA[i]!.Value));
                }
                else
                {
                    logA.Add(null);
                }

                if (alignedB[i].HasValue && alignedB[i]!.Value > 0m)
                {
                    logB.Add((decimal)Math.Log((double)alignedB[i]!.Value));
                }
                else
                {
                    logB.Add(null);
                }
            }

            calcSeriesA = logA;
            calcSeriesB = logB;
        }

        // Calculate Rolling Beta stats (calcSeriesA = Y, calcSeriesB = X)
        var (betaValues, alphaValues, _) = IndicatorCalculationHelper.CalculateRollingBetaStats(calcSeriesA, calcSeriesB, Period);

        // Step 1: Calculate Spread series: Spread_t = Y_t - alpha_t - beta_t * X_t
        var spreadValues = new List<decimal?>(calcSeriesA.Count);
        for (int t = 0; t < calcSeriesA.Count; t++)
        {
            if (!betaValues[t].HasValue || !alphaValues[t].HasValue || !calcSeriesA[t].HasValue || !calcSeriesB[t].HasValue)
            {
                spreadValues.Add(null);
                continue;
            }

            try
            {
                checked
                {
                    decimal spread = calcSeriesA[t]!.Value - alphaValues[t]!.Value - (betaValues[t]!.Value * calcSeriesB[t]!.Value);
                    spreadValues.Add(spread);
                }
            }
            catch (OverflowException)
            {
                spreadValues.Add(null);
            }
        }

        // Step 2: Calculate Z-Score using Sample Standard Deviation (Bessel's correction: N - 1)
        var zScoreValues = new List<decimal?>(spreadValues.Count);
        for (int t = 0; t < spreadValues.Count; t++)
        {
            if (t < ZScorePeriod - 1 || ZScorePeriod < 2)
            {
                zScoreValues.Add(null);
                continue;
            }

            bool hasNull = false;
            decimal sum = 0m;
            for (int j = t - ZScorePeriod + 1; j <= t; j++)
            {
                if (!spreadValues[j].HasValue)
                {
                    hasNull = true;
                    break;
                }
                sum += spreadValues[j]!.Value;
            }

            if (hasNull)
            {
                zScoreValues.Add(null);
                continue;
            }

            decimal mean = sum / ZScorePeriod;
            decimal sumSqDiff = 0m;
            for (int j = t - ZScorePeriod + 1; j <= t; j++)
            {
                decimal diff = spreadValues[j]!.Value - mean;
                sumSqDiff += diff * diff;
            }

            // Unbiased sample variance: divide by (ZScorePeriod - 1)
            double variance = (double)(sumSqDiff / (ZScorePeriod - 1));
            decimal stdDev = (decimal)Math.Sqrt(variance);

            // If stdDev is zero or near zero, Z-Score is mathematically undefined -> output null
            if (stdDev < IndicatorDefaultConstants.CointegrationSpreadEpsilon)
            {
                zScoreValues.Add(null);
            }
            else
            {
                try
                {
                    checked
                    {
                        decimal z = (spreadValues[t]!.Value - mean) / stdDev;
                        zScoreValues.Add(z);
                    }
                }
                catch (OverflowException)
                {
                    zScoreValues.Add(null);
                }
            }
        }

        // Step 3: Calculate Rolling Augmented Dickey-Fuller (ADF) Test Statistic for Spread Stationarity
        var adfValues = new List<decimal?>(spreadValues.Count);
        for (int t = 0; t < spreadValues.Count; t++)
        {
            if (t < Period - 1)
            {
                adfValues.Add(null);
                continue;
            }

            adfValues.Add(ComputeAdfStatistic(spreadValues, t, Period));
        }

        _values.AddRange(spreadValues);
        ZScore.AddRange(zScoreValues);
        HedgeRatio.AddRange(betaValues);
        Intercept.AddRange(alphaValues);
        ADFStatistic.AddRange(adfValues);

        // Pad with nulls if original seriesA was longer
        for (int i = spreadValues.Count; i < outputCount; i++)
        {
            _values.Add(null);
            ZScore.Add(null);
            HedgeRatio.Add(null);
            Intercept.Add(null);
            ADFStatistic.Add(null);
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    /// <summary>
    /// Computes Dickey-Fuller test statistic (tau value) for residual stationarity over window [endIndex - windowSize + 1, endIndex].
    /// Model: Delta e_t = rho * e_{t-1} + u_t
    /// </summary>
    private static decimal? ComputeAdfStatistic(IReadOnlyList<decimal?> spreadSeries, int endIndex, int windowSize)
    {
        int startIndex = endIndex - windowSize + 1;
        if (startIndex < 0) return null;

        for (int i = startIndex; i <= endIndex; i++)
        {
            if (!spreadSeries[i].HasValue) return null;
        }

        int m = windowSize - 1;
        if (m < 3) return null; // Need degrees of freedom

        try
        {
            checked
            {
                decimal sumXX = 0m;
                decimal sumXY = 0m;
                for (int k = startIndex + 1; k <= endIndex; k++)
                {
                    decimal x = spreadSeries[k - 1]!.Value;
                    decimal y = spreadSeries[k]!.Value - x;
                    sumXX += x * x;
                    sumXY += x * y;
                }

                if (sumXX < IndicatorDefaultConstants.CointegrationSpreadEpsilon)
                {
                    return null;
                }

                decimal rho = sumXY / sumXX;

                // Sum of squared residuals
                decimal sumU2 = 0m;
                for (int k = startIndex + 1; k <= endIndex; k++)
                {
                    decimal x = spreadSeries[k - 1]!.Value;
                    decimal y = spreadSeries[k]!.Value - x;
                    decimal u = y - (rho * x);
                    sumU2 += u * u;
                }

                if (m <= 1) return null;
                decimal s2 = sumU2 / (m - 1);
                double seDouble = Math.Sqrt((double)(s2 / sumXX));
                if (seDouble <= 0.0 || double.IsNaN(seDouble) || double.IsInfinity(seDouble))
                {
                    return null;
                }

                decimal se = (decimal)seDouble;
                if (se < IndicatorDefaultConstants.CointegrationSpreadEpsilon)
                {
                    return null;
                }

                return rho / se;
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private void ClearAllSeries()
    {
        _values.Clear();
        ZScore.Clear();
        HedgeRatio.Clear();
        Intercept.Clear();
        ADFStatistic.Clear();
    }

    private IIndicatorResult FillNulls(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
            ZScore.Add(null);
            HedgeRatio.Add(null);
            Intercept.Add(null);
            ADFStatistic.Add(null);
        }
        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary()
    {
        return new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { ZScoreSeriesName, ZScore },
            { HedgeRatioSeriesName, HedgeRatio },
            { InterceptSeriesName, Intercept },
            { ADFStatisticSeriesName, ADFStatistic }
        };
    }
}
