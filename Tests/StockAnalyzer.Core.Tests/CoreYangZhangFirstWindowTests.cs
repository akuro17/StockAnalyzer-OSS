using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Tests
{
    public class CoreYangZhangFirstWindowTests
    {
        private const int Period = 4;

        private static List<CoreCandleData> CreateCandles()
        {
            var start = DateTime.Today;
            var rows = new (decimal O, decimal H, decimal L, decimal C)[]
            {
                (100m, 103m, 98m, 101m),
                (102m, 106m, 100m, 105m),
                (104m, 107m, 101m, 102m),
                (103m, 108m, 102m, 107m),
                (109m, 112m, 105m, 106m),
                (105m, 109m, 103m, 108m),
                (110m, 113m, 107m, 109m),
            };
            var list = new List<CoreCandleData>();
            for (int i = 0; i < rows.Length; i++)
            {
                list.Add(new CoreCandleData(start.AddDays(i), rows[i].O, rows[i].H, rows[i].L, rows[i].C, 1000));
            }
            return list;
        }

        // Independent oracle over bars [first..last] (window of Period bars, all overnight terms defined).
        private static double Oracle(IReadOnlyList<CoreCandleData> c, int last)
        {
            int first = last - Period + 1;
            var o = new double[Period];
            var cc = new double[Period];
            double sumRs = 0.0;
            for (int j = 0; j < Period; j++)
            {
                int m = first + j;
                double open = (double)c[m].Open, high = (double)c[m].High, low = (double)c[m].Low, close = (double)c[m].Close;
                o[j] = Math.Log(open / (double)c[m - 1].Close);
                cc[j] = Math.Log(close / open);
                double u = Math.Log(high / open), d = Math.Log(low / open);
                sumRs += u * (u - cc[j]) + d * (d - cc[j]);
            }
            double meanO = 0, meanC = 0;
            for (int j = 0; j < Period; j++) { meanO += o[j]; meanC += cc[j]; }
            meanO /= Period; meanC /= Period;
            double varO = 0, varC = 0;
            for (int j = 0; j < Period; j++)
            {
                varO += (o[j] - meanO) * (o[j] - meanO);
                varC += (cc[j] - meanC) * (cc[j] - meanC);
            }
            varO /= Period - 1; varC /= Period - 1;
            double k = 0.34 / (1.34 + (double)(Period + 1) / (Period - 1));
            return Math.Sqrt(varO + k * varC + (1.0 - k) * (sumRs / Period)) * 100.0;
        }

        [Fact]
        public void Calculate_FirstBarOvernightIsUndefined_FirstValueAtIndexPeriod()
        {
            var indicator = new CoreYangZhangVolatilityIndicator { Period = Period };
            var candles = CreateCandles();

            indicator.Calculate(candles);

            Assert.Equal(candles.Count, indicator.Values.Count);
            for (int i = 0; i < Period; i++)
            {
                Assert.Null(indicator.Values[i]);
            }
            Assert.NotNull(indicator.Values[Period]);
        }

        [Fact]
        public void Calculate_ValuesFromIndexPeriod_MatchIndependentOracle()
        {
            var indicator = new CoreYangZhangVolatilityIndicator { Period = Period };
            var candles = CreateCandles();

            indicator.Calculate(candles);

            for (int i = Period; i < candles.Count; i++)
            {
                Assert.Equal(Oracle(candles, i), (double)indicator.Values[i]!, 9);
            }
        }
    }
}
