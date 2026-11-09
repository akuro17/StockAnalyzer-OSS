using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Cross-Ticker Dynamic Time Warping (DTW) Indicator.
/// Computes Z-normalized shape similarity and warping distance between the primary symbol
/// and a comparison benchmark symbol over rolling windows.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.CrossTickerDtw)]
public class CoreCrossTickerDtwIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private int _period = IndicatorDefaultConstants.CrossTickerDtwDefaultPeriod;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private int _warpingRadius = IndicatorDefaultConstants.CrossTickerDtwDefaultWarpingRadius;
    private DtwOutputMode _outputMode = DtwOutputMode.Distance;

    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public const string NormalizedDistanceSeriesName = "NormalizedDistance";
    public const string SimilarityScoreSeriesName = "SimilarityScore";

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

    public int WarpingRadius
    {
        get => _warpingRadius;
        set => _warpingRadius = value;
    }

    public DtwOutputMode OutputMode
    {
        get => _outputMode;
        set => _outputMode = value;
    }

    public override string Name
    {
        get
        {
            string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol) ? ComparisonSymbol : "Benchmark";
            string modeStr = OutputMode == DtwOutputMode.Similarity ? "Sim" : "Dist";
            return $"DTW({Period}, {sym}, {modeStr})";
        }
    }

    public override bool IsOverlay => false;

    public List<decimal?> NormalizedDistance { get; } = new();
    public List<decimal?> SimilarityScore { get; } = new();

    public CoreCrossTickerDtwIndicator()
    {
    }

    public CoreCrossTickerDtwIndicator(int period, string comparisonSymbol = "")
    {
        _period = period;
        _comparisonSymbol = comparisonSymbol;
    }

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreCrossTickerDtwParameter p)
        {
            Period = p.Period;
            ComparisonSymbol = p.ComparisonSymbol;
            PriceSource = p.PriceSource;
            ComparisonPriceSource = p.ComparisonPriceSource;
            WarpingRadius = p.WarpingRadius;
            OutputMode = p.OutputMode;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        NormalizedDistance.Clear();
        SimilarityScore.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = candles.Count;
        _values.Capacity = count;
        NormalizedDistance.Capacity = count;
        SimilarityScore.Capacity = count;

        if (count < Period || string.IsNullOrWhiteSpace(ComparisonSymbol) || _secondaryCandles == null || _secondaryCandles.Count == 0)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
                NormalizedDistance.Add(null);
                SimilarityScore.Add(null);
            }
            return IndicatorResult.Success(CreateSeriesDictionary());
        }

        var primaryPrices = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        var secondaryPrices = PriceDataHelper.ExtractDateAlignedPriceSeries(candles, _secondaryCandles, ComparisonPriceSource);

        Span<double> windowX = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> windowY = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> normX = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> normY = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];

        double invSqrtPeriod = 1.0 / Math.Sqrt(Period);

        for (int i = 0; i < count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                NormalizedDistance.Add(null);
                SimilarityScore.Add(null);
                continue;
            }

            int startIndex = i - Period + 1;
            bool loadedX = PriceDataHelper.TryLoadWindow(primaryPrices, startIndex, windowX);
            bool loadedY = PriceDataHelper.TryLoadWindow(secondaryPrices, startIndex, windowY);

            if (!loadedX || !loadedY)
            {
                _values.Add(null);
                NormalizedDistance.Add(null);
                SimilarityScore.Add(null);
                continue;
            }

            bool normOkX = ZNormalization.TryNormalize(windowX, normX);
            bool normOkY = ZNormalization.TryNormalize(windowY, normY);

            if (!normOkX && !normOkY)
            {
                // Both windows flat: identical constant shape
                NormalizedDistance.Add(0.0m);
                SimilarityScore.Add(1.0m);
                _values.Add(OutputMode == DtwOutputMode.Distance ? 0.0m : 1.0m);
                continue;
            }

            if (!normOkX || !normOkY)
            {
                // Exactly one window flat: shape comparison undefined
                _values.Add(null);
                NormalizedDistance.Add(null);
                SimilarityScore.Add(null);
                continue;
            }

            double rawDistance = DtwMath.Calculate(normX, normY, WarpingRadius);
            if (!double.IsFinite(rawDistance) || rawDistance < 0.0)
            {
                _values.Add(null);
                NormalizedDistance.Add(null);
                SimilarityScore.Add(null);
                continue;
            }

            double dNorm = rawDistance * invSqrtPeriod;
            double sim = 1.0 / (1.0 + dNorm);

            decimal dNormDec = (decimal)Math.Round(dNorm, 8, MidpointRounding.AwayFromZero);
            decimal simDec = (decimal)Math.Round(Math.Clamp(sim, 0.0, 1.0), 8, MidpointRounding.AwayFromZero);

            NormalizedDistance.Add(dNormDec);
            SimilarityScore.Add(simDec);
            _values.Add(OutputMode == DtwOutputMode.Distance ? dNormDec : simDec);
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        // Standalone series calculation without candles: return nulls
        _values.Clear();
        NormalizedDistance.Clear();
        SimilarityScore.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        for (int i = 0; i < series.Count; i++)
        {
            _values.Add(null);
            NormalizedDistance.Add(null);
            SimilarityScore.Add(null);
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary()
    {
        return new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { NormalizedDistanceSeriesName, NormalizedDistance },
            { SimilarityScoreSeriesName, SimilarityScore }
        };
    }
}
