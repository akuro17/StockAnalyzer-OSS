using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ZeroAllocation;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Strategies
{
    public class PointAndFigureChartStrategy : IChartStrategy
    {
        public ChartType TargetType => ChartType.PointAndFigure;

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

                decimal boxSize = AutoBoxSizeCalculator.Calculate(
                    parameters.Mode,
                    candleList,
                    parameters.ManualSize,
                    parameters.AtrPeriod,
                    parameters.AtrMultiplier,
                    (ChartRoundingMode)parameters.RoundingMode,
                    (AutoFallbackMode)parameters.FallbackMode,
                    candleList[^1].Close,
                    parameters.Percentage);

                var param = new PointAndFigureParameters(boxSize, parameters.PnfReversal, (ChartRoundingMode)parameters.RoundingMode);
                var adapter = PointAndFigureCalculator.Calculate(zeroAllocCandles.AsMemory(0, candleList.Count), param);
                
                return new ChartStrategyResult(adapter, boxSize);
            }
            finally
            {
                System.Buffers.ArrayPool<ZeroAllocCandleData>.Shared.Return(zeroAllocCandles, clearArray: true);
            }
        }
    }
}
