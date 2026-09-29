using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Statistics;

/// <summary>
/// Cross-Correlation Function (CCF) Indicator.
/// Computes causal rolling cross-correlation between two price/volume series at fixed or dynamic lags.
/// Exposes primary correlation oscillator values ([-1.0, 1.0]) and optional optimal lead-lag series.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.CrossCorrelation)]
public class CoreCrossCorrelationIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private int _period = IndicatorDefaultConstants.CrossCorrelationPeriod;
    private int _lag = IndicatorDefaultConstants.CrossCorrelationLag;
    private bool _enableDynamicLag = true;
    private int _minLag = IndicatorDefaultConstants.CrossCorrelationMinLag;
    private int _maxLag = IndicatorDefaultConstants.CrossCorrelationMaxLag;
    private int _lagSmoothingPeriod = IndicatorDefaultConstants.CrossCorrelationLagSmoothingPeriod;
    private double _threshold = IndicatorDefaultConstants.CrossCorrelationThreshold;
    private CrossCorrelationPeakSelectionMode _peakSelectionMode = CrossCorrelationPeakSelectionMode.MaxCorrelation;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private CorrelationCalculationMode _calculationMode = CorrelationCalculationMode.LogReturn;

    private readonly IReadOnlyList<decimal?>? _explicitSeriesB;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;

    public int Period
    {
        get => _period;
        set => _period = value;
    }

    public int Lag
    {
        get => _lag;
        set => _lag = value;
    }

    public bool EnableDynamicLag
    {
        get => _enableDynamicLag;
        set => _enableDynamicLag = value;
    }

    public int MinLag
    {
        get => _minLag;
        set => _minLag = value;
    }

    public int MaxLag
    {
        get => _maxLag;
        set => _maxLag = value;
    }

    public int LagSmoothingPeriod
    {
        get => _lagSmoothingPeriod;
        set => _lagSmoothingPeriod = value;
    }

    public double Threshold
    {
        get => _threshold;
        set => _threshold = value;
    }

    public CrossCorrelationPeakSelectionMode PeakSelectionMode
    {
        get => _peakSelectionMode;
        set => _peakSelectionMode = value;
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

    public CorrelationCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => _calculationMode = value;
    }

    public override string Name
    {
        get
        {
            string modeSuffix = CalculationMode == CorrelationCalculationMode.LogReturn ? ", Return" : string.Empty;
            string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol) ? ComparisonSymbol.Trim().ToUpperInvariant() : "Volume";

            if (EnableDynamicLag)
            {
                return $"CCF({Period}, {sym}, L[{MinLag}:{MaxLag}]{modeSuffix})";
            }

            return Lag != 0
                ? $"CCF({Period}, {sym}, Lag={Lag}{modeSuffix})"
                : $"CCF({Period}, {sym}{modeSuffix})";
        }
    }

    public override bool IsOverlay => false;

    // Series Names
    public const string CrossCorrelationSeriesName = IndicatorResult.MainSeriesName;
    public const string OptimalLagSeriesName = "OptimalLag";
    public const string PeakCorrelationSeriesName = "PeakCorrelation";

    public IReadOnlyList<decimal?> CrossCorrelation => _values;
    public List<decimal?> OptimalLag { get; } = new();

    public CoreCrossCorrelationIndicator()
    {
        _enableDynamicLag = true;
    }

    public CoreCrossCorrelationIndicator(int period, int lag = 0)
    {
        _period = period;
        _lag = lag;
        _enableDynamicLag = false;
    }

    public CoreCrossCorrelationIndicator(int period, IEnumerable<CoreCandleData> seriesB, int lag = 0)
    {
        _period = period;
        _lag = lag;
        _explicitSeriesB = seriesB?.Select(c => (decimal?)c.Close).ToList();
        _enableDynamicLag = false;
    }

    public CoreCrossCorrelationIndicator(int period, IEnumerable<decimal?> seriesB, int lag = 0)
    {
        _period = period;
        _lag = lag;
        _explicitSeriesB = seriesB?.ToList();
        _enableDynamicLag = false;
    }

    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreCrossCorrelationParameter p)
        {
            Period = p.Period;
            Lag = p.Lag;
            EnableDynamicLag = p.EnableDynamicLag;
            MinLag = p.MinLag;
            MaxLag = p.MaxLag;
            LagSmoothingPeriod = p.LagSmoothingPeriod;
            Threshold = p.Threshold;
            PeakSelectionMode = p.PeakSelectionMode;
            ComparisonSymbol = p.ComparisonSymbol;
            ComparisonPriceSource = p.ComparisonPriceSource;
            CalculationMode = p.CalculationMode;
            PriceSource = p.PriceSource;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        OptimalLag.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        var seriesA = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        IReadOnlyList<decimal?> seriesB;

        bool isVolumeSeries = false;
        if (_secondaryCandles != null && _secondaryCandles.Count > 0)
        {
            seriesB = PriceDataHelper.ExtractPriceSeries(_secondaryCandles, ComparisonPriceSource);
        }
        else if (_explicitSeriesB != null)
        {
            seriesB = _explicitSeriesB;
        }
        else if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            // Secondary symbol requested but candles not yet loaded: emit nulls
            for (int i = 0; i < candles.Count; i++)
            {
                _values.Add(null);
                OptimalLag.Add(null);
            }
            return IndicatorResult.Success(CreateSeriesDictionary());
        }
        else
        {
            // Default when no comparison symbol: Price vs Volume correlation
            var volumeSeries = new List<decimal?>(candles.Count);
            for (int i = 0; i < candles.Count; i++)
            {
                volumeSeries.Add(candles[i].Volume);
            }
            seriesB = volumeSeries;
            isVolumeSeries = true;
        }

        return ComputeCrossCorrelation(seriesA, seriesB, isVolumeSeries);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        OptimalLag.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        IReadOnlyList<decimal?>? seriesB = _explicitSeriesB ?? dynamicPeriods;
        if (seriesB == null)
        {
            for (int i = 0; i < series.Count; i++)
            {
                _values.Add(null);
                OptimalLag.Add(null);
            }
            return IndicatorResult.Success(CreateSeriesDictionary());
        }

        return ComputeCrossCorrelation(series, seriesB, isVolumeSeries: false);
    }

    private IIndicatorResult ComputeCrossCorrelation(IReadOnlyList<decimal?> seriesA, IReadOnlyList<decimal?> seriesB, bool isVolumeSeries = false)
    {
        IReadOnlyList<decimal?> calcSeriesA = seriesA;
        IReadOnlyList<decimal?> calcSeriesB = seriesB;

        if (CalculationMode == CorrelationCalculationMode.LogReturn)
        {
            calcSeriesA = IndicatorCalculationHelper.ConvertToLogReturns(seriesA);
            calcSeriesB = isVolumeSeries
                ? CrossCorrelationEngine.ConvertVolumeToLogDifferences(seriesB)
                : IndicatorCalculationHelper.ConvertToLogReturns(seriesB);
        }

        if (EnableDynamicLag)
        {
            var (peaks, lags) = CrossCorrelationEngine.CalculateOptimalLeadLag(
                calcSeriesA,
                calcSeriesB,
                Period,
                MinLag,
                MaxLag,
                Threshold,
                PeakSelectionMode,
                LagSmoothingPeriod);

            _values.AddRange(peaks);
            OptimalLag.AddRange(lags);
        }
        else
        {
            var ccf = CrossCorrelationEngine.CalculateRollingCcf(
                calcSeriesA,
                calcSeriesB,
                Period,
                Lag);

            _values.AddRange(ccf);

            // In fixed lag mode, do not emit a misleading static horizontal line of 0.
            // OptimalLag is populated with null for all bars.
            for (int i = 0; i < seriesA.Count; i++)
            {
                OptimalLag.Add(null);
            }
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary()
    {
        return new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { OptimalLagSeriesName, OptimalLag }
        };
    }
}
