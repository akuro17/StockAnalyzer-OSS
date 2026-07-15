using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ZeroAllocation;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Strategies
{
    public class KagiChartStrategy : IChartStrategy
    {
        public ChartType TargetType => ChartType.Kagi;

        public ChartStrategyResult Calculate(IEnumerable<CoreCandleData> candles, ChartStrategyParameters parameters)
        {
            if (candles == null || !candles.Any())
            {
                // Return empty/null or throw? ViewModel handled null.
                // Let's return null like the original code or a safe empty adapter.
                // Original: KagiDataAdapter = null;
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

                decimal reversalAmount = AutoBoxSizeCalculator.Calculate(
                    parameters.Mode,
                    candleList,
                    parameters.ManualSize,
                    parameters.AtrPeriod,
                    parameters.AtrMultiplier,
                    (ChartRoundingMode)parameters.RoundingMode,
                    (AutoFallbackMode)parameters.FallbackMode,
                    candleList[^1].Close,
                    parameters.Percentage);

                var param = new KagiParameters(reversalAmount, (ChartRoundingMode)parameters.RoundingMode);
                var adapter = KagiCalculator.Calculate(zeroAllocCandles.AsMemory(0, candleList.Count), param);
                
                return new ChartStrategyResult(adapter, reversalAmount);
            }
            finally
            {
                System.Buffers.ArrayPool<ZeroAllocCandleData>.Shared.Return(zeroAllocCandles, clearArray: true);
            }
        }
    }
}
