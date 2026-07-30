using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.StandardDeviation)]
    public class CoreStandardDeviationIndicator : CoreIndicatorBase
    {
        public override string Name => $"StdDev({Period})";
        public int Period { get; set; } = 20;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period; 
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (candles.Count < Period)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                decimal sum = 0m;
                int start = i - Period + 1;
                for (int j = 0; j < Period; j++)
                {
                    sum += candles[start + j].Close;
                }
                decimal mean = sum / Period;

                decimal sumSqDiff = 0m;
                for (int j = 0; j < Period; j++)
                {
                    decimal diff = candles[start + j].Close - mean;
                    sumSqDiff += diff * diff;
                }
                decimal variance = sumSqDiff / (Period - 1);
                _values.Add((decimal)System.Math.Sqrt((double)variance));
            }

            return IndicatorResult.Success(_values);
        }
    }
}
