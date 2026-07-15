using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.WMA)]
public class CoreWmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;

    public override string Name => $"WMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        int weightSum = Period * (Period + 1) / 2;

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
            }
            else
            {
                decimal weightedSum = 0;
                for (int j = 0; j < Period; j++)
                {
                    weightedSum += candles[i - j].Close * (Period - j);
                }
                _values.Add(weightedSum / weightSum);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
