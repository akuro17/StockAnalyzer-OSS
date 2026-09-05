using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.FRAMA)]
    public class CoreFRAMAIndicator : CoreIndicatorBase
    {
        public override bool IsOverlay => true;
        
        public override string Name => "Fractal Adaptive Moving Average";

        public int Period { get; set; } = 16;
        private const double W = -4.6;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreFRAMAParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (candles.Count < Period)
            {
                for (int j = 0; j < candles.Count; j++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            for (int j = 0; j < Period - 1; j++)
            {
                _values.Add(null);
            }

            decimal? frama = null;
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

            for (int i = Period - 1; i < candles.Count; i++)
            {
                int halfPeriod = Period / 2;
                int secondHalfLen = Period - halfPeriod;

                decimal h1 = decimal.MinValue, l1 = decimal.MaxValue;
                for (int j = 0; j < halfPeriod; j++)
                {
                    var c = candles[i - Period + 1 + j];
                    if (c.High > h1) h1 = c.High;
                    if (c.Low < l1) l1 = c.Low;
                }
                decimal n1 = (h1 - l1) / halfPeriod;

                decimal h2 = decimal.MinValue, l2 = decimal.MaxValue;
                for (int j = 0; j < secondHalfLen; j++)
                {
                    var c = candles[i - Period + 1 + halfPeriod + j];
                    if (c.High > h2) h2 = c.High;
                    if (c.Low < l2) l2 = c.Low;
                }
                decimal n2 = (h2 - l2) / secondHalfLen;

                decimal h3 = decimal.MinValue, l3 = decimal.MaxValue;
                for (int j = 0; j < Period; j++)
                {
                    var c = candles[i - Period + 1 + j];
                    if (c.High > h3) h3 = c.High;
                    if (c.Low < l3) l3 = c.Low;
                }
                decimal n3 = (h3 - l3) / Period;

                double dimen = 1.0;
                if (n1 > 0 && n2 > 0 && n3 > 0)
                {
                    dimen = (Math.Log((double)(n1 + n2)) - Math.Log((double)n3)) / Math.Log(2.0);
                }

                double alphaVal = Math.Exp(W * (dimen - 1));
                alphaVal = Math.Max(0.01, Math.Min(alphaVal, 1.0));
                decimal alpha = (decimal)alphaVal;

                decimal currentPrice = priceSeries[i] ?? 0m;

                if (frama == null)
                {
                    frama = currentPrice;
                }
                else
                {
                    frama = alpha * currentPrice + (1 - alpha) * frama;
                }

                _values.Add(frama);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
