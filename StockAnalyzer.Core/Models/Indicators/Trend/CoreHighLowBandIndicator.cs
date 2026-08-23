using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.HighLowBand)]
    public class CoreHighLowBandIndicator : CoreIndicatorBase
    {
        public override string Name => "High/Low Band";

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

            var highValues = new List<decimal?>();
            var midValues = new List<decimal?>();
            var lowValues = new List<decimal?>();

            for (int i = 0; i < Period - 1; i++)
            {
                highValues.Add(null);
                midValues.Add(null);
                lowValues.Add(null);
            }

            for (int i = Period - 1; i < candles.Count; i++)
            {
                decimal highestHigh = decimal.MinValue, lowestLow = decimal.MaxValue;
                for (int j = 0; j < Period; j++)
                {
                    var c = candles[i - Period + 1 + j];
                    if (c.High > highestHigh) highestHigh = c.High;
                    if (c.Low < lowestLow) lowestLow = c.Low;
                }
                var middle = (highestHigh + lowestLow) / 2;
                
                highValues.Add(highestHigh);
                midValues.Add(middle);
                lowValues.Add(lowestLow);
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "High", highValues },
                { "Mid", midValues },
                { "Low", lowValues }
            });
        }
    }
}
