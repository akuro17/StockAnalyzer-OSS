using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.MtfLaggedEma)]
    public class CoreMtfLaggedEmaIndicator : CoreIndicatorBase
    {
        public override string Name => "Multi-Timeframe Lagged EMA";
        public override bool IsOverlay => true;

        public int Period { get; set; } = 20;
        private int _timeFrameMultiplier = 4;
        public int TimeFrameMultiplier 
        { 
            get => _timeFrameMultiplier; 
            set => _timeFrameMultiplier = Math.Max(1, value); 
        }
        private int _lag = 1;
        public int Lag 
        { 
            get => _lag; 
            set => _lag = Math.Max(1, value); 
        }

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMtfLaggedEmaParameter p)
            {
                Period = p.Period;
                TimeFrameMultiplier = Math.Max(1, p.TimeFrameMultiplier);
                Lag = Math.Max(1, p.Lag);
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < Period * TimeFrameMultiplier)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            int higherTfCount = candles.Count / TimeFrameMultiplier;
            var higherTfCloses = new decimal[higherTfCount];
            int index = 0;
            for (int i = 0; i <= candles.Count - TimeFrameMultiplier; i += TimeFrameMultiplier)
            {
                higherTfCloses[index++] = priceSeries[i + TimeFrameMultiplier - 1] ?? 0m;
            }

            var higherTfEma = IndicatorCalculationHelper.CalculateEma(higherTfCloses, Period);

            for (int i = 0; i < candles.Count; i++)
            {
                int higherTfIndex = i / TimeFrameMultiplier;
                int laggedIndex = higherTfIndex - Lag;

                if (laggedIndex >= 0 && laggedIndex < higherTfEma.Count && higherTfEma[laggedIndex].HasValue)
                {
                    _values.Add(higherTfEma[laggedIndex]);
                }
                else
                {
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}

