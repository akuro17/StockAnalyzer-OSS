using StockAnalyzer.Core.Models.Parameters;
using Math = System.Math;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.CCI)]
public class CoreCciIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"CCI ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        decimal constant = 0.015m;

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1) { _values.Add(null); continue; }

            // Typical Price
            var tpList = new List<decimal>();
            for (int j = 0; j < Period; j++)
            {
                var c = candles[i - j];
                tpList.Add((c.High + c.Low + c.Close) / 3);
            }

            decimal smaTP = tpList.Average();
            decimal meanDeviation = tpList.Select(tp => Math.Abs(tp - smaTP)).Average();

            decimal tp = (candles[i].High + candles[i].Low + candles[i].Close) / 3;
            decimal cci = meanDeviation == 0 ? 0 : (tp - smaTP) / (constant * meanDeviation);
            _values.Add(cci);
        }

        return IndicatorResult.Success(_values);
    }
}
