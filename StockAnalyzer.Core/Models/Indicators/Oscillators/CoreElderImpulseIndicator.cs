using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.ElderImpulse)]
    public class CoreElderImpulseIndicator : CoreIndicatorBase
    {
        public override string Name => "Elder Impulse";

        public int EmaPeriod { get; set; } = 13;
        public int MacdFastPeriod { get; set; } = 12;
        public int MacdSlowPeriod { get; set; } = 26;
        public int MacdSignalPeriod { get; set; } = 9;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreElderImpulseParameter p)
            {
                EmaPeriod = p.EmaPeriod;
                MacdFastPeriod = p.MacdFastPeriod;
                MacdSlowPeriod = p.MacdSlowPeriod;
                MacdSignalPeriod = p.MacdSignalPeriod;
            }
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            _values.Clear();

            if (series == null || series.Count == 0)
            {
                return IndicatorResult.Success(_values);
            }

            if (EmaPeriod <= 0 || MacdFastPeriod <= 0 || MacdSlowPeriod <= 0 || MacdSignalPeriod <= 0)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var ema13 = IndicatorCalculationHelper.CalculateEmaWithNulls(series, EmaPeriod);
            var macdHistogram = CalculateMacdHistogram(series, MacdFastPeriod, MacdSlowPeriod, MacdSignalPeriod);

            for (int i = 0; i < series.Count; i++)
            {
                if (i == 0 || !series[i].HasValue || !series[i - 1].HasValue ||
                    ema13[i] == null || ema13[i - 1] == null ||
                    macdHistogram[i] == null || macdHistogram[i - 1] == null)
                {
                    _values.Add(null);
                    continue;
                }

                bool isEmaRising = ema13[i] > ema13[i - 1];
                bool isMacdHistRising = macdHistogram[i] > macdHistogram[i - 1];

                if (isEmaRising && isMacdHistRising)
                {
                    _values.Add(1); // Green
                }
                else if (!isEmaRising && !isMacdHistRising)
                {
                    _values.Add(-1); // Red
                }
                else
                {
                    _values.Add(0); // Blue
                }
            }

            return IndicatorResult.Success(_values);
        }

        private static List<decimal?> CalculateMacdHistogram(IReadOnlyList<decimal?> prices, int fastPeriod, int slowPeriod, int signalPeriod)
        {
            var fastEma = IndicatorCalculationHelper.CalculateEmaWithNulls(prices, fastPeriod);
            var slowEma = IndicatorCalculationHelper.CalculateEmaWithNulls(prices, slowPeriod);

            var macdLine = new List<decimal?>(prices.Count);
            for (int i = 0; i < prices.Count; i++)
            {
                if (fastEma[i] != null && slowEma[i] != null)
                {
                    macdLine.Add(fastEma[i]!.Value - slowEma[i]!.Value);
                }
                else
                {
                    macdLine.Add(null);
                }
            }

            var signalLine = IndicatorCalculationHelper.CalculateEmaWithNulls(macdLine, signalPeriod);

            var histogram = new List<decimal?>(macdLine.Count);
            for (int i = 0; i < macdLine.Count; i++)
            {
                if (macdLine[i] != null && signalLine[i] != null)
                {
                    histogram.Add(macdLine[i]!.Value - signalLine[i]!.Value);
                }
                else
                {
                    histogram.Add(null);
                }
            }
            return histogram;
        }
    }
}

