using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

[StockAnalyzerIndicator(IndicatorType.YangZhangVolatility)]
public class CoreYangZhangVolatilityIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 10;
    public override string Name => $"Yang-Zhang Vol ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period; 
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int n = candles.Count;
        if (Period < 2 || n < Period)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        bool[] isValid = new bool[n];
        double[] overnightLog = new double[n];
        double[] openCloseLog = new double[n];
        double[] rsTerm = new double[n];

        for (int m = 0; m < n; m++)
        {
            var candle = candles[m];
            if (candle.Open <= 0m || candle.High <= 0m || candle.Low <= 0m || candle.Close <= 0m)
            {
                isValid[m] = false;
                continue;
            }

            if (m > 0 && candles[m - 1].Close <= 0m)
            {
                isValid[m] = false;
                continue;
            }

            if (m == 0)
            {
                // The first bar has no previous close, so its overnight return is undefined and the
                // bar cannot belong to a window (first output is at index Period).
                isValid[0] = false;
                continue;
            }

            isValid[m] = true;

            double o = (double)candle.Open;
            double h = (double)candle.High;
            double l = (double)candle.Low;
            double c = (double)candle.Close;

            double prevC = (double)candles[m - 1].Close;
            overnightLog[m] = Math.Log(o / prevC);

            double cm = Math.Log(c / o);
            openCloseLog[m] = cm;

            double um = Math.Log(h / o);
            double dm = Math.Log(l / o);
            rsTerm[m] = um * (um - cm) + dm * (dm - cm);
        }

        double k = 0.34 / (1.34 + (double)(Period + 1) / (Period - 1));

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        for (int i = Period - 1; i < n; i++)
        {
            int start = i - Period + 1;
            bool windowValid = true;
            for (int m = start; m <= i; m++)
            {
                if (!isValid[m])
                {
                    windowValid = false;
                    break;
                }
            }

            if (!windowValid)
            {
                _values.Add(null);
                continue;
            }

            double sumO = 0.0;
            double sumC = 0.0;
            double sumRS = 0.0;

            for (int m = start; m <= i; m++)
            {
                sumO += overnightLog[m];
                sumC += openCloseLog[m];
                sumRS += rsTerm[m];
            }

            double meanO = sumO / Period;
            double meanC = sumC / Period;

            double varO = 0.0;
            double varC = 0.0;

            for (int m = start; m <= i; m++)
            {
                double diffO = overnightLog[m] - meanO;
                varO += diffO * diffO;

                double diffC = openCloseLog[m] - meanC;
                varC += diffC * diffC;
            }

            varO /= (Period - 1);
            varC /= (Period - 1);
            double varRS = sumRS / Period;

            double yzVar = Math.Max(0.0, varO + k * varC + (1.0 - k) * varRS);
            double yz = Math.Sqrt(yzVar) * 100.0;

            if (double.IsNaN(yz) || double.IsInfinity(yz))
            {
                _values.Add(null);
            }
            else
            {
                _values.Add((decimal)yz);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
