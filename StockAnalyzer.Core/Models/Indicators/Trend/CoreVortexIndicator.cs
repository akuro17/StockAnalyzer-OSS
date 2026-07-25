using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.Vortex)]
    public class CoreVortexIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"Vortex ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }
        public List<decimal?> VIPlus { get; } = new();
        public List<decimal?> VIMinus { get; } = new();

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
 VIPlus.Clear(); VIMinus.Clear();
            var candleDataList = candles.ToList();
            if (candles.Count < 2) return IndicatorResult.Success(_values);
            var vmPlus = new List<decimal> { 0 };
            var vmMinus = new List<decimal> { 0 };
            var tr = new List<decimal> { candles[0].High - candles[0].Low };

            for (int i = 1; i < candles.Count; i++)
            {
                vmPlus.Add(Math.Abs(candles[i].High - candles[i - 1].Low));
                vmMinus.Add(Math.Abs(candles[i].Low - candles[i - 1].High));
                decimal trueRange = Math.Max(candles[i].High - candles[i].Low,
                    Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close), Math.Abs(candles[i].Low - candles[i - 1].Close)));
                tr.Add(trueRange);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period) { _values.Add(null); VIPlus.Add(null); VIMinus.Add(null); continue; }
                decimal sumVmPlus = 0, sumVmMinus = 0, sumTr = 0;
                for (int j = 0; j < Period; j++) { sumVmPlus += vmPlus[i - j]; sumVmMinus += vmMinus[i - j]; sumTr += tr[i - j]; }
                if (sumTr == 0) { VIPlus.Add(null); VIMinus.Add(null); _values.Add(null); }
                else { VIPlus.Add(sumVmPlus / sumTr); VIMinus.Add(sumVmMinus / sumTr); _values.Add(sumVmPlus / sumTr - sumVmMinus / sumTr); }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
