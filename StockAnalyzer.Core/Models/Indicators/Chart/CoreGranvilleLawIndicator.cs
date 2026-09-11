using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>
/// Granville's Laws Analysis indicator.
/// Detects 8 entry signals (4 buy + 4 sell) based on MA slope and price-MA interaction.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.GranvilleLaw)]
public class CoreGranvilleLawIndicator : CoreIndicatorBase
{
    public override string Name => "Granville's Law";

    /// <summary>Signal series (positive values = buy 1-4, negative = sell 1-4, null = no signal).</summary>
    public IEnumerable<decimal?> Signals => _signals;

    /// <summary>MA line series to be drawn on the main chart.</summary>
    public IEnumerable<decimal?> MA => _ma;

    private readonly List<decimal?> _signals = new();
    private readonly List<decimal?> _ma = new();
    private readonly List<decimal> _smaBuffer = new();

    private int _maPeriod = IndicatorDefaultConstants.GranvilleMaPeriod;
    private int _slopePeriod = IndicatorDefaultConstants.GranvilleSlopePeriod;
    private decimal _deviationThreshold = IndicatorDefaultConstants.GranvilleDeviationThreshold;
    private decimal _bounceTolerance = IndicatorDefaultConstants.GranvilleBounceTolerance;
    private decimal _flatThreshold = IndicatorDefaultConstants.GranvilleFlatThreshold;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreGranvilleLawParameter p)
        {
            _maPeriod = p.MaPeriod;
            _slopePeriod = p.SlopePeriod;
            _deviationThreshold = p.DeviationThreshold;
            _bounceTolerance = p.BounceTolerance;
            _flatThreshold = p.FlatThreshold;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _signals.Clear();
        _ma.Clear();

        int count = candles.Count;

        // Initialize with nulls
        for (int i = 0; i < count; i++)
        {
            _signals.Add(null);
            _ma.Add(null);
        }

        // Need at least MaPeriod + SlopePeriod bars
        int minBars = _maPeriod + _slopePeriod;
        if (count < minBars)
        {
            return CreateResult();
        }

        // Step 1: Calculate SMA
        CalculateSma(candles, _maPeriod, _smaBuffer);

        // Populate MA series for all bars where SMA is valid (from maPeriod-1 onward)
        for (int i = _maPeriod - 1; i < count; i++)
        {
            if (_smaBuffer[i] != 0)
                _ma[i] = _smaBuffer[i];
        }

        // Step 2: Detect signals for each bar
        for (int i = minBars; i < count; i++)
        {

            if (_smaBuffer[i] == 0 || _smaBuffer[i - 1] == 0)
                continue;

            // MA slope direction
            decimal slope = _smaBuffer[i] - _smaBuffer[i - _slopePeriod];
            bool isMaRising = slope > 0 && Math.Abs(slope) / _smaBuffer[i] * 100m > _flatThreshold;
            bool isMaFalling = slope < 0 && Math.Abs(slope) / _smaBuffer[i] * 100m > _flatThreshold;
            bool isMaFlat = !isMaRising && !isMaFalling;

            decimal price = candles[i].Close;
            decimal prevPrice = candles[i - 1].Close;
            decimal maValue = _smaBuffer[i];
            decimal prevSma = _smaBuffer[i - 1];
            decimal high = candles[i].High;
            decimal low = candles[i].Low;

            decimal offset = price - maValue;
            decimal deviationRate = maValue != 0 ? (offset / maValue) * 100m : 0;
            
            // Calculate proximity relative to price points for bounce logic
            decimal proximityRateLow = maValue != 0 ? (Math.Abs(low - maValue) / maValue) * 100m : 0;
            decimal proximityRateHigh = maValue != 0 ? (Math.Abs(high - maValue) / maValue) * 100m : 0;

            bool crossedAbove = prevPrice <= prevSma && price > maValue;
            bool crossedBelow = prevPrice >= prevSma && price < maValue;
            bool isAboveMa = price > maValue;

            // Initialize to None
            GranvilleLawSignalType signal = GranvilleLawSignalType.None;

            // === Buy Signals ===

            // B4: MA falling + price extremely far below MA (reversal buy - checked first due to priority)
            if (isMaFalling && deviationRate < -_deviationThreshold)
            {
                signal = GranvilleLawSignalType.Buy4_ReversalBuy;
            }
            // B1: MA flat/rising + price breaks above MA from below (new buy)
            else if ((isMaFlat || isMaRising) && crossedAbove)
            {
                signal = GranvilleLawSignalType.Buy1_NewBuy;
            }
            // B2: MA rising + price dips below MA temporarily (pullback buy)
            else if (isMaRising && crossedBelow) // B2 is technically a crossover while MA is rising
            {
                signal = GranvilleLawSignalType.Buy2_PullbackBuy;
            }
            // B3: MA rising + price above MA + low touches/approaches MA zone and closes above MA (bounce buy)
            else if (isMaRising && isAboveMa && proximityRateLow <= _bounceTolerance && price > maValue && price > low)
            {
                signal = GranvilleLawSignalType.Buy3_BounceBuy;
            }

            // === Sell Signals (only if no buy signal) ===
            if (signal == GranvilleLawSignalType.None)
            {
                // S4: MA rising + price extremely far above MA (reversal sell - checked first)
                if (isMaRising && deviationRate > _deviationThreshold)
                {
                    signal = GranvilleLawSignalType.Sell4_ReversalSell;
                }
                // S1: MA flat/falling + price breaks below MA from above (new sell)
                else if ((isMaFlat || isMaFalling) && crossedBelow)
                {
                    signal = GranvilleLawSignalType.Sell1_NewSell;
                }
                // S2: MA falling + price rises above MA temporarily (return sell)
                else if (isMaFalling && crossedAbove)
                {
                    signal = GranvilleLawSignalType.Sell2_ReturnSell;
                }
                // S3: MA falling + price below MA + high touches/approaches MA zone and closes below MA (rejection sell)
                else if (isMaFalling && !isAboveMa && proximityRateHigh <= _bounceTolerance && price < maValue && price < high)
                {
                    signal = GranvilleLawSignalType.Sell3_RejectionSell;
                }
            }

            // Map continuous signal to output series (0 for no signal)
            _signals[i] = signal != GranvilleLawSignalType.None ? (decimal)signal : 0m;
        }

        return CreateResult();
    }

    /// <summary>
    /// Calculates Simple Moving Average for each bar and stores it in the provided buffer (ZeroAllocation).
    /// </summary>
    private static void CalculateSma(IReadOnlyList<CoreCandleData> candles, int period, List<decimal> buffer)
    {
        buffer.Clear();
        int count = candles.Count;

        // Ensure buffer has enough capacity
        if (buffer.Capacity < count)
        {
            buffer.Capacity = count;
        }

        // Initialize with default values (0)
        for (int i = 0; i < count; i++)
        {
            buffer.Add(0m);
        }

        if (count < period)
            return;

        // First SMA value
        decimal sum = 0;
        for (int i = 0; i < period; i++)
        {
            sum += candles[i].Close;
        }
        buffer[period - 1] = sum / period;

        // Subsequent values using sliding window
        for (int i = period; i < count; i++)
        {
            sum += candles[i].Close - candles[i - period].Close;
            buffer[i] = sum / period;
        }
    }

    /// <summary>
    /// Calculates MA slope direction over the specified number of bars comparing percentage change vs FlatThreshold.
    /// Returns positive for rising, negative for falling, 0 for flat.
    /// </summary>
    private static decimal CalculateSlopeDirection(decimal[] sma, int index, int slopePeriod, decimal flatThreshold)
    {
        if (index < slopePeriod || sma[index - slopePeriod] == 0)
            return 0;

        decimal currentMa = sma[index];
        decimal pastMa = sma[index - slopePeriod];

        // Calculate percentage change
        decimal changePercentage = pastMa != 0 ? (currentMa - pastMa) / pastMa * 100m : 0;

        if (changePercentage > flatThreshold)
            return 1; // Rising
        if (changePercentage < -flatThreshold)
            return -1; // Falling
        return 0; // Flat
    }
    private IIndicatorResult CreateResult()
    {
        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { "MA", _ma },
            { "Signals", _signals }
        });
    }
}
