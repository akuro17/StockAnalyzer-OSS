using System;
using System.Collections.Generic;
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
            _values.Clear();
            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            if (candles.Count < Period || Period <= 0)
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
            int halfPeriod = Period / 2;
            int secondHalfLen = Period - halfPeriod;

            for (int i = Period - 1; i < candles.Count; i++)
            {
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

                if (!priceSeries[i].HasValue)
                {
                    _values.Add(null);
                }
                else
                {
                    decimal currentPrice = priceSeries[i]!.Value;
                    if (frama == null)
                    {
                        frama = currentPrice;
                    }
                    else
                    {
                        frama = alpha * currentPrice + (1m - alpha) * frama.Value;
                    }
                    _values.Add(frama);
                }
            }

            return IndicatorResult.Success(_values);
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            _values.Clear();
            if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

            if (series.Count < Period || Period <= 0)
            {
                for (int j = 0; j < series.Count; j++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            for (int j = 0; j < Period - 1; j++)
            {
                _values.Add(null);
            }

            decimal? frama = null;
            int halfPeriod = Period / 2;
            int secondHalfLen = Period - halfPeriod;

            for (int i = Period - 1; i < series.Count; i++)
            {
                bool hasNull = false;
                decimal h1 = decimal.MinValue, l1 = decimal.MaxValue;
                for (int j = 0; j < halfPeriod; j++)
                {
                    var val = series[i - Period + 1 + j];
                    if (!val.HasValue) { hasNull = true; break; }
                    if (val.Value > h1) h1 = val.Value;
                    if (val.Value < l1) l1 = val.Value;
                }

                decimal h2 = decimal.MinValue, l2 = decimal.MaxValue;
                if (!hasNull)
                {
                    for (int j = 0; j < secondHalfLen; j++)
                    {
                        var val = series[i - Period + 1 + halfPeriod + j];
                        if (!val.HasValue) { hasNull = true; break; }
                        if (val.Value > h2) h2 = val.Value;
                        if (val.Value < l2) l2 = val.Value;
                    }
                }

                decimal h3 = decimal.MinValue, l3 = decimal.MaxValue;
                if (!hasNull)
                {
                    for (int j = 0; j < Period; j++)
                    {
                        var val = series[i - Period + 1 + j];
                        if (val.Value > h3) h3 = val.Value;
                        if (val.Value < l3) l3 = val.Value;
                    }
                }

                if (hasNull || !series[i].HasValue)
                {
                    _values.Add(null);
                    continue;
                }

                decimal n1 = (h1 - l1) / halfPeriod;
                decimal n2 = (h2 - l2) / secondHalfLen;
                decimal n3 = (h3 - l3) / Period;

                double dimen = 1.0;
                if (n1 > 0 && n2 > 0 && n3 > 0)
                {
                    dimen = (Math.Log((double)(n1 + n2)) - Math.Log((double)n3)) / Math.Log(2.0);
                }

                double alphaVal = Math.Exp(W * (dimen - 1));
                alphaVal = Math.Max(0.01, Math.Min(alphaVal, 1.0));
                decimal alpha = (decimal)alphaVal;

                decimal currentPrice = series[i]!.Value;
                if (frama == null)
                {
                    frama = currentPrice;
                }
                else
                {
                    frama = alpha * currentPrice + (1m - alpha) * frama.Value;
                }

                _values.Add(frama);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
