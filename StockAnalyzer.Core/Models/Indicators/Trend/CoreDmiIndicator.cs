using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.DMI)]
public class CoreDmiIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public override string Name => $"DMI ({Period})";
    
    public List<decimal?> PlusDI { get; } = new();
    public List<decimal?> MinusDI { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreDmiParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        PlusDI.Clear();
        MinusDI.Clear();
        var candleDataList = candles.ToList();
        if (candles == null || candles.Count < Period)
        {
            // Initial fill handled dynamically or ignored
            return IndicatorResult.Success(_values);
        }

        var trList = new List<decimal>();
        var plusDmList = new List<decimal>();
        var minusDmList = new List<decimal>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i == 0)
            {
                trList.Add(candles[i].High - candles[i].Low);
                plusDmList.Add(0);
                minusDmList.Add(0);
                continue;
            }

            var high = candles[i].High;
            var low = candles[i].Low;
            var prevClose = candles[i - 1].Close;
            var prevHigh = candles[i - 1].High;
            var prevLow = candles[i - 1].Low;

            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trList.Add(tr);

            var upMove = high - prevHigh;
            var downMove = prevLow - low;

            plusDmList.Add(upMove > downMove && upMove > 0 ? upMove : 0);
            minusDmList.Add(downMove > upMove && downMove > 0 ? downMove : 0);
        }

        var atrSma = Sma(trList, Period);
        var plusDmSma = Sma(plusDmList, Period);
        var minusDmSma = Sma(minusDmList, Period);

        for(int i = 0; i < candles.Count; i++)
        {
            var atr = atrSma[i];
            if (atr.HasValue && atr != 0 && plusDmSma[i].HasValue && minusDmSma[i].HasValue)
            {
                decimal pdi = plusDmSma[i]!.Value / atr!.Value * 100;
                decimal mdi = minusDmSma[i]!.Value / atr!.Value * 100;
                PlusDI.Add(pdi);
                MinusDI.Add(mdi);
                _values.Add(pdi); // Default value is PlusDI? Or meaningless? 
                                  // DMI typically plots both +DI and -DI. Main value set to +DI for now, or Difference?
                                  // Usually DMI indicator implies +DI and -DI lines.
                                  // Let's set _values to +DI to be consistent with single return value, but callers should use lists.
            }
            else
            {
                PlusDI.Add(null);
                MinusDI.Add(null);
                _values.Add(null);
            }
        }

        return CreateAutomaticResult();
    }

    private static List<decimal?> Sma(IReadOnlyList<decimal> data, int period)
    {
        var results = new List<decimal?>();
        for (int i = 0; i < data.Count; i++)
        {
            if (i < period - 1) results.Add(null);
            else results.Add(data.Skip(i - period + 1).Take(period).Average());
        }
        return results;
    }
}
