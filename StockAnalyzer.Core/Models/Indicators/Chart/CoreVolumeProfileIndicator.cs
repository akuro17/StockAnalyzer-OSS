using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Analysis;
using System;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    [StockAnalyzerIndicator(IndicatorType.VolumeProfile)]
    public class CoreVolumeProfileIndicator : CoreIndicatorBase
    {
        public override string Name => "Volume Profile";
        
        public int RowCount { get; set; } = 50;
        public int Period { get; set; } = 200;
        public VolumeDistributionMode Mode { get; set; } = VolumeDistributionMode.Proportional;

        // Exposed for Renderer
        public List<VolumeBin> ProfileData { get; private set; } = new();

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreVolumeProfileParameter p)
            {
                RowCount = p.RowCount;
                Period = p.Period;
                Mode = p.Mode;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            // Determine range for profile calculation
            var subset = candles;
            if (Period > 0 && Period < candles.Count)
            {
                subset = candles.Skip(candles.Count - Period).ToList();
            }

            // Use shared analysis logic
            ProfileData = VolumeAnalysis.CalculateProfile(subset, RowCount, Mode);

            if (ProfileData.Count == 0)
            {
                 // Fallback
                 for(int i = 0; i < candles.Count; i++) _values.Add(null);
                 return IndicatorResult.Success(_values);
            }

            // Calculate POC (Point of Control) for time series display
            // POC is the price level with max volume
            var maxVol = ProfileData.Max(b => b.TotalVolume);
            var pocBin = ProfileData.FirstOrDefault(b => b.TotalVolume == maxVol);
            decimal poc = pocBin?.Price ?? 0;
            
            // Populate _values with POC (Horizontal Line)

            for (int i = 0; i < candles.Count; i++)
            {
                _values.Add(poc);
            }

            // Return Result with CustomData (ProfileData)
            var resultSeries = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { IndicatorResult.MainSeriesName, _values }
            };

            return IndicatorResult.Success(resultSeries, customData: ProfileData);
        }
    }
}
