using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.KST)]
    public class CoreKstIndicator : CoreIndicatorBase
    {
        public override string Name => "KST";

        // ROC Periods
        public int Roc1 { get; set; } = 10;
        public int Roc2 { get; set; } = 15;
        public int Roc3 { get; set; } = 20;
        public int Roc4 { get; set; } = 30;

        // SMA Periods
        public int Sma1 { get; set; } = 10;
        public int Sma2 { get; set; } = 10;
        public int Sma3 { get; set; } = 10;
        public int Sma4 { get; set; } = 15;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreKstParameter p)
            {
                Roc1 = p.Roc1; Roc2 = p.Roc2; Roc3 = p.Roc3; Roc4 = p.Roc4;
                Sma1 = p.Sma1; Sma2 = p.Sma2; Sma3 = p.Sma3; Sma4 = p.Sma4;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            var roc1 = CalculateRoc(candles, 10);
            var roc2 = CalculateRoc(candles, 15);
            var roc3 = CalculateRoc(candles, 20);
            var roc4 = CalculateRoc(candles, 30);

            var sma1 = CalculateSma(roc1, 10);
            var sma2 = CalculateSma(roc2, 10);
            var sma3 = CalculateSma(roc3, 10);
            var sma4 = CalculateSma(roc4, 15);

            for (int i = 0; i < candles.Count; i++)
            {
                if (!sma1[i].HasValue || !sma2[i].HasValue || !sma3[i].HasValue || !sma4[i].HasValue)
                { _values.Add(null); continue; }
                _values.Add(sma1[i]!.Value + 2 * sma2[i]!.Value + 3 * sma3[i]!.Value + 4 * sma4[i]!.Value);
            }

            return IndicatorResult.Success(_values);
        }

        private List<decimal?> CalculateRoc(IReadOnlyList<CoreCandleData> candles, int period)
        {
            var result = new List<decimal?>();
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < period) { result.Add(null); continue; }
                decimal prev = candles[i - period].Close;
                result.Add(prev == 0 ? 0 : ((candles[i].Close - prev) / prev) * 100);
            }
            return result;
        }

        private List<decimal?> CalculateSma(List<decimal?> data, int period)
        {
            var result = new List<decimal?>();
            for (int i = 0; i < data.Count; i++)
            {
                if (i < period - 1) { result.Add(null); continue; }

                int count = 0;
                decimal sum = 0;
                bool hasEnoughData = true;

                for (int j = 0; j < period; j++)
                {
                    if (i - j < 0 || !data[i-j].HasValue)
                    {
                        hasEnoughData = false;
                        break;
                    }
                    sum += data[i - j]!.Value;
                    count++;
                }

                if (hasEnoughData && count == period)
                {
                    result.Add(sum / period);
                }
                else
                {
                    result.Add(null);
                }
            }
            return result;
        }
    }
}
