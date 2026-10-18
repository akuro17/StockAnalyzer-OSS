using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.MassIndex)]
    public class CoreMassIndexIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 25;
        public int EmaPeriod { get; set; } = 9;

        public override string Name => $"Mass Index ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMassIndexParameter p)
            {
                Period = p.Period;
                EmaPeriod = p.EmaPeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            if (candles.Count < Period || Period <= 0 || EmaPeriod <= 0)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var ranges = new List<decimal?>(candles.Count);
            for (int i = 0; i < candles.Count; i++)
            {
                ranges.Add(candles[i].High - candles[i].Low);
            }

            var singleEma = IndicatorCalculationHelper.CalculateEmaWithNulls(ranges, EmaPeriod);
            var doubleEma = IndicatorCalculationHelper.CalculateEmaWithNulls(singleEma, EmaPeriod);

            var ratios = new List<decimal?>(candles.Count);
            for (int i = 0; i < candles.Count; i++)
            {
                if (!singleEma[i].HasValue || !doubleEma[i].HasValue || doubleEma[i]!.Value == 0m)
                {
                    ratios.Add(null);
                }
                else
                {
                    ratios.Add(singleEma[i]!.Value / doubleEma[i]!.Value);
                }
            }

            int minWarmup = (2 * EmaPeriod - 2) + (Period - 1);
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < minWarmup)
                {
                    _values.Add(null);
                    continue;
                }

                decimal sum = 0m;
                bool hasNull = false;
                for (int j = 0; j < Period; j++)
                {
                    var r = ratios[i - j];
                    if (!r.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    sum += r.Value;
                }

                _values.Add(hasNull ? null : sum);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
