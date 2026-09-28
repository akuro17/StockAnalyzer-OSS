using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.ThreeLineBreakSignal)]
    public class CoreThreeLineBreakSignalIndicator : CoreIndicatorBase
    {
        public override string Name => "Three Line Break Signal";


        public int NumberOfLines { get; set; } = 3;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreThreeLineBreakParameter p)
            {
                NumberOfLines = p.NumberOfLines;
            }
        }

        private class TlbBlock
        {
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public bool IsUp { get; set; }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var values = new decimal?[candles.Count];
            if (candles.Count == 0) return IndicatorResult.Success(values);

            var blocks = new List<TlbBlock>();
            
            // Initialization phase (find first trend)
            int startIndex = 0;
            bool? isUpTrend = null;
            decimal firstBlockOpen = candles[0].Close;

            for (int i = 1; i < candles.Count; i++)
            {
                if (candles[i].Close > firstBlockOpen)
                {
                    isUpTrend = true;
                    startIndex = i;
                    blocks.Add(new TlbBlock { High = candles[i].Close, Low = firstBlockOpen, IsUp = true });
                    break;
                }
                if (candles[i].Close < firstBlockOpen)
                {
                    isUpTrend = false;
                    startIndex = i;
                    blocks.Add(new TlbBlock { High = firstBlockOpen, Low = candles[i].Close, IsUp = false });
                    break;
                }
            }

            if (isUpTrend == null)
            {
                // Never established a trend
                for (int i = 0; i < candles.Count; i++) values[i] = 0;
                return IndicatorResult.Success(values);
            }

            // Fill until startIndex with 0
            for (int i = 0; i <= startIndex; i++)
            {
                values[i] = 0;
            }

            // Process remaining candles
            for (int i = startIndex + 1; i < candles.Count; i++)
            {
                decimal currentClose = candles[i].Close;
                var lastBlock = blocks[blocks.Count - 1];
                bool currentIsUp = lastBlock.IsUp;
                values[i] = 0; // Default no signal

                if (currentIsUp)
                {
                    if (currentClose > lastBlock.High)
                    {
                        // Continuation Up
                        blocks.Add(new TlbBlock { High = currentClose, Low = lastBlock.High, IsUp = true });
                    }
                    else
                    {
                        // Check for reversal
                        int lookbackCount = System.Math.Min(blocks.Count, NumberOfLines);
                        decimal reversalLow = decimal.MaxValue;
                        for (int b = blocks.Count - lookbackCount; b < blocks.Count; b++)
                        {
                            if (blocks[b].Low < reversalLow) reversalLow = blocks[b].Low;
                        }

                        if (currentClose < reversalLow)
                        {
                            // Reversed Down
                            blocks.Add(new TlbBlock { High = reversalLow, Low = currentClose, IsUp = false });
                            values[i] = -1; // Bearish Signal
                        }
                    }
                }
                else
                {
                    if (currentClose < lastBlock.Low)
                    {
                        // Continuation Down
                        blocks.Add(new TlbBlock { High = lastBlock.Low, Low = currentClose, IsUp = false });
                    }
                    else
                    {
                        // Check for reversal
                        int lookbackCount = System.Math.Min(blocks.Count, NumberOfLines);
                        decimal reversalHigh = decimal.MinValue;
                        for (int b = blocks.Count - lookbackCount; b < blocks.Count; b++)
                        {
                            if (blocks[b].High > reversalHigh) reversalHigh = blocks[b].High;
                        }

                        if (currentClose > reversalHigh)
                        {
                            // Reversed Up
                            blocks.Add(new TlbBlock { High = currentClose, Low = reversalHigh, IsUp = true });
                            values[i] = 1; // Bullish Signal
                        }
                    }
                }
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "Histogram", values.ToList() }
            });
        }
    }
}
