using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

/// <summary>
/// Ratiocator (Relative Strength Ratio) Indicator.
/// Calculates the ratio of a primary symbol's price to a benchmark symbol's price × 100.
/// Output unit: dimensionless ratio (not price-native).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Ratiocator)]
public class CoreRatiocatorIndicator : CoreIndicatorBase, ICrossTickerIndicator
{
    private const decimal ScaleFactor = 100m;
    public const string SignalSeriesName = "Signal";

    private readonly IReadOnlyList<decimal?>? _explicitSeriesB;
    private IReadOnlyList<CoreCandleData?>? _secondaryCandles;
    private int _lookbackPeriod = IndicatorDefaultConstants.RatiocatorLookbackPeriod;
    private int _smoothingPeriod = IndicatorDefaultConstants.RatiocatorSmoothingPeriod;

    public string ComparisonSymbol { get; set; } = string.Empty;
    public PriceType ComparisonPriceSource { get; set; } = PriceType.Close;
    public RatiocatorCalculationMode CalculationMode { get; set; } = RatiocatorCalculationMode.RollingRatio;

    public int LookbackPeriod
    {
        get => _lookbackPeriod;
        set => _lookbackPeriod = value;
    }

    public int SmoothingPeriod
    {
        get => _smoothingPeriod;
        set => _smoothingPeriod = value;
    }

