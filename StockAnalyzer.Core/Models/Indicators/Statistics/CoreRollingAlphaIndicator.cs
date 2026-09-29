using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Rolling Alpha Indicator.
/// Calculates market model regression intercept α = y_bar - β * x_bar over a rolling window.
/// Optionally annualizes alpha via α_annual = α_daily * AnnualizationFactor.
/// Requires a benchmark symbol via ICrossTickerIndicator.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RollingAlpha)]
public class CoreRollingAlphaIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private int _period = IndicatorDefaultConstants.RollingAlphaPeriod;
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
    public bool Annualize { get; set; } = IndicatorDefaultConstants.RollingAlphaAnnualizeDefault;
    public int AnnualizationFactor { get; set; } = IndicatorDefaultConstants.RollingAlphaAnnualizationFactor;

    public override string Name
    {
        get
        {
            string modeSuffix = CalculationMode == BetaCalculationMode.LogReturn ? ", Log" : string.Empty;
            string annSuffix = Annualize ? ", Ann" : string.Empty;
            return !string.IsNullOrWhiteSpace(ComparisonSymbol)
                ? $"Rolling Alpha({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix}{annSuffix})"
                : $"Rolling Alpha({Period}{modeSuffix}{annSuffix})";
        }
    }

    public override bool IsOverlay => false;

    // ── Constructors ──
    public CoreRollingAlphaIndicator()
    {
        _period = IndicatorDefaultConstants.RollingAlphaPeriod;
    }

    public CoreRollingAlphaIndicator(int period)
    {
        _period = period;
    }

    public CoreRollingAlphaIndicator(int period, IEnumerable<CoreCandleData> benchmarkSeries)
    {
        _period = period;
        _explicitSeriesB = benchmarkSeries?.Select(c => (decimal?)c.Close).ToList();
    }

    public CoreRollingAlphaIndicator(int period, IEnumerable<decimal?> benchmarkSeries)
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
        if (parameters is CoreRollingAlphaParameter alphaParam)
        {
            Period = alphaParam.Period;
            ComparisonSymbol = alphaParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = alphaParam.ComparisonPriceSource;
            CalculationMode = alphaParam.CalculationMode;
            Annualize = alphaParam.Annualize;
            AnnualizationFactor = alphaParam.AnnualizationFactor;
        }
        else if (parameters is CoreRollingBetaParameter betaParam)
        {
            Period = betaParam.Period;
            ComparisonSymbol = betaParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = betaParam.ComparisonPriceSource;
            CalculationMode = betaParam.CalculationMode;
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
            // Comparison symbol not specified: Alpha is undefined without a benchmark.
            return FillNulls(candles.Count);
        }

        return ComputeAlpha(seriesA, seriesB);
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

        return ComputeAlpha(series, seriesB);
    }

    private IIndicatorResult ComputeAlpha(IReadOnlyList<decimal?> seriesA, IReadOnlyList<decimal?> seriesB)
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

        // Calculate Rolling Beta stats (Alpha is returned as daily intercept)
        var (_, alphaValues, _) = IndicatorCalculationHelper.CalculateRollingBetaStats(returnsA, returnsB, Period);

        decimal factor = (decimal)AnnualizationFactor;
        for (int i = 0; i < alphaValues.Count; i++)
        {
            decimal? val = alphaValues[i];
            if (val.HasValue && Annualize)
            {
                try
                {
                    checked
                    {
                        val = val.Value * factor;
                    }
                }
                catch (OverflowException)
                {
                    val = null;
                }
            }
            _values.Add(val);
        }

        // Pad with nulls if original seriesA was longer
        for (int i = alphaValues.Count; i < outputCount; i++)
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
