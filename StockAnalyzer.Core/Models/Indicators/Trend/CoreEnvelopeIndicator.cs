using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.Envelope)]
    public class CoreEnvelopeIndicator : CoreIndicatorBase
    {
        public override string Name => "Envelope";

        public int Period { get; set; } = 20;
        public decimal Deviation { get; set; } = 0.025m; // 2.5%

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreEnvelopeParameter p)
            {
                Period = p.Period;
                Deviation = p.Deviation;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var upper = new List<decimal?>(candles.Count);
            var main = new List<decimal?>(candles.Count);
            var lower = new List<decimal?>(candles.Count);

            if (candles.Count < Period)
            {
                for (int i = 0; i < candles.Count; i++)
                {
                    upper.Add(null); main.Add(null); lower.Add(null);
                }
                return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { "Upper", upper }, { "Main", main }, { "Lower", lower } });
            }

            var closePrices = candles.Select(c => c.Close).ToList();

            decimal sum = 0;
            for (int i = 0; i < Period; i++)
            {
                 sum += closePrices[i];
                 upper.Add(null); main.Add(null); lower.Add(null);
            }
            
            decimal sma = sum / Period;
            main[Period - 1] = sma;
            upper[Period - 1] = sma * (1 + Deviation);
            lower[Period - 1] = sma * (1 - Deviation);

            for (int i = Period; i < candles.Count; i++)
            {
                sum = sum - closePrices[i - Period] + closePrices[i];
                sma = sum / Period;
                main.Add(sma);
                upper.Add(sma * (1 + Deviation));
                lower.Add(sma * (1 - Deviation));
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { "Upper", upper }, { "Main", main }, { "Lower", lower } });
        }
    }
}
