using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.MACD)]
public class CoreMacdIndicator : CoreIndicatorBase
{
    public int FastPeriod { get; set; } = 12;
    public int SlowPeriod { get; set; } = 26;
    public int SignalPeriod { get; set; } = 9;

    public override string Name => $"MACD ({FastPeriod}, {SlowPeriod}, {SignalPeriod})";
    public override bool IsOverlay => false;


    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreMacdParameter macdParam)
        {
            FastPeriod = macdParam.ShortPeriod;
            SlowPeriod = macdParam.LongPeriod;
            SignalPeriod = macdParam.SignalPeriod;
        }
    }

    // Well-known series names for MACD
    public const string MacdSeriesName = IndicatorResult.MainSeriesName;
    public const string SignalSeriesName = "Signal";
    public const string HistogramSeriesName = "Histogram";

    public IReadOnlyList<decimal?> MacdLine => _values;
    public List<decimal?> Signal { get; } = new();
    public List<decimal?> Histogram { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BullishSignals { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BearishSignals { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // MACD involves multiple recursive calculations (EMAs), so we use a robust overlap strategy.
        // FastEMA, SlowEMA, and then Signal line (EMA of MACD).
        // Warmup needs to cover the SlowEMA convergence AND Signal convergence.
        var results = CalculateParallel(candles, CalculateSegment);

        // Separate the tuple results into lists
        _values.Clear();
        Signal.Clear();
        Histogram.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        decimal? prevHist = null;
        foreach (var r in results)
        {
            _values.Add(r.Macd);
            Signal.Add(r.Signal);
            Histogram.Add(r.Histogram);

            // Signal Detection: Histogram crossover
            if (prevHist.HasValue && r.Histogram.HasValue)
            {
                if (prevHist <= 0 && r.Histogram > 0) BullishSignals.Add(1m);
                else BullishSignals.Add(null);

                if (prevHist >= 0 && r.Histogram < 0) BearishSignals.Add(1m);
                else BearishSignals.Add(null);
            }
            else
            {
                BullishSignals.Add(null);
                BearishSignals.Add(null);
            }
            prevHist = r.Histogram;
        }

        return CreateAutomaticResult();
    }

    private struct MacdResult
    {
        public decimal? Macd;
        public decimal? Signal;
        public decimal? Histogram;
    }

    private List<MacdResult> CalculateSegment(IReadOnlyList<CoreCandleData> candles, int start, int end)
    {
        var results = new List<MacdResult>(end - start);

        // 1. Determine Warmup Start
        int overlap = (SlowPeriod * IndicatorDefaultConstants.EmaConvergenceMultiplier) + (SignalPeriod * IndicatorDefaultConstants.EmaConvergenceMultiplier);
        int calcStart = Math.Max(0, start - overlap);

        // Constants
        decimal fastMultiplier = 2m / (FastPeriod + 1);
        decimal slowMultiplier = 2m / (SlowPeriod + 1);
        decimal signalMultiplier = 2m / (SignalPeriod + 1);

        // Local State
        decimal? fastEma = null;
        decimal? slowEma = null;
        decimal? signal = null;

        // Buffer for Signal initialization (only needed if starting from beginning)
        List<decimal> macdBuffer = new();
        int firstValidMacdIndex = SlowPeriod - 1;
        int firstValidSignalIndex = firstValidMacdIndex + SignalPeriod - 1;

        // Loop
        for (int i = calcStart; i < end; i++)
        {
            decimal close = candles[i].Close;

            // --- Fast EMA ---
            if (i < FastPeriod - 1) fastEma = null;
            else if (i == FastPeriod - 1)
            {
                decimal sum = 0;
                for (int j = 0; j < FastPeriod; j++) sum += candles[i - j].Close;
                fastEma = sum / FastPeriod;
            }
            else
            {
                if (fastEma.HasValue)
                    fastEma = (close - fastEma.Value) * fastMultiplier + fastEma.Value;
                else
                {
                    // Warmup initialization
                     decimal sum = 0;
                     for (int j = 0; j < FastPeriod; j++) sum += candles[i - j].Close;
                     fastEma = sum / FastPeriod;
                }
            }

            // --- Slow EMA ---
            if (i < SlowPeriod - 1) slowEma = null;
            else if (i == SlowPeriod - 1)
            {
                decimal sum = 0;
                for (int j = 0; j < SlowPeriod; j++) sum += candles[i - j].Close;
                slowEma = sum / SlowPeriod;
            }
            else
            {
                 if (slowEma.HasValue)
                    slowEma = (close - slowEma.Value) * slowMultiplier + slowEma.Value;
                 else
                 {
                     // Warmup initialization
                     decimal sum = 0;
                     for (int j = 0; j < SlowPeriod; j++) sum += candles[i - j].Close;
                     slowEma = sum / SlowPeriod;
                 }
            }

            // --- MACD Line ---
            decimal? macd = (fastEma.HasValue && slowEma.HasValue) ? fastEma - slowEma : null;

            // --- Signal Line ---
            if (i < firstValidSignalIndex)
            {
                signal = null;
                // Buffer MACD for exact initialization if we are at start
                if (i >= firstValidMacdIndex && calcStart == 0 && macd.HasValue)
                {
                    macdBuffer.Add(macd.Value);
                }
            }
            else
            {
                if (signal.HasValue)
                {
                    // Normal recursion
                    signal = (macd!.Value - signal.Value) * signalMultiplier + signal.Value;
                }
                else
                {
                    // Initialization
                    if (calcStart == 0 && i == firstValidSignalIndex)
                    {
                        // Exact initialization using buffer
                        if (macd.HasValue) macdBuffer.Add(macd.Value);
                        if (macdBuffer.Count > 0)
                            signal = macdBuffer.Average();
                        else
                            signal = macd; // Fallback
                            
                        macdBuffer.Clear(); // No longer needed
                    }
                    else
                    {
                        // Warmup initialization (approximation)
                        signal = macd;
                    }
                }
            }

            // --- Histogram ---
            decimal? histogram = (macd.HasValue && signal.HasValue) ? macd - signal : null;

            // Store result if within requested range
            if (i >= start)
            {
                results.Add(new MacdResult
                {
                    Macd = macd,
                    Signal = signal,
                    Histogram = histogram
                });
            }
        }

        return results;
    }
}
