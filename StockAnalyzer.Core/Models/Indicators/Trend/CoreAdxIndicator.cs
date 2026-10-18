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
        _values.Clear();
        PlusDI.Clear();
        MinusDI.Clear();

        if (candles == null || candles.Count == 0)
        {
            return CreateAutomaticResult();
        }

        int n = candles.Count;
        if (Period <= 0 || n < Period)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
                PlusDI.Add(null);
                MinusDI.Add(null);
            }
            return CreateAutomaticResult();
        }

        decimal str = 0m;
        decimal sPlusDm = 0m;
        decimal sMinusDm = 0m;
        decimal prevAdx = 0m;

        for (int i = 0; i < n; i++)
        {
            decimal tr;
            decimal plusDm = 0m;
            decimal minusDm = 0m;

            if (i == 0)
            {
                tr = candles[0].High - candles[0].Low;
            }
            else
            {
                var cur = candles[i];
                var prev = candles[i - 1];

                tr = Math.Max(cur.High - cur.Low,
                     Math.Max(Math.Abs(cur.High - prev.Close),
                              Math.Abs(cur.Low - prev.Close)));

                decimal upMove = cur.High - prev.High;
                decimal downMove = prev.Low - cur.Low;

                if (upMove > downMove && upMove > 0m)
                {
                    plusDm = upMove;
                }
                if (downMove > upMove && downMove > 0m)
                {
                    minusDm = downMove;
                }
            }

            if (i < Period - 1)
            {
                str += tr;
                sPlusDm += plusDm;
                sMinusDm += minusDm;

                _values.Add(null);
                PlusDI.Add(null);
                MinusDI.Add(null);
            }
            else if (i == Period - 1)
            {
                str += tr;
                sPlusDm += plusDm;
                sMinusDm += minusDm;

                decimal pdi = str > 0m ? (sPlusDm / str) * 100m : 0m;
                decimal mdi = str > 0m ? (sMinusDm / str) * 100m : 0m;
                decimal diSum = pdi + mdi;
                decimal dx = diSum > 0m ? (Math.Abs(pdi - mdi) / diSum) * 100m : 0m;

                prevAdx = dx;
                _values.Add(prevAdx);
                PlusDI.Add(pdi);
                MinusDI.Add(mdi);
            }
            else
            {
                str = str - (str / Period) + tr;
                sPlusDm = sPlusDm - (sPlusDm / Period) + plusDm;
                sMinusDm = sMinusDm - (sMinusDm / Period) + minusDm;

                decimal pdi = str > 0m ? (sPlusDm / str) * 100m : 0m;
                decimal mdi = str > 0m ? (sMinusDm / str) * 100m : 0m;
                decimal diSum = pdi + mdi;
                decimal dx = diSum > 0m ? (Math.Abs(pdi - mdi) / diSum) * 100m : 0m;

                decimal adx = (prevAdx * (Period - 1) + dx) / Period;
                prevAdx = adx;

                _values.Add(adx);
                PlusDI.Add(pdi);
                MinusDI.Add(mdi);
            }
        }

        return CreateAutomaticResult();
    }
}
