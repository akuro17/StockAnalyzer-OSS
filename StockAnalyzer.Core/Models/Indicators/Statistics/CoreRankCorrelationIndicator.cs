using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Cross-Ticker Rank Correlation Indicator.
/// Computes non-parametric Spearman rho or Kendall tau-b correlation between two price series
/// or primary price vs volume over rolling windows with mid-rank tie handling.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.RankCorrelation)]
public class CoreRankCorrelationIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private int _period = IndicatorDefaultConstants.RankCorrelationDefaultPeriod;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private RankCorrelationMethod _method = RankCorrelationMethod.Spearman;

    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period
    {
        get => _period;
        set => _period = value;
    }

    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => _comparisonSymbol = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => _comparisonPriceSource = value;
    }

    public RankCorrelationMethod Method
    {
        get => _method;
        set => _method = value;
    }

    public override string Name
    {
        get
        {
            string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol) ? ComparisonSymbol : "Volume";
            return $"RankCorr({Period}, {sym}, {Method})";
        }
    }

    public override bool IsOverlay => false;

    public IReadOnlyList<decimal?> RankCorrelation => _values;

    public CoreRankCorrelationIndicator()
    {
    }

    public CoreRankCorrelationIndicator(int period, string comparisonSymbol = "", RankCorrelationMethod method = RankCorrelationMethod.Spearman)
    {
        _period = period;
        _comparisonSymbol = comparisonSymbol;
        _method = method;
    }

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRankCorrelationParameter p)
        {
            Period = p.Period;
            ComparisonSymbol = p.ComparisonSymbol;
            PriceSource = p.PriceSource;
            ComparisonPriceSource = p.ComparisonPriceSource;
            Method = p.Method;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = candles.Count;
        _values.Capacity = count;

        if (count < Period)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        var primaryPrices = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        IReadOnlyList<decimal?> secondarySeries;

        if (string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            // Branch A: Fallback to Primary Price vs Primary Volume
            var volumeSeries = new List<decimal?>(count);
            for (int i = 0; i < count; i++)
            {
                volumeSeries.Add(candles[i].Volume);
            }
            secondarySeries = volumeSeries;
        }
        else if (_secondaryCandles == null || _secondaryCandles.Count == 0)
        {
            // Branch B: Comparison symbol requested but not loaded -> emit nulls
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }
        else
        {
            // Branch C: Date-aligned comparison symbol series
            secondarySeries = PriceDataHelper.ExtractDateAlignedPriceSeries(candles, _secondaryCandles, ComparisonPriceSource);
        }

        Span<double> windowX = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> windowY = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> rankX = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> rankY = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];

        for (int i = 0; i < count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                continue;
            }

            int startIndex = i - Period + 1;
            bool loadedX = PriceDataHelper.TryLoadWindow(primaryPrices, startIndex, windowX);
            bool loadedY = PriceDataHelper.TryLoadWindow(secondarySeries, startIndex, windowY);

            if (!loadedX || !loadedY)
            {
                _values.Add(null);
                continue;
            }

            double corr = Method == RankCorrelationMethod.Spearman
                ? RankCorrelationEngine.CalculateSpearman(windowX, windowY, rankX, rankY)
                : RankCorrelationEngine.CalculateKendallTauB(windowX, windowY);

            if (!double.IsFinite(corr))
            {
                _values.Add(null);
            }
            else
            {
                decimal corrDec = (decimal)Math.Round(Math.Clamp(corr, -1.0, 1.0), 8, MidpointRounding.AwayFromZero);
                _values.Add(corrDec);
            }
        }

        return IndicatorResult.Success(_values);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = series.Count;
        _values.Capacity = count;

        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
        }

        return IndicatorResult.Success(_values);
    }
}
