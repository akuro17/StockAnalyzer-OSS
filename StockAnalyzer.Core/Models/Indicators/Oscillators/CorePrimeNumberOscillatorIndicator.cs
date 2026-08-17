using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.PrimeNumberOscillator)]
public class CorePrimeNumberOscillatorIndicator : CoreIndicatorBase
{
    public override string Name => $"PNO ({ScaleMultiplier}, {ConsecutiveExtremaPeriods})";
    public override bool IsOverlay => false;

    public decimal ScaleMultiplier { get; set; } = 10.0m;
    public int ConsecutiveExtremaPeriods { get; set; } = 2;
    public int LookbackPeriod { get; set; } = 5;
    public decimal Tolerance { get; set; } = 0.0m;

    private readonly List<decimal?> _buySignals = new();
    private readonly List<decimal?> _sellSignals = new();

    public IReadOnlyList<decimal?> BuySignals => _buySignals;
    public IReadOnlyList<decimal?> SellSignals => _sellSignals;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePrimeNumberOscillatorParameter p)
        {
            ScaleMultiplier = p.ScaleMultiplier;
            ConsecutiveExtremaPeriods = p.ConsecutiveExtremaPeriods;
            LookbackPeriod = p.LookbackPeriod;
            Tolerance = p.Tolerance;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        _buySignals.Clear();
        _sellSignals.Clear();

        if (candles == null || candles.Count == 0)
            return IndicatorResult.Empty();

        int count = candles.Count;
        if (_values.Capacity < count) _values.Capacity = count;
        if (_buySignals.Capacity < count) _buySignals.Capacity = count;
        if (_sellSignals.Capacity < count) _sellSignals.Capacity = count;

        // 1. Calculate PNO values (O(N))
        for (int i = 0; i < count; i++)
        {
            decimal price = candles[i].Close;
            _buySignals.Add(null);
            _sellSignals.Add(null);

            if (price <= 0m)
            {
                _values.Add(0.0m);
                continue;
            }

            // Integer scaling and strict boundary clamping [2, MaxCachedPrime]
            int scaled = Math.Clamp(
                (int)Math.Round(price * ScaleMultiplier, MidpointRounding.AwayFromZero),
                2,
                PrimeNumberHelper.MaxCachedPrime);

            var (lowerPrime, upperPrime) = PrimeNumberHelper.FindNearestPrimes(scaled);

            decimal diffUpper = upperPrime - scaled;
            decimal diffLower = scaled - lowerPrime;

            decimal pno;
            if (diffUpper < diffLower)
            {
                pno = diffUpper / ScaleMultiplier;
            }
            else if (diffLower < diffUpper)
            {
                pno = -diffLower / ScaleMultiplier;
            }
            else
            {
                pno = 0.0m;
            }

            _values.Add(pno);
        }

        // 2. Consecutive Extrema Plateau signal evaluation (O(N * W))
        int W = LookbackPeriod;
        int K = ConsecutiveExtremaPeriods;
        int minRequired = Math.Max(W, K);

        int lastBuyPlateauIndex = -2;
        int lastSellPlateauIndex = -2;

        for (int i = minRequired - 1; i < count; i++)
        {
            decimal currentPno = _values[i]!.Value;

            // Calculate local extrema within lookback window [i-W+1, i]
            decimal peak = decimal.MinValue;
            decimal trough = decimal.MaxValue;
            for (int j = 0; j < W; j++)
            {
                decimal v = _values[i - j]!.Value;
                if (v > peak) peak = v;
                if (v < trough) trough = v;
            }

            // Evaluate Plateau condition across consecutive periods [i-K+1, i]
            bool isPlateau = true;
            for (int k = 1; k < K; k++)
            {
                if (Math.Abs(_values[i - k]!.Value - currentPno) > Tolerance)
                {
                    isPlateau = false;
                    break;
                }
            }

            if (isPlateau)
            {
                // Buy Signal: Local trough and negative oscillator territory
                if (currentPno < 0m && currentPno <= trough + Tolerance)
                {
                    // Suppress repeated duplicate signals if already fired on previous bar (i-1)
                    if (lastBuyPlateauIndex != i - 1)
                    {
                        _buySignals[i] = candles[i].Low;
                    }
                    lastBuyPlateauIndex = i;
                }

                // Sell Signal: Local peak and positive oscillator territory
                if (currentPno > 0m && currentPno >= peak - Tolerance)
                {
                    // Suppress repeated duplicate signals if already fired on previous bar (i-1)
                    if (lastSellPlateauIndex != i - 1)
                    {
                        _sellSignals[i] = candles[i].High;
                    }
                    lastSellPlateauIndex = i;
                }
            }
        }

        return CreateAutomaticResult();
    }
}
