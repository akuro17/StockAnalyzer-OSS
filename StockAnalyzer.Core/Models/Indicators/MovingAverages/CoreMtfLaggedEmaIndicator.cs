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
        public int TimeFrameMultiplier { get; set; } = 4;
        public int Lag { get; set; } = 1;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMtfLaggedEmaParameter p)
            {
                Period = p.Period;
                TimeFrameMultiplier = p.TimeFrameMultiplier;
                Lag = p.Lag;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < Period * TimeFrameMultiplier)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            int higherTfCount = candles.Count / TimeFrameMultiplier;
            var higherTfCloses = new decimal[higherTfCount];
            int index = 0;
            for (int i = 0; i <= candles.Count - TimeFrameMultiplier; i += TimeFrameMultiplier)
            {
                higherTfCloses[index++] = candles[i + TimeFrameMultiplier - 1].Close;
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

