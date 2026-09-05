using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.ElderRay)]
    public class CoreElderRayIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 13;
        public override string Name => $"ElderRay ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }
        public List<decimal?> BullPower { get; } = new();
        public List<decimal?> BearPower { get; } = new();

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
 BullPower.Clear(); BearPower.Clear();
            var candleDataList = candles.ToList();

            var ema = new CoreEmaIndicator { Period = Period, PriceSource = PriceSource };
            ema.Calculate(candles);

            for (int i = 0; i < candles.Count; i++)
            {
                if (!ema.Values[i].HasValue) { BullPower.Add(null); BearPower.Add(null); _values.Add(null); continue; }
                decimal bull = candles[i].High - ema.Values[i]!.Value;
                decimal bear = candles[i].Low - ema.Values[i]!.Value;
                BullPower.Add(bull); BearPower.Add(bear); _values.Add(bull + bear);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
