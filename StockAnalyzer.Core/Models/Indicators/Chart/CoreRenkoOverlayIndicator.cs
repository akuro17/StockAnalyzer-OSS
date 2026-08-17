using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    public class CoreRenkoOverlayIndicator : CoreIndicatorBase
    {
        public override string Name => "Renko Overlay";

        public decimal BrickSize { get; set; } = 10;

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            decimal? lastBrickPrice = null;

            foreach (var candle in candles)
            {
                if (lastBrickPrice == null)
                {
                    lastBrickPrice = candle.Close;
                    _values.Add(lastBrickPrice);
                    continue;
                }

                var priceMove = candle.Close - lastBrickPrice.Value;
                int numBricks = (int)(Math.Abs(priceMove) / BrickSize);

                if (numBricks > 0)
                {
                    var direction = Math.Sign(priceMove);
                    lastBrickPrice += direction * numBricks * BrickSize;
                }

                _values.Add(lastBrickPrice);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
