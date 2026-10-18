using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
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
            _values.Clear();
            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var roc1 = CalculateRoc(priceSeries, Roc1);
            var roc2 = CalculateRoc(priceSeries, Roc2);
            var roc3 = CalculateRoc(priceSeries, Roc3);
            var roc4 = CalculateRoc(priceSeries, Roc4);

            var sma1 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc1, Sma1);
            var sma2 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc2, Sma2);
            var sma3 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc3, Sma3);
            var sma4 = IndicatorCalculationHelper.CalculateSmaWithNulls(roc4, Sma4);

            for (int i = 0; i < candles.Count; i++)
            {
                if (!sma1[i].HasValue || !sma2[i].HasValue || !sma3[i].HasValue || !sma4[i].HasValue)
                {
                    _values.Add(null);
                    continue;
                }
                _values.Add(sma1[i]!.Value + 2m * sma2[i]!.Value + 3m * sma3[i]!.Value + 4m * sma4[i]!.Value);
            }

            return IndicatorResult.Success(_values);
        }

        private static List<decimal?> CalculateRoc(IReadOnlyList<decimal?> priceSeries, int period)
        {
            var result = new List<decimal?>(priceSeries.Count);
            for (int i = 0; i < priceSeries.Count; i++)
            {
                if (i < period || !priceSeries[i].HasValue || !priceSeries[i - period].HasValue)
                {
                    result.Add(null);
                    continue;
                }
                decimal past = priceSeries[i - period]!.Value;
                if (past == 0m)
                {
                    result.Add(null);
                }
                else
                {
                    result.Add(((priceSeries[i]!.Value - past) / past) * 100m);
                }
            }
            return result;
        }
    }
}
