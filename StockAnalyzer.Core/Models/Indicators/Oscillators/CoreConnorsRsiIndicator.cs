using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.ConnorsRsi)]
    public class CoreConnorsRsiIndicator : CoreIndicatorBase
    {
        public int RsiPeriod { get; set; } = 3;
        public int StreakPeriod { get; set; } = 2;
        public int PercentRankPeriod { get; set; } = 100;
        public override string Name => "ConnorsRSI";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreConnorsRsiParameter p)
            {
                RsiPeriod = p.RsiPeriod;
                StreakPeriod = p.StreakPeriod;
                PercentRankPeriod = p.PercentRankPeriod;
            }
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            if (PercentRankPeriod <= 0 || series.Count < PercentRankPeriod + 1)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            // Calculate streak (null whenever adjacent prices are unavailable)
            var streakSeries = new List<decimal?>(series.Count) { series[0].HasValue ? 0m : null };
            for (int i = 1; i < series.Count; i++)
            {
                if (!series[i].HasValue || !series[i - 1].HasValue)
                {
                    streakSeries.Add(null);
                    continue;
                }

                int prev = streakSeries[i - 1] is { } prevStreak ? (int)prevStreak : 0;
                decimal currPrice = series[i]!.Value;
                decimal prevPrice = series[i - 1]!.Value;
                if (currPrice > prevPrice) streakSeries.Add(prev >= 0 ? prev + 1m : 1m);
                else if (currPrice < prevPrice) streakSeries.Add(prev <= 0 ? prev - 1m : -1m);
                else streakSeries.Add(0m);
            }

            // RSI of price
            var rsi = new CoreRsiIndicator { Period = RsiPeriod };
            rsi.CalculateSeries(series);

            // RSI of streak
            var streakRsi = new CoreRsiIndicator { Period = StreakPeriod };
            streakRsi.CalculateSeries(streakSeries);

            // Percent rank of ROC
            var roc = new CoreRocIndicator { Period = 1 };
            roc.CalculateSeries(series);

            for (int i = 0; i < series.Count; i++)
            {
                if (i < PercentRankPeriod
                    || !rsi.Values[i].HasValue
                    || !streakRsi.Values[i].HasValue
                    || !roc.Values[i].HasValue)
                {
                    _values.Add(null);
                    continue;
                }

                bool windowHasNullPrice = false;
                for (int k = i - PercentRankPeriod; k <= i; k++)
                {
                    if (!series[k].HasValue)
                    {
                        windowHasNullPrice = true;
                        break;
                    }
                }

                if (windowHasNullPrice)
                {
                    _values.Add(null);
                    continue;
                }

                decimal currentRoc = roc.Values[i]!.Value;
                int count = 0;
                for (int j = i - PercentRankPeriod; j < i; j++)
                    if (roc.Values[j].HasValue && roc.Values[j]!.Value < currentRoc) count++;
                decimal percentRank = (decimal)count / PercentRankPeriod * 100m;

                _values.Add((rsi.Values[i]!.Value + streakRsi.Values[i]!.Value + percentRank) / 3m);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
