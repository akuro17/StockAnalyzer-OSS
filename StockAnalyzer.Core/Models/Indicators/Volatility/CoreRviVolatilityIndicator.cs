using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.RviVolatility)]
    public class CoreRviVolatilityIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 10;
        public override string Name => $"RVI Vol ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period; 
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            if (candles.Count < 2 * Period - 1 || Period <= 0)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var stdDev = new CoreStandardDeviationIndicator { Period = Period, PriceSource = PriceSource };
            stdDev.Calculate(candles);

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var upStd = new List<decimal?>(candles.Count);
            var downStd = new List<decimal?>(candles.Count);

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1 || !stdDev.Values[i].HasValue || i == 0 || !priceSeries[i].HasValue || !priceSeries[i - 1].HasValue)
                {
                    upStd.Add(null);
                    downStd.Add(null);
                    continue;
                }

                decimal diff = priceSeries[i]!.Value - priceSeries[i - 1]!.Value;
                decimal s = stdDev.Values[i]!.Value;
                if (diff > 0m)
                {
                    upStd.Add(s);
                    downStd.Add(0m);
                }
                else if (diff < 0m)
                {
                    upStd.Add(0m);
                    downStd.Add(s);
                }
                else
                {
                    upStd.Add(0m);
                    downStd.Add(0m);
                }
            }

            decimal? avgUp = null;
            decimal? avgDown = null;
            int seedIndex = 2 * Period - 2;

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < seedIndex)
                {
                    _values.Add(null);
                    continue;
                }

                if (i == seedIndex)
                {
                    decimal sumUp = 0m;
                    decimal sumDown = 0m;
                    bool hasNull = false;

                    for (int k = Period - 1; k <= seedIndex; k++)
                    {
                        if (!upStd[k].HasValue || !downStd[k].HasValue)
                        {
                            hasNull = true;
                            break;
                        }
                        sumUp += upStd[k]!.Value;
                        sumDown += downStd[k]!.Value;
                    }

                    if (hasNull)
                    {
                        _values.Add(null);
                    }
                    else
                    {
                        avgUp = sumUp / Period;
                        avgDown = sumDown / Period;
                        decimal total = avgUp.Value + avgDown.Value;
                        _values.Add(total == 0m ? IndicatorCalculationHelper.BoundedOscillatorNeutral : 100m * (avgUp.Value / total));
                    }
                }
                else
                {
                    if (avgUp.HasValue && avgDown.HasValue && upStd[i].HasValue && downStd[i].HasValue)
                    {
                        avgUp = (avgUp.Value * (Period - 1) + upStd[i]!.Value) / Period;
                        avgDown = (avgDown.Value * (Period - 1) + downStd[i]!.Value) / Period;

                        decimal total = avgUp.Value + avgDown.Value;
                        _values.Add(total == 0m ? IndicatorCalculationHelper.BoundedOscillatorNeutral : 100m * (avgUp.Value / total));
                    }
                    else
                    {
                        _values.Add(null);
                    }
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
