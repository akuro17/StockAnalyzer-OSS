using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    public class CorePointAndFigureOverlayIndicator : CoreIndicatorBase
    {
        public override string Name => "Point and Figure Overlay";

        public decimal BoxSize { get; set; } = 1;
        public int ReversalAmount { get; set; } = 3;

        private readonly List<PnfSignal> _signalsBuffer = new(32);
        private readonly List<PnfTrendline> _activeLinesBuffer = new(32);

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles == null || candles.Count == 0)
                return IndicatorResult.Empty();

            // Calculate P&F Columns (ZeroAllocation path)
            var pnfColumns = PointAndFigureConverter.Convert(candles, BoxSize, ReversalAmount);
            
            // Run Pattern Analysis
            var pnfAnalysis = PointAndFigurePatternEngine.Analyze(pnfColumns, BoxSize, _signalsBuffer, _activeLinesBuffer);

            // Populate traditional staircase values (for overlay line)
            // Note: We use the last known column price for each original candle index
            int currentColIdx = 0;
            DateTime? nextColTime = pnfColumns.Count > 1 ? pnfColumns[1].Timestamp : null;

            foreach (var candle in candles)
            {
                // Advance current column if the candle timestamp has reached the next column
                while (nextColTime.HasValue && candle.Timestamp >= nextColTime.Value && currentColIdx < pnfColumns.Count - 1)
                {
                    currentColIdx++;
                    nextColTime = currentColIdx < pnfColumns.Count - 1 ? pnfColumns[currentColIdx + 1].Timestamp : null;
                }
                
                _values.Add(pnfColumns[currentColIdx].Close);
            }

            // Return result with P&F analysis in CustomData
            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { IndicatorResult.MainSeriesName, _values }
            }, pnfAnalysis);
        }
    }
}
