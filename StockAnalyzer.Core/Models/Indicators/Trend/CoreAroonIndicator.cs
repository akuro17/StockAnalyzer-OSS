using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.Aroon)]
public class CoreAroonIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 25;
    public override string Name => $"Aroon ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAroonParameter p)
        {
            Period = p.Period;
        }
    }

    public List<decimal?> AroonUp { get; } = new();
    public List<decimal?> AroonDown { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        AroonUp.Clear();
        AroonDown.Clear();
        var candleDataList = candles.ToList();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period) { _values.Add(null); AroonUp.Add(null); AroonDown.Add(null); continue; }

            int daysSinceHigh = 0, daysSinceLow = 0;
            decimal highest = decimal.MinValue, lowest = decimal.MaxValue;

            for (int j = 0; j <= Period; j++)
            {
                if (candles[i - j].High > highest) { highest = candles[i - j].High; daysSinceHigh = j; }
                if (candles[i - j].Low < lowest) { lowest = candles[i - j].Low; daysSinceLow = j; }
            }

            decimal up = ((decimal)(Period - daysSinceHigh) / Period) * 100;
            decimal down = ((decimal)(Period - daysSinceLow) / Period) * 100;

            AroonUp.Add(up);
            AroonDown.Add(down);
            _values.Add(up - down); // Aroon Oscillator
        }

        return CreateAutomaticResult();
    }
}
