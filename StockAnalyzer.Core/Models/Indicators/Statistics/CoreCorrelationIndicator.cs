using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

[StockAnalyzerIndicator(IndicatorType.Correlation)]
public class CoreCorrelationIndicator : CoreIndicatorBase
{
    private int _period = IndicatorDefaultConstants.CorrelationPeriod;
    private readonly IReadOnlyList<decimal?>? _explicitSeriesB;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period
    {
        get => _period;
        set => _period = value;
    }

    public string ComparisonSymbol { get; set; } = string.Empty;
    public PriceType ComparisonPriceSource { get; set; } = PriceType.Close;
    public CorrelationCalculationMode CalculationMode { get; set; } = CorrelationCalculationMode.PriceLevel;

    public override string Name
    {
        get
        {
            string modeSuffix = CalculationMode == CorrelationCalculationMode.LogReturn ? ", Return" : string.Empty;
            return !string.IsNullOrWhiteSpace(ComparisonSymbol)
                ? $"Correlation({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix})"
                : (CalculationMode == CorrelationCalculationMode.LogReturn ? $"Correlation({Period}, Return)" : $"Correlation({Period})");
        }
    }

    public override bool IsOverlay => false;

    public CoreCorrelationIndicator()
    {
        _period = IndicatorDefaultConstants.CorrelationPeriod;
    }

    public CoreCorrelationIndicator(int period)
    {
        _period = period;
    }

    public CoreCorrelationIndicator(int period, IEnumerable<CoreCandleData> seriesB)
    {
        _period = period;
        _explicitSeriesB = seriesB?.Select(c => (decimal?)c.Close).ToList();
    }

    public CoreCorrelationIndicator(int period, IEnumerable<decimal> seriesB)
    {
        _period = period;
        _explicitSeriesB = seriesB?.Select(v => (decimal?)v).ToList();
    }

    public CoreCorrelationIndicator(int period, IEnumerable<decimal?> seriesB)
    {
        _period = period;
        _explicitSeriesB = seriesB?.ToList();
    }

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }


    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreCorrelationParameter corrParam)
        {
            Period = corrParam.Period;
            ComparisonSymbol = corrParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = corrParam.ComparisonPriceSource;
            CalculationMode = corrParam.CalculationMode;
        }
        else if (parameters is CoreSmaParameter smaParam)
        {
            Period = smaParam.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        var seriesA = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        IReadOnlyList<decimal?> seriesB;

        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            if (_secondaryCandles != null && _secondaryCandles.Count > 0)
            {
                // Cross-ticker comparison: Primary Price vs Secondary Price
                seriesB = PriceDataHelper.ExtractPriceSeries(_secondaryCandles, ComparisonPriceSource);
            }
            else if (_explicitSeriesB != null)
            {
                seriesB = _explicitSeriesB;
            }
            else
            {
                // Comparison symbol specified but secondary data not yet loaded/available:
                // Return nulls (do not fall back to Price vs Volume silently)
                _values = new List<decimal?>(candles.Count);
                for (int i = 0; i < candles.Count; i++)
                {
                    _values.Add(null);
                }
                return IndicatorResult.Success(_values);
            }
        }
        else if (_explicitSeriesB != null)
        {
            seriesB = _explicitSeriesB;
        }
        else
        {
            // Default when single candle series is provided (ComparisonSymbol is empty): Price vs Volume correlation
            var volumeSeries = new List<decimal?>(candles.Count);
            for (int i = 0; i < candles.Count; i++)
            {
                volumeSeries.Add(candles[i].Volume);
            }
            seriesB = volumeSeries;
        }

        IReadOnlyList<decimal?> calcSeriesA = seriesA;
        IReadOnlyList<decimal?> calcSeriesB = seriesB;

        if (CalculationMode == CorrelationCalculationMode.LogReturn)
        {
            calcSeriesA = IndicatorCalculationHelper.ConvertToLogReturns(seriesA);
            calcSeriesB = IndicatorCalculationHelper.ConvertToLogReturns(seriesB);
        }

        if (calcSeriesA.Count == calcSeriesB.Count)
        {
            _values = IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(calcSeriesA, calcSeriesB, Period);
        }
        else
        {
            int commonCount = Math.Min(calcSeriesA.Count, calcSeriesB.Count);
            var subA = new List<decimal?>(commonCount);
            var subB = new List<decimal?>(commonCount);
            for (int i = 0; i < commonCount; i++)
            {
                subA.Add(calcSeriesA[i]);
                subB.Add(calcSeriesB[i]);
            }
            var commonResults = IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(subA, subB, Period);
            _values = new List<decimal?>(calcSeriesA.Count);
            _values.AddRange(commonResults);
            for (int i = commonCount; i < calcSeriesA.Count; i++)
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }



    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        IReadOnlyList<decimal?>? seriesB = _explicitSeriesB ?? dynamicPeriods;
        if (seriesB == null)
        {
            _values = new List<decimal?>(series.Count);
            for (int i = 0; i < series.Count; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        if (series.Count == seriesB.Count)
        {
            _values = IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(series, seriesB, Period);
        }
        else
        {
            int commonCount = Math.Min(series.Count, seriesB.Count);
            var subA = new List<decimal?>(commonCount);
            var subB = new List<decimal?>(commonCount);
            for (int i = 0; i < commonCount; i++)
            {
                subA.Add(series[i]);
                subB.Add(seriesB[i]);
            }
            var commonResults = IndicatorCalculationHelper.CalculateRollingPearsonCorrelation(subA, subB, Period);
            _values = new List<decimal?>(series.Count);
            _values.AddRange(commonResults);
            for (int i = commonCount; i < series.Count; i++)
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}

