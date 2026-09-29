using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Rolling Beta Indicator.
/// Calculates β = Cov(R_asset, R_bench) / Var(R_bench), α = y_bar - β * x_bar,
/// and ρ = Cov / (σ_asset * σ_bench) over a rolling window using centered statistics.
/// Requires a benchmark symbol via ICrossTickerIndicator.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RollingBeta)]
public class CoreRollingBetaIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private int _period = IndicatorDefaultConstants.RollingBetaPeriod;
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
                ? $"Rolling Beta({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix})"
                : $"Rolling Beta({Period}{modeSuffix})";
        }
    }

    public override bool IsOverlay => false;

    // ── Series ──
    public List<decimal?> RollingCorrelation { get; } = new();

    // ── Series Name Constants ──
    public const string CorrelationSeriesName = "RollingCorrelation";

    // ── Constructors ──
    public CoreRollingBetaIndicator()
    {
        _period = IndicatorDefaultConstants.RollingBetaPeriod;
    }

    public CoreRollingBetaIndicator(int period)
    {
        _period = period;
    }

    public CoreRollingBetaIndicator(int period, IEnumerable<CoreCandleData> benchmarkSeries)
    {
        _period = period;
        _explicitSeriesB = benchmarkSeries?.Select(c => (decimal?)c.Close).ToList();
    }

    public CoreRollingBetaIndicator(int period, IEnumerable<decimal?> benchmarkSeries)
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
        if (parameters is CoreRollingBetaParameter betaParam)
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
        RollingCorrelation.Clear();

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
            // Comparison symbol not specified: Beta is undefined without a benchmark.
            // Return nulls (do not fall back to Volume).
            return FillNulls(candles.Count);
        }

        return ComputeBeta(seriesA, seriesB);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        RollingCorrelation.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        IReadOnlyList<decimal?>? seriesB = _explicitSeriesB ?? dynamicPeriods;
        if (seriesB == null)
        {
            return FillNulls(series.Count);
        }

        return ComputeBeta(series, seriesB);
    }

    private IIndicatorResult ComputeBeta(IReadOnlyList<decimal?> seriesA, IReadOnlyList<decimal?> seriesB)
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

        // Calculate Rolling Beta and Correlation using centered algorithm
        var (betaValues, _, corrValues) = IndicatorCalculationHelper.CalculateRollingBetaStats(returnsA, returnsB, Period);

        _values.AddRange(betaValues);
        RollingCorrelation.AddRange(corrValues);

        // Pad with nulls if original seriesA was longer
        for (int i = betaValues.Count; i < outputCount; i++)
        {
            _values.Add(null);
            RollingCorrelation.Add(null);
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private IIndicatorResult FillNulls(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
            RollingCorrelation.Add(null);
        }
        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary()
    {
        return new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { CorrelationSeriesName, RollingCorrelation }
        };
    }
}
