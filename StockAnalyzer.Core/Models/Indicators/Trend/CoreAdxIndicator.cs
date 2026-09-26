using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.ADX)]
public class CoreAdxIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"ADX ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAdxParameter p)
        {
            Period = p.Period;
        }
    }

    public List<decimal?> PlusDI { get; } = new();
    public List<decimal?> MinusDI { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        PlusDI.Clear();
        MinusDI.Clear();
        var candleDataList = candles.ToList();
        if (candles.Count < 2) return IndicatorResult.Success(_values);
        var trList = new List<decimal>();
        var plusDM = new List<decimal>();
        var minusDM = new List<decimal>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i == 0)
            {
                trList.Add(candles[i].High - candles[i].Low);
                plusDM.Add(0);
                minusDM.Add(0);
                continue;
            }

            decimal tr = Math.Max(candles[i].High - candles[i].Low,
                         Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close),
                                  Math.Abs(candles[i].Low - candles[i - 1].Close)));
            trList.Add(tr);

            decimal upMove = candles[i].High - candles[i - 1].High;
            decimal downMove = candles[i - 1].Low - candles[i].Low;

            decimal currentPlusDM = 0;
            decimal currentMinusDM = 0;

            if (upMove > downMove && upMove > 0)
            {
                currentPlusDM = upMove;
            }

            if (downMove > upMove && downMove > 0)
            {
                currentMinusDM = downMove;
            }

            plusDM.Add(currentPlusDM);
            minusDM.Add(currentMinusDM);
        }

        // Smoothed TR, +DM, -DM
        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1) { _values.Add(null); PlusDI.Add(null); MinusDI.Add(null); continue; }

            decimal atr = trList.Skip(i - Period + 1).Take(Period).Average() * Period;
            decimal sPlusDM = plusDM.Skip(i - Period + 1).Take(Period).Sum();
            decimal sMinusDM = minusDM.Skip(i - Period + 1).Take(Period).Sum();

            decimal pdi = atr == 0 ? 0 : (sPlusDM / atr) * 100;
            decimal mdi = atr == 0 ? 0 : (sMinusDM / atr) * 100;

            PlusDI.Add(pdi);
            MinusDI.Add(mdi);

            decimal dx = (pdi + mdi) == 0 ? 0 : (Math.Abs(pdi - mdi) / (pdi + mdi)) * 100;
            _values.Add(dx);
        }

        return IndicatorResult.Success(_values);
    }
}
