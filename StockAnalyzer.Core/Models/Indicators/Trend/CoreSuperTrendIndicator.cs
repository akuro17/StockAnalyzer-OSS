using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.SuperTrend)]
    public class CoreSuperTrendIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 10;
        public decimal Multiplier { get; set; } = 3.0m;

        public override string Name => $"SuperTrend ({Period}, {Multiplier})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSuperTrendParameter p)
            {
                Period = p.Period;
                Multiplier = p.Multiplier;
            }
        }

        public List<bool> IsUpTrend { get; } = new();

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            IsUpTrend.Clear();
            var candleDataList = candles.ToList();
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            var atr = new CoreAtrIndicator { Period = Period };
            atr.Calculate(candles);

            decimal? upperBand = null;
            decimal? lowerBand = null;

            // The trend is stateful. It's either up or down.
            // We can't just re-evaluate it on every candle without knowing the previous state.
            // Let's track it explicitly.
            bool currentTrendIsUp = true;

            for (int i = 0; i < candles.Count; i++)
            {
                if (i == 0 || !atr.Values[i].HasValue)
                {
                    _values.Add(null);
                    IsUpTrend.Add(true); // Default to uptrend
                    continue;
                }

                decimal hl2 = (candles[i].High + candles[i].Low) / 2;
                decimal basicUpperBand = hl2 + Multiplier * atr.Values[i]!.Value;
                decimal basicLowerBand = hl2 - Multiplier * atr.Values[i]!.Value;

                decimal prevClose = candles[i - 1].Close;

                // If this is the first valid value, initialize bands
                if(upperBand == null)
                {
                    upperBand = basicUpperBand;
                    lowerBand = basicLowerBand;
                }
                else
                {
                    // Update upper band
                    upperBand = (basicUpperBand < upperBand || prevClose > upperBand) ? basicUpperBand : upperBand;

                    // Update lower band
                    lowerBand = (basicLowerBand > lowerBand || prevClose < lowerBand) ? basicLowerBand : lowerBand;
                }

                // Determine trend
                if (candles[i].Close > upperBand)
                {
                    currentTrendIsUp = true;
                }
                else if (candles[i].Close < lowerBand)
                {
                    currentTrendIsUp = false;
                }
                // else, trend remains the same

                _values.Add(currentTrendIsUp ? lowerBand : upperBand);
                IsUpTrend.Add(currentTrendIsUp);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
