using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Rolling R-Squared (R²) Indicator (Sample Coefficient of Determination).
/// Calculates the proportion of variance in the target asset's returns explained
/// by the benchmark's returns under a single-variable OLS regression model with intercept:
/// R² = r², where r is Pearson correlation.
/// Value range is strictly bounded in [0.0, 1.0].
/// Outputs null during the warm-up period (t < Period) or when either asset or benchmark variance is zero (S_xx < 1e-12 or S_yy < 1e-12).
/// Requires a benchmark symbol via ICrossTickerIndicator.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RollingRSquared)]
public class CoreRollingRSquaredIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private int _period = IndicatorDefaultConstants.RollingRSquaredPeriod;
    private readonly IReadOnlyList<decimal?>? _explicitSeriesB;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period
    {
        get => _period;
        set => _period = value;
    }

    public string ComparisonSymbol { get; set; } = string.Empty;
    public PriceType ComparisonPriceSource { get; set; } = PriceType.Close;
    public BetaCalculationMode CalculationMode { get; set; } = BetaCalculationMode.SimpleReturn;

    public override string Name
    {
        get
        {
            string modeSuffix = CalculationMode == BetaCalculationMode.LogReturn ? ", Log" : string.Empty;
            return !string.IsNullOrWhiteSpace(ComparisonSymbol)
                ? $"Rolling R²({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix})"
                : $"Rolling R²({Period}{modeSuffix})";
        }
    }

    public override bool IsOverlay => false;

    // ── Constructors ──
    public CoreRollingRSquaredIndicator()
    {
        _period = IndicatorDefaultConstants.RollingRSquaredPeriod;
    }

    public CoreRollingRSquaredIndicator(int period)
    {
        _period = period;
    }

    public CoreRollingRSquaredIndicator(int period, IEnumerable<CoreCandleData> benchmarkSeries)
    {
        _period = period;
        _explicitSeriesB = benchmarkSeries?.Select(c => (decimal?)c.Close).ToList();
    }

    public CoreRollingRSquaredIndicator(int period, IEnumerable<decimal?> benchmarkSeries)
    {
        _period = period;
        _explicitSeriesB = benchmarkSeries?.ToList();
    }

    // ── ICrossTickerIndicator ──
    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    // ── Configuration ──
    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRollingRSquaredParameter rsqParam)
        {
            Period = rsqParam.Period;
            ComparisonSymbol = rsqParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = rsqParam.ComparisonPriceSource;
            CalculationMode = rsqParam.CalculationMode;
            PriceSource = rsqParam.PriceSource;
        }
        else if (parameters is CoreRollingBetaParameter betaParam)
        {
            Period = betaParam.Period;
            ComparisonSymbol = betaParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = betaParam.ComparisonPriceSource;
            CalculationMode = betaParam.CalculationMode;
        }
        else if (parameters is CoreRollingAlphaParameter alphaParam)
        {
            Period = alphaParam.Period;
            ComparisonSymbol = alphaParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = alphaParam.ComparisonPriceSource;
            CalculationMode = alphaParam.CalculationMode;
        }
        else if (parameters is CoreSmaParameter smaParam)
        {
            Period = smaParam.Period;
        }
    }

    // ── Calculation ──
    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        var seriesA = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        IReadOnlyList<decimal?> seriesB;

        // Resolve benchmark series
        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            if (_secondaryCandles != null && _secondaryCandles.Count > 0)
            {
                seriesB = PriceDataHelper.ExtractPriceSeries(_secondaryCandles, ComparisonPriceSource);
            }
            else if (_explicitSeriesB != null)
            {
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
            seriesB = _explicitSeriesB;
        }
        else
        {
            // Comparison symbol not specified: R² is undefined without a benchmark.
            return FillNulls(candles.Count);
        }

        return ComputeRSquared(seriesA, seriesB);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        IReadOnlyList<decimal?>? seriesB = _explicitSeriesB ?? dynamicPeriods;
        if (seriesB == null)
        {
            return FillNulls(series.Count);
        }

        return ComputeRSquared(series, seriesB);
    }

    private IIndicatorResult ComputeRSquared(IReadOnlyList<decimal?> seriesA, IReadOnlyList<decimal?> seriesB)
    {
        // Align series to seriesA length by aligning from the end (latest bars matched)
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
                    // Past bars beyond seriesB range
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

        // Convert to returns based on CalculationMode
        IReadOnlyList<decimal?> returnsA;
        IReadOnlyList<decimal?> returnsB;

        if (CalculationMode == BetaCalculationMode.LogReturn)
        {
            returnsA = IndicatorCalculationHelper.ConvertToLogReturns(alignedA);
            returnsB = IndicatorCalculationHelper.ConvertToLogReturns(alignedB);
        }
        else
        {
            returnsA = IndicatorCalculationHelper.ConvertToSimpleReturns(alignedA);
            returnsB = IndicatorCalculationHelper.ConvertToSimpleReturns(alignedB);
        }

        // Calculate Pearson correlation from Rolling Beta stats
        var (_, _, corrValues) = IndicatorCalculationHelper.CalculateRollingBetaStats(returnsA, returnsB, Period);

        for (int i = 0; i < corrValues.Count; i++)
        {
            decimal? r = corrValues[i];
            if (r.HasValue)
            {
                try
                {
                    checked
                    {
                        decimal rSq = r.Value * r.Value;
                        if (rSq < 0m) rSq = 0m;
                        if (rSq > 1m) rSq = 1m;
                        _values.Add(rSq);
                    }
                }
                catch (OverflowException)
                {
                    _values.Add(null);
                }
            }
            else
            {
                _values.Add(null);
            }
        }

        // Pad with nulls if original seriesA was longer
        for (int i = corrValues.Count; i < outputCount; i++)
        {
            _values.Add(null);
        }

        return IndicatorResult.Success(_values);
    }

    private IIndicatorResult FillNulls(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
        }
        return IndicatorResult.Success(_values);
    }
}
