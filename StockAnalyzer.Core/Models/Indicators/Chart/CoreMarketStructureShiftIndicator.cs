using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using System;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    [StockAnalyzerIndicator(IndicatorType.MarketStructureShift)]
    public class CoreMarketStructureShiftIndicator : CoreIndicatorBase
    {
        public override string Name => "Market Structure Shift (BOS/CHoCH)";

        public IEnumerable<decimal?> BOS_Lines => _bosLines;
        public IEnumerable<decimal?> CHoCH_Lines => _chochLines;

        private readonly List<decimal?> _bosLines = new();
        private readonly List<decimal?> _chochLines = new();
        
        private decimal _zigzagThreshold = 5.0m;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMarketStructureShiftParameter p)
            {
                _zigzagThreshold = p.ZigZagThreshold;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _bosLines.Clear();
            _chochLines.Clear();

            // Initialize empty arrays
            for (int i = 0; i < candles.Count; i++)
            {
                _bosLines.Add(null);
                _chochLines.Add(null);
            }

            if (candles.Count < 3)
            {
                return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
                {
                    { "BOS_Lines", _bosLines },
                    { "CHoCH_Lines", _chochLines }
                }, new List<MarketStructureShift>());
            }

            var candleDataList = candles.Select(c => new CandleData(
                c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume
            )).ToList().AsReadOnly();

            // Detect shifts (using configured threshold)
            var shifts = MarketStructureDetector.Detect(candleDataList, _zigzagThreshold);

            foreach (var shift in shifts)
            {
                // Determine the start index and price level
                int startIndex = 0;
                decimal level = 0;

                if (shift.Type == MarketStructureType.BullishBOS || shift.Type == MarketStructureType.BullishCHoCH)
                {
                    startIndex = shift.PreviousPivotHighIndex;
                    level = shift.PreviousPivotHigh;
                }
                else
                {
                    startIndex = shift.PreviousPivotLowIndex;
                    level = shift.PreviousPivotLow;
                }

                int endIndex = shift.Index;

                // Safety bounds
                startIndex = Math.Max(0, startIndex);
                endIndex = Math.Min(candles.Count - 1, endIndex);

                // Fill the line segment
                for (int i = startIndex; i <= endIndex; i++)
                {
                    if (shift.Type == MarketStructureType.BullishBOS || shift.Type == MarketStructureType.BearishBOS)
                    {
                        _bosLines[i] = level;
                    }
                    else
                    {
                        _chochLines[i] = level;
                    }
                }
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "BOS_Lines", _bosLines },
                { "CHoCH_Lines", _chochLines }
            }, shifts);
        }
    }
}
