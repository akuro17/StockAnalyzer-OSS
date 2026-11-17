using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Bivariate Johansen (1988) VECM Cointegration test indicator.
/// <c>Main</c> outputs the Trace statistic; companion series expose <c>MaxEigenStat</c>,
/// <c>CriticalValue95</c>, and <c>CointegrationSpreadZScore</c>.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.JohansenCointegration)]
public class CoreJohansenCointegrationIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    public const string MaxEigenStatSeriesName = nameof(MaxEigenStat);
    public const string CriticalValue95SeriesName = nameof(CriticalValue95);
    public const string CointegrationSpreadZScoreSeriesName = nameof(CointegrationSpreadZScore);

    private string _comparisonSymbol = string.Empty;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period { get; set; } = IndicatorDefaultConstants.JohansenDefaultPeriod;

    public bool UseLogPrices { get; set; } = true;

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
            return $"Johansen Cointegration ({Period}, {sym})";
        }
    }

    public override bool IsOverlay => false;

    public List<decimal?> MaxEigenStat { get; } = new();
    public List<decimal?> CriticalValue95 { get; } = new();
    public List<decimal?> CointegrationSpreadZScore { get; } = new();

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreJohansenCointegrationParameter p)
        {
            p.Validate();
            Period = p.Period;
            UseLogPrices = p.UseLogPrices;
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

        var validation = new CoreJohansenCointegrationParameter
        {
            Period = Period,
            UseLogPrices = UseLogPrices,
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

        JohansenMath.Compute(
            seriesA,
            seriesB,
            Period,
            UseLogPrices,
            _values,
            MaxEigenStat,
            CriticalValue95,
            CointegrationSpreadZScore);

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
        MaxEigenStat.Clear();
        CriticalValue95.Clear();
        CointegrationSpreadZScore.Clear();
    }

    private IIndicatorResult FillNulls(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
            MaxEigenStat.Add(null);
            CriticalValue95.Add(null);
            CointegrationSpreadZScore.Add(null);
        }
        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary() => new()
    {
        { IndicatorResult.MainSeriesName, _values },
        { MaxEigenStatSeriesName, MaxEigenStat },
        { CriticalValue95SeriesName, CriticalValue95 },
        { CointegrationSpreadZScoreSeriesName, CointegrationSpreadZScore }
    };
}
