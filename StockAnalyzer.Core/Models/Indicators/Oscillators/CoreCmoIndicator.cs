using Math = System.Math;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.CMO)]
public class CoreCmoIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"CMO ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles.Count < 2) return IndicatorResult.Success(_values);
        _values.Add(null);

        for (int i = 1; i < candles.Count; i++)
        {
            if (i < Period) { _values.Add(null); continue; }

            decimal sumUp = 0, sumDown = 0;
            for (int j = 0; j < Period; j++)
            {
                decimal change = candles[i - j].Close - candles[i - j - 1].Close;
                if (change > 0) sumUp += change;
                else sumDown += Math.Abs(change);
            }

            decimal cmo = (sumUp + sumDown) == 0 ? 0 : ((sumUp - sumDown) / (sumUp + sumDown)) * 100;
            _values.Add(cmo);
        }

        return IndicatorResult.Success(_values);
    }
}
