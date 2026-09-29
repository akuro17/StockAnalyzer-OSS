using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    public class CoreKagiOverlayIndicator : CoreIndicatorBase
    {
        public override string Name => "Kagi Overlay";

        public decimal ReversalAmount { get; set; } = 0.04m; // 4%

        private class KagiLine
        {
            public decimal Start { get; set; }
            public decimal End { get; set; }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            var kagiLines = new List<KagiLine>();
            kagiLines.Add(new KagiLine { Start = candles[0].Close, End = candles[0].Close });
            _values.Add(candles[0].Close);

            bool isUp = true;

            for(int i = 1; i < candles.Count; i++)
            {
                var currentPrice = candles[i].Close;
                var lastLine = kagiLines.Last();
                var reversal = lastLine.End * ReversalAmount;

                if (isUp)
                {
                    if (currentPrice > lastLine.End)
                    {
                        lastLine.End = currentPrice;
                    }
                    else if (currentPrice < lastLine.End - reversal)
                    {
                        kagiLines.Add(new KagiLine { Start = lastLine.End, End = currentPrice });
                        isUp = false;
                    }
                }
                else // isDown
                {
                    if (currentPrice < lastLine.End)
                    {
                        lastLine.End = currentPrice;
                    }
                    else if (currentPrice > lastLine.End + reversal)
                    {
                        kagiLines.Add(new KagiLine { Start = lastLine.End, End = currentPrice });
                        isUp = true;
                    }
                }
                _values.Add(kagiLines.Last().End);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
