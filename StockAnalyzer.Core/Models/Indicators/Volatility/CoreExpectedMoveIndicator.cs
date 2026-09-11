using StockAnalyzer.Core.Models.Indicators.Volatility;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.ExpectedMove)]
public class CoreExpectedMoveIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public decimal Multiplier { get; set; } = 1.0m;

    public override string Name => $"Expected Move ({Period}, {Multiplier})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreExpectedMoveParameter p)
        {
            Period = p.Period;
            Multiplier = p.Multiplier;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (!candles.Any())
        {
            return IndicatorResult.Success(_values);
        }

        var atrIndicator = new CoreAtrIndicator { Period = Period };
        atrIndicator.Calculate(candles);

        foreach (var atrValue in atrIndicator.Values)
        {
            if (atrValue.HasValue)
            {
                _values.Add(atrValue.Value * Multiplier);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
