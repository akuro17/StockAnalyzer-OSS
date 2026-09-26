using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.AdaptiveRSI)]
public class CoreAdaptiveRsiIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.AdaptiveRsiDefaultWindowSize;
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.AdaptiveRsiDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.AdaptiveRsiMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.AdaptiveRsiMaxPeriod;

    public override string Name => $"Adaptive RSI ({WindowSize}, {DefaultPeriod})";
    public override bool IsOverlay => false;

    public List<decimal?> DominantPeriod { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BullishSignals { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BearishSignals { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAdaptiveRsiParameter p)
        {
            WindowSize = p.WindowSize;
            DefaultPeriod = p.DefaultPeriod;
            MinPeriod = p.MinPeriod;
            MaxPeriod = p.MaxPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        DominantPeriod.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        // Dominant cycle (bars) of the trailing FFT window on the median price, computed natively: it drives the RSI period per bar.
        var samples = PriceDataHelper.ExtractDoubleSeries(candles, PriceType.Median);
        var cycle = new double[samples.Length];
        FftCycleMath.CalculateRollingFftCycle(samples, WindowSize, cycle, new double[samples.Length], new double[samples.Length]);
        NullableDecimalConversion.AppendTo(cycle, DominantPeriod);

        var rsiValues = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(
            candles,
            DominantPeriod,
            DefaultPeriod,
            MinPeriod,
            MaxPeriod);

        _values.AddRange(rsiValues);
        GenerateSignals(_values);

        return CreateAutomaticResult();
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        DominantPeriod.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        if (dynamicPeriods != null)
        {
            DominantPeriod.AddRange(dynamicPeriods);
        }
        else
        {
            for (int i = 0; i < series.Count; i++) DominantPeriod.Add(null);
        }

        var rsiValues = AdaptiveSmoothingHelper.CalculateAdaptiveWilderRsi(
            series,
            dynamicPeriods,
            DefaultPeriod,
            MinPeriod,
            MaxPeriod);

        _values.AddRange(rsiValues);
        GenerateSignals(_values);

        return CreateAutomaticResult();
    }

    private void GenerateSignals(IReadOnlyList<decimal?> values)
    {
        decimal? prevRsi = null;
        foreach (var rsi in values)
        {
            if (prevRsi.HasValue && rsi.HasValue)
            {
                // Bullish: exit oversold (< 30)
                if (prevRsi <= 30m && rsi > 30m)
                {
                    BullishSignals.Add(1m);
                }
                else
                {
                    BullishSignals.Add(null);
                }

                // Bearish: exit overbought (> 70)
                if (prevRsi >= 70m && rsi < 70m)
                {
                    BearishSignals.Add(1m);
                }
                else
                {
                    BearishSignals.Add(null);
                }
            }
            else
            {
                BullishSignals.Add(null);
                BearishSignals.Add(null);
            }

            prevRsi = rsi;
        }
    }
}
