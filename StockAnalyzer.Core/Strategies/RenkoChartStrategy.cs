using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ZeroAllocation;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Strategies
{
    public class RenkoChartStrategy : IChartStrategy
    {
        public ChartType TargetType => ChartType.Renko;

        public ChartStrategyResult Calculate(IEnumerable<CoreCandleData> candles, ChartStrategyParameters parameters)
        {
             if (candles == null || !candles.Any())
            {
                return null!;
            }

            var candleList = candles as IReadOnlyList<CoreCandleData> ?? candles.ToList();

            // Convert CoreCandleData to ZeroAllocCandleData using rented array
            var zeroAllocCandles = System.Buffers.ArrayPool<ZeroAllocCandleData>.Shared.Rent(candleList.Count);
            try
            {
                for (int i = 0; i < candleList.Count; i++)
                {
                    var c = candleList[i];
                    zeroAllocCandles[i] = new ZeroAllocCandleData(
                        c.Timestamp,
                        c.Open, c.High, c.Low, c.Close, (long)c.Volume
                    );
                }

                decimal brickSize = AutoBoxSizeCalculator.Calculate(
                    parameters.Mode,
                    candleList,
                    parameters.ManualSize,
                    parameters.AtrPeriod,
                    parameters.AtrMultiplier,
                    (ChartRoundingMode)parameters.RoundingMode,
                    (AutoFallbackMode)parameters.FallbackMode,
                    candleList[^1].Close,
                    parameters.Percentage);

                var param = new RenkoParameters(brickSize, parameters.RenkoReversal, (ChartRoundingMode)parameters.RoundingMode);
                var adapter = RenkoCalculator.Calculate(zeroAllocCandles.AsMemory(0, candleList.Count), param);

                return new ChartStrategyResult(adapter, brickSize);
            }
            finally
            {
                System.Buffers.ArrayPool<ZeroAllocCandleData>.Shared.Return(zeroAllocCandles, clearArray: true);
            }
        }
    }
}
