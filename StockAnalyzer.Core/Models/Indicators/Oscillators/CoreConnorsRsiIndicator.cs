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

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < PercentRankPeriod + 1) { for (int i = 0; i < candles.Count; i++) _values.Add(null); return IndicatorResult.Success(_values); }

            // Calculate streak
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var streak = new List<int> { 0 };
            for (int i = 1; i < candles.Count; i++)
            {
                int prev = streak[i - 1];
                if ((priceSeries[i] ?? 0m) > (priceSeries[i - 1] ?? 0m)) streak.Add(prev >= 0 ? prev + 1 : 1);
                else if ((priceSeries[i] ?? 0m) < (priceSeries[i - 1] ?? 0m)) streak.Add(prev <= 0 ? prev - 1 : -1);
                else streak.Add(0);
            }

            // RSI of price
            var rsi = new CoreRsiIndicator { Period = RsiPeriod, PriceSource = PriceSource };
            rsi.Calculate(candles);

            // RSI of streak
            var streakCandles = streak.Select((s, i) => new CoreCandleData(candles[i].Timestamp, s, s, s, s, 0)).ToList();
            var streakRsi = new CoreRsiIndicator { Period = StreakPeriod };
            streakRsi.Calculate(streakCandles);

            // Percent rank of ROC
            var roc = new CoreRocIndicator { Period = 1, PriceSource = PriceSource };
            roc.Calculate(candles);

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < PercentRankPeriod || !rsi.Values[i].HasValue || !streakRsi.Values[i].HasValue)
                { _values.Add(null); continue; }

                // Percent rank
                decimal currentRoc = roc.Values[i] ?? 0;
                int count = 0;
                for (int j = i - PercentRankPeriod; j < i; j++)
                    if (roc.Values[j].HasValue && roc.Values[j]!.Value < currentRoc) count++;
                decimal percentRank = (decimal)count / PercentRankPeriod * 100;

                _values.Add((rsi.Values[i]!.Value + streakRsi.Values[i]!.Value + percentRank) / 3);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