    public List<decimal?> Signal { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> SignalValues => Signal;

    public override string Name
    {
        get
        {
            string modePart = CalculationMode switch
            {
                RatiocatorCalculationMode.RollingRatio => $", Roll{LookbackPeriod}",
                RatiocatorCalculationMode.NormalizedRatio => ", Norm",
                _ => string.Empty
            };
            string smoothPart = SmoothingPeriod > 0 ? $", EMA{SmoothingPeriod}" : string.Empty;
            if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
            {
                return $"Ratiocator({ComparisonSymbol.Trim().ToUpperInvariant()}{modePart}{smoothPart})";
            }
            string combined = (modePart + smoothPart).TrimStart(',', ' ');
            return string.IsNullOrEmpty(combined) ? "Ratiocator" : $"Ratiocator({combined})";
        }
    }

    public override bool IsOverlay => false;

    // ── Constructors ──
    public CoreRatiocatorIndicator()
    {
    }

    public CoreRatiocatorIndicator(int smoothingPeriod)
    {
        _smoothingPeriod = smoothingPeriod;
    }

    public CoreRatiocatorIndicator(int lookbackPeriod, int smoothingPeriod)
    {
        _lookbackPeriod = lookbackPeriod;
        _smoothingPeriod = smoothingPeriod;
    }

    public CoreRatiocatorIndicator(IEnumerable<decimal?> benchmarkSeries)
    {
        _explicitSeriesB = benchmarkSeries is IReadOnlyList<decimal?> list
            ? list
            : new List<decimal?>(benchmarkSeries);
    }

    public CoreRatiocatorIndicator(int smoothingPeriod, IEnumerable<decimal?> benchmarkSeries)
    {
        _smoothingPeriod = smoothingPeriod;
        _explicitSeriesB = benchmarkSeries is IReadOnlyList<decimal?> list
            ? list
            : new List<decimal?>(benchmarkSeries);
    }

    public CoreRatiocatorIndicator(int lookbackPeriod, int smoothingPeriod, IEnumerable<decimal?> benchmarkSeries)
    {
        _lookbackPeriod = lookbackPeriod;
        _smoothingPeriod = smoothingPeriod;
        _explicitSeriesB = benchmarkSeries is IReadOnlyList<decimal?> list
            ? list
            : new List<decimal?>(benchmarkSeries);
    }

    public CoreRatiocatorIndicator(int smoothingPeriod, IEnumerable<CoreCandleData> benchmarkSeries)
    {
        _smoothingPeriod = smoothingPeriod;
        _explicitSeriesB = benchmarkSeries?.Select(c => (decimal?)c.Close).ToList();
    }

    // ── ICrossTickerIndicator ──
    public void SetSecondaryCandles(IReadOnlyList<CoreCandleData?>? candles)
    {
        _secondaryCandles = candles;
    }

    // ── Configuration ──
    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRatiocatorParameter rParam)
        {
            ComparisonSymbol = rParam.ComparisonSymbol?.Trim() ?? string.Empty;
            ComparisonPriceSource = rParam.ComparisonPriceSource;
            CalculationMode = rParam.CalculationMode;
            LookbackPeriod = rParam.LookbackPeriod;
            SmoothingPeriod = rParam.SmoothingPeriod;
        }
        else if (parameters is CoreSmaParameter smaParam)
        {
            // Fallback: interpret Period as SmoothingPeriod
            SmoothingPeriod = smaParam.Period;
        }
    }

    // ── Calculation ──
    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        Signal.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = candles.Count;
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
                return FillNulls(count);
            }
        }
        else if (_explicitSeriesB != null)
        {
            seriesB = _explicitSeriesB;
        }
        else
        {
            return FillNulls(count);
        }

        return ComputeRatio(seriesA, seriesB);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        Signal.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        IReadOnlyList<decimal?>? seriesB = _explicitSeriesB ?? dynamicPeriods;
        if (seriesB == null)
        {
            return FillNulls(series.Count);
        }

        return ComputeRatio(series, seriesB);
    }

    private IIndicatorResult ComputeRatio(IReadOnlyList<decimal?> seriesA, IReadOnlyList<decimal?> seriesB)
    {
        int count = seriesA.Count;

        // Align series (end-aligned, matching latest bars)
        IReadOnlyList<decimal?> alignedB;
        if (seriesA.Count == seriesB.Count)
        {
            alignedB = seriesB;
        }
        else
        {
            int commonCount = Math.Min(seriesA.Count, seriesB.Count);
            int offsetA = seriesA.Count - commonCount;
            int offsetB = seriesB.Count - commonCount;

            var aligned = new List<decimal?>(seriesA.Count);
            for (int i = 0; i < seriesA.Count; i++)
            {
                if (i < offsetA)
                {
                    aligned.Add(null);
                }
                else
                {
                    int bIdx = i - offsetA + offsetB;
                    aligned.Add(bIdx < seriesB.Count ? seriesB[bIdx] : null);
                }
            }
            alignedB = aligned;
        }

        // Calculate raw ratio values: P_primary / P_benchmark * 100
        var rawRatios = new List<decimal?>(count);
        for (int i = 0; i < count; i++)
        {
            decimal? primary = seriesA[i];
            decimal? benchmark = alignedB[i];

            if (!primary.HasValue || !benchmark.HasValue || benchmark.Value <= 0m || primary.Value < 0m)
            {
                rawRatios.Add(null);
            }
            else
            {
                rawRatios.Add(primary.Value / benchmark.Value * ScaleFactor);
            }
        }

        List<decimal?> ratioValues;
        if (CalculationMode == RatiocatorCalculationMode.RollingRatio)
        {
            int n = Math.Max(1, LookbackPeriod);
            ratioValues = new List<decimal?>(count);
            for (int i = 0; i < count; i++)
            {
                if (i < n)
                {
                    ratioValues.Add(null);
                    continue;
                }

                decimal? currentA = rawRatios[i];
                decimal? pastA = rawRatios[i - n];

                if (!currentA.HasValue || !pastA.HasValue || pastA.Value <= 0m)
                {
                    ratioValues.Add(null);
                }
                else
                {
                    ratioValues.Add(currentA.Value / pastA.Value * ScaleFactor);
                }
            }
        }
        else if (CalculationMode == RatiocatorCalculationMode.NormalizedRatio)
        {
            ratioValues = new List<decimal?>(count);
            decimal? firstValidRatio = rawRatios.FirstOrDefault(r => r.HasValue);
            if (firstValidRatio.HasValue && firstValidRatio.Value != 0m)
            {
                decimal baseRatio = firstValidRatio.Value;
                for (int i = 0; i < count; i++)
                {
                    if (rawRatios[i].HasValue)
                    {
                        ratioValues.Add(rawRatios[i]!.Value / baseRatio * ScaleFactor);
                    }
                    else
                    {
                        ratioValues.Add(null);
                    }
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    ratioValues.Add(null);
                }
            }
        }
        else // RawRatio
        {
            ratioValues = rawRatios;
        }

        _values.AddRange(ratioValues);

        // Calculate signal line (EMA smoothing)
        if (SmoothingPeriod >= 2)
        {
            var ema = IndicatorCalculationHelper.CalculateEmaWithNulls(ratioValues, SmoothingPeriod);
            Signal.AddRange(ema);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Signal.Add(null);
            }
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private IIndicatorResult FillNulls(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _values.Add(null);
            Signal.Add(null);
        }
        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary()
    {
        return new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { SignalSeriesName, Signal }
        };
    }
}
