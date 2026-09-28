using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    [StockAnalyzerIndicator(IndicatorType.TimeAtPrice)]
    public class CoreTimeAtPriceIndicator : CoreIndicatorBase
    {
        public override string Name => "Time at Price";
        public override bool IsOverlay => true;

        public override CoreIndicatorSettings GetDefaultSettings()
        {
            return new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.TimeAtPrice,
                IsEnabled = false,
                Category = CoreIndicatorCategory.Chart,
                Color = IndicatorDefaultConstants.VolumeProfileColor,
                Thickness = IndicatorDefaultConstants.DefaultBandThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                ParameterObject = new CoreTimeAtPriceParameter
                {
                    Period = IndicatorDefaultConstants.VolumeProfilePeriod,
                    RowCount = IndicatorDefaultConstants.VolumeProfileRowCount,
                    PriceType = PriceType.Close,
                    Opacity = IndicatorDefaultConstants.VolumeProfileOpacity,
                    Side = DisplaySide.Left
                }
            };
        }

        public int RowCount { get; set; } = 50;
        public int Period { get; set; } = 200;
        public PriceType PriceType
        {
            get => PriceSource;
            set => PriceSource = value;
        }

        // Exposed for Renderer
        public List<VolumeBin> ProfileData { get; private set; } = new();

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreTimeAtPriceParameter p)
            {
                RowCount = p.RowCount;
                Period = p.Period;
                PriceSource = p.PriceSource;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            var subset = candles;
            if (Period > 0 && Period < candles.Count)
            {
                subset = candles.Skip(candles.Count - Period).ToList();
            }

            ProfileData = TimeAtPriceAnalysis.CalculateProfile(subset, RowCount, PriceSource);

            if (ProfileData.Count == 0)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var maxTime = ProfileData.Max(b => b.TotalVolume);
            var pocBin = ProfileData.FirstOrDefault(b => b.TotalVolume == maxTime);
            decimal poc = pocBin?.Price ?? 0;

            for (int i = 0; i < candles.Count; i++)
            {
                _values.Add(poc);
            }

            var resultSeries = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { IndicatorResult.MainSeriesName, _values }
            };

            return IndicatorResult.Success(resultSeries, customData: ProfileData);
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            _values.Clear();
            if (series == null || series.Count == 0) return IndicatorResult.Empty();

            var subset = series;
            if (Period > 0 && Period < series.Count)
            {
                subset = series.Skip(series.Count - Period).ToList();
            }

            ProfileData = TimeAtPriceAnalysis.CalculateProfile(subset, RowCount);

            if (ProfileData.Count == 0)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var maxTime = ProfileData.Max(b => b.TotalVolume);
            var pocBin = ProfileData.FirstOrDefault(b => b.TotalVolume == maxTime);
            decimal poc = pocBin?.Price ?? 0;

            for (int i = 0; i < series.Count; i++)
            {
                _values.Add(poc);
            }

            var resultSeries = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { IndicatorResult.MainSeriesName, _values }
            };

            return IndicatorResult.Success(resultSeries, customData: ProfileData);
        }
    }
}
