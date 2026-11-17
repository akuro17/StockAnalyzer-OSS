using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Bivariate Granger Causality F-Test &amp; Engle (2002) DCC(1,1) indicator.
/// <c>Main</c> outputs F(Comparison -&gt; Primary); companion series expose <c>ReverseFStat</c>,
/// <c>DirectionalDiff</c>, and <c>DccCorrelation</c> (1-step-ahead conditional correlation Q_{t|t-1}
/// predicted for bar t using standardized innovations up to t-1, distinct from post-innovation Q_{t+1|t}).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.GrangerCausality)]
public class CoreGrangerCausalityIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    public const string ReverseFStatSeriesName = nameof(ReverseFStat);
    public const string DirectionalDiffSeriesName = nameof(DirectionalDiff);
    public const string DccCorrelationSeriesName = nameof(DccCorrelation);

    private string _comparisonSymbol = string.Empty;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period { get; set; } = IndicatorDefaultConstants.GrangerCausalityDefaultPeriod;

    public int LagOrder { get; set; } = IndicatorDefaultConstants.GrangerCausalityDefaultLagOrder;

    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => _comparisonSymbol = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    public PriceType ComparisonPriceSource { get; set; } = PriceType.Close;

    public override string Name
    {
        get
        {
            string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol) ? ComparisonSymbol : "Benchmark";
            return $"Granger Causality ({Period}, L{LagOrder}, {sym})";
        }
    }

    public override bool IsOverlay => false;

    public List<decimal?> ReverseFStat { get; } = new();
    public List<decimal?> DirectionalDiff { get; } = new();
    public List<decimal?> DccCorrelation { get; } = new();

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreGrangerCausalityParameter p)
        {
            p.Validate();
            Period = p.Period;
            LagOrder = p.LagOrder;
            ComparisonSymbol = p.ComparisonSymbol;
            PriceSource = p.PriceSource;
            ComparisonPriceSource = p.ComparisonPriceSource;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        ClearSeries();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        var validation = new CoreGrangerCausalityParameter
        {
            Period = Period,
            LagOrder = LagOrder,
            ComparisonSymbol = ComparisonSymbol,
            PriceSource = PriceSource,
            ComparisonPriceSource = ComparisonPriceSource
        };

        try
        {
            validation.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return IndicatorResult.Failure(ex.Message);
        }

        if (_secondaryCandles == null || _secondaryCandles.Count == 0)
        {
            return FillNulls(candles.Count);
        }

        IReadOnlyList<decimal?> seriesA = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        IReadOnlyList<decimal?> seriesB = PriceDataHelper.ExtractDateAlignedPriceSeries(candles, _secondaryCandles, ComparisonPriceSource);

        GrangerDccMath.Compute(
            seriesA,
            seriesB,
            Period,
            LagOrder,
            _values,
            ReverseFStat,
            DirectionalDiff,
            DccCorrelation);

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        ClearSeries();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        return FillNulls(series.Count);
    }

    private void ClearSeries()
    {
        _values.Clear();
        ReverseFStat.Clear();
        DirectionalDiff.Clear();
        DccCorrelation.Clear();
    }

    private IIndicatorResult FillNulls(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
            ReverseFStat.Add(null);
            DirectionalDiff.Add(null);
            DccCorrelation.Add(null);
        }
        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary() => new()
    {
        { IndicatorResult.MainSeriesName, _values },
        { ReverseFStatSeriesName, ReverseFStat },
        { DirectionalDiffSeriesName, DirectionalDiff },
        { DccCorrelationSeriesName, DccCorrelation }
    };
}
