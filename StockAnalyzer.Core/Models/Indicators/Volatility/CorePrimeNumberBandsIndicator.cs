using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.PrimeNumberBands)]
public class CorePrimeNumberBandsIndicator : CoreIndicatorBase
{
    public override string Name => $"PNB ({Period}, {ScaleMultiplier})";
    public override bool IsOverlay => true;

    public int Period { get; set; } = IndicatorDefaultConstants.PrimeNumberBandsPeriod;
    public decimal ScaleMultiplier { get; set; } = IndicatorDefaultConstants.PrimeNumberBandsScaleMultiplier;

    public const string MiddleSeriesName = IndicatorResult.MainSeriesName;
    public const string UpperSeriesName = "Upper";
    public const string LowerSeriesName = "Lower";

    private readonly List<decimal?> _upperBand = new();
    private readonly List<decimal?> _lowerBand = new();

    public IReadOnlyList<decimal?> MiddleBand => _values;
    public IReadOnlyList<decimal?> UpperBand => _upperBand;
    public IReadOnlyList<decimal?> LowerBand => _lowerBand;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePrimeNumberBandsParameter p)
        {
            Period = p.Period;
            ScaleMultiplier = p.ScaleMultiplier;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        _upperBand.Clear();
        _lowerBand.Clear();

        if (candles == null || candles.Count == 0)
            return IndicatorResult.Empty();

        int count = candles.Count;
        if (_values.Capacity < count) _values.Capacity = count;
        if (_upperBand.Capacity < count) _upperBand.Capacity = count;
        if (_lowerBand.Capacity < count) _lowerBand.Capacity = count;

        int period = Period;
        decimal multiplier = ScaleMultiplier;
        decimal limitMaxPrime = (decimal)PrimeNumberHelper.MaxCachedPrime;

        for (int i = 0; i < count; i++)
        {
            if (i < period - 1)
            {
                _values.Add(null);
                _upperBand.Add(null);
                _lowerBand.Add(null);
                continue;
            }

            // 1. Extract Highest High and Lowest Low in lookback window [i - period + 1, i]
            decimal highestHigh = decimal.MinValue;
            decimal lowestLow = decimal.MaxValue;
            for (int j = 0; j < period; j++)
            {
                var c = candles[i - j];
                if (c.High > highestHigh) highestHigh = c.High;
                if (c.Low < lowestLow) lowestLow = c.Low;
            }

            // 2. Safe scaling and clamping in decimal domain to strictly avert (int) OverflowException
            decimal rawHigh = Math.Round(highestHigh * multiplier, MidpointRounding.AwayFromZero);
            decimal rawLow = Math.Round(lowestLow * multiplier, MidpointRounding.AwayFromZero);

            int scaledHigh = (int)Math.Clamp(rawHigh, 2m, limitMaxPrime);
            int scaledLow = (int)Math.Clamp(rawLow, 2m, limitMaxPrime);

            // 3. Search nearest primes
            var (_, upperPrime) = PrimeNumberHelper.FindNearestPrimes(scaledHigh);
            var (lowerPrime, _) = PrimeNumberHelper.FindNearestPrimes(scaledLow);

            // 4. Reverse mapping to price domain
            decimal upperBand = upperPrime / multiplier;
            decimal lowerBand = lowerPrime / multiplier;
            decimal middleBand = (upperBand + lowerBand) / 2m;

            _values.Add(middleBand);
            _upperBand.Add(upperBand);
            _lowerBand.Add(lowerBand);
        }

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { MiddleSeriesName, _values },
            { UpperSeriesName, _upperBand },
            { LowerSeriesName, _lowerBand }
        });
    }
}
