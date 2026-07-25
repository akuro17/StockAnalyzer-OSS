using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.BandWidth)]
public class CoreBandWidthIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public decimal StdDevMultiplier { get; set; } = 2.0m;

    public override string Name => $"BandWidth ({Period}, {StdDevMultiplier})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreBollingerBandsParameter p)
        {
            Period = p.Period;
            StdDevMultiplier = (decimal)p.StdDevMultiplier;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);
        if (candles.Count < Period)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        var middleBand = new List<decimal?>();
        var stdDevs = new List<decimal?>();

        // Calculate SMA (Middle Band) and StdDev
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                middleBand.Add(null);
                stdDevs.Add(null);
            }
            else
            {
                decimal sum = 0m;
                int start = i - Period + 1;
                for (int j = 0; j < Period; j++)
                {
                    sum += candles[start + j].Close;
                }
                decimal sma = sum / Period;
                middleBand.Add(sma);

                decimal sumOfSquares = 0m;
                for (int j = 0; j < Period; j++)
                {
                    decimal diff = candles[start + j].Close - sma;
                    sumOfSquares += diff * diff;
                }
                stdDevs.Add((decimal)Math.Sqrt((double)(sumOfSquares / Period)));
            }
        }

        // Calculate Bandwidth
        for (int i = 0; i < candles.Count; i++)
        {
            if (middleBand[i].HasValue && middleBand[i] != 0 && stdDevs[i].HasValue)
            {
                var upperBand = middleBand[i]!.Value + StdDevMultiplier * stdDevs[i]!.Value;
                var lowerBand = middleBand[i]!.Value - StdDevMultiplier * stdDevs[i]!.Value;
                _values.Add((upperBand - lowerBand) / middleBand[i]!.Value * 100);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
