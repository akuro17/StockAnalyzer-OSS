using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.CMF)]
    public class CoreCmfIndicator : CoreIndicatorBase
    {
        public override string Name => $"CMF({Period})";
        public int Period { get; set; } = 20;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (candles.Count < Period)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var mfVolumes = new List<decimal>();
            var volumes = new List<long>();

            for (int i = 0; i < candles.Count; i++)
            {
                var c = candles[i];
                decimal range = c.High - c.Low;
                decimal mfMultiplier = range == 0 ? 0 : ((c.Close - c.Low) - (c.High - c.Close)) / range;
                decimal mfVolume = mfMultiplier * c.Volume;

                mfVolumes.Add(mfVolume);
                volumes.Add(c.Volume);

                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                decimal sumMfVolume = 0;
                long sumVolume = 0;
                for(int j = i - Period + 1; j <= i; j++)
                {
                    sumMfVolume += mfVolumes[j];
                    sumVolume += volumes[j];
                }

                _values.Add(sumVolume == 0 ? 0 : sumMfVolume / sumVolume);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
