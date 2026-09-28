using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.AnchoredVWAP)]
    public class CoreAnchoredVWAPIndicator : CoreIndicatorBase
    {
        public override string Name => "Anchored VWAP";
        public override bool IsOverlay => true;

        public int AnchorIndex { get; set; } = 0;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreAnchoredVwapParameter p)
            {
                AnchorIndex = p.AnchorIndex;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (AnchorIndex < 0 || AnchorIndex >= candles.Count)
            {
                // Invalid AnchorIndex, fill with nulls
                for(int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            for (int i = 0; i < AnchorIndex; i++)
            {
                _values.Add(null);
            }

            decimal cumulativePriceVolume = 0;
            long cumulativeVolume = 0;

            for (int i = AnchorIndex; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal typicalPrice = (candle.High + candle.Low + candle.Close) / 3;
                cumulativePriceVolume += typicalPrice * candle.Volume;
                cumulativeVolume += candle.Volume;

                if (cumulativeVolume > 0)
                {
                    _values.Add(cumulativePriceVolume / cumulativeVolume);
                }
                else
                {
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
