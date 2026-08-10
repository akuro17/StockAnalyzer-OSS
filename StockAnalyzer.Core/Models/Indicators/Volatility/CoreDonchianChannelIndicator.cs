using Math = System.Math;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.Donchian)]
public class CoreDonchianChannelIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"Donchian ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period; 
        }
    }

    public List<decimal?> UpperBand { get; } = new();
    public List<decimal?> LowerBand { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        UpperBand.Clear();
        LowerBand.Clear();
        var candleDataList = candles.ToList();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1) { _values.Add(null); UpperBand.Add(null); LowerBand.Add(null); continue; }

            decimal highest = decimal.MinValue;
            decimal lowest = decimal.MaxValue;
            for (int j = 0; j < Period; j++)
            {
                highest = System.Math.Max(highest, candles[i - j].High);
                lowest = System.Math.Min(lowest, candles[i - j].Low);
            }

            UpperBand.Add(highest);
            LowerBand.Add(lowest);
            _values.Add((highest + lowest) / 2);
        }

        return IndicatorResult.Success(_values);
    }
}
