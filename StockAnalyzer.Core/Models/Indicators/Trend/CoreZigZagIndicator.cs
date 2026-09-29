using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.ZigZag)]
    public class CoreZigZagIndicator : CoreIndicatorBase
    {
        public decimal Threshold { get; set; } = 5m; // Percentage
        public override string Name => $"ZigZag ({Threshold}%)";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreZigZagParameter p)
            {
                Threshold = p.Threshold;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            for (int i = 0; i < candles.Count; i++) _values.Add(null);

            if (candles.Count == 0) return IndicatorResult.Success(_values);
            int lastPivotIndex = 0;
            decimal lastPivotValue = candles[0].Close;
            _values[0] = lastPivotValue;
            bool isUpTrend = true;

            for (int i = 1; i < candles.Count; i++)
            {
                decimal price = candles[i].Close;
                decimal change = (price - lastPivotValue) / lastPivotValue * 100;

                if (isUpTrend)
                {
                    if (price > lastPivotValue)
                    {
                        _values[lastPivotIndex] = null;
                        lastPivotIndex = i;
                        lastPivotValue = price;
                        _values[i] = price;
                    }
                    else if (change <= -Threshold)
                    {
                        isUpTrend = false;
                        lastPivotIndex = i;
                        lastPivotValue = price;
                        _values[i] = price;
                    }
                }
                else // Down trend
                {
                    if (price < lastPivotValue)
                    {
                        _values[lastPivotIndex] = null;
                        lastPivotIndex = i;
                        lastPivotValue = price;
                        _values[i] = price;
                    }
                    else if (change >= Threshold)
                    {
                        isUpTrend = true;
                        lastPivotIndex = i;
                        lastPivotValue = price;
                        _values[i] = price;
                    }
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
