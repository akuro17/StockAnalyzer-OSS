using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.MESA)]
    public class CoreMesaIndicator : CoreIndicatorBase
    {
        public decimal FastLimit { get; set; } = 0.5m;
        public decimal SlowLimit { get; set; } = 0.05m;
        public override string Name => "MESA";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMesaParameter p)
            {
                FastLimit = p.FastLimit;
                SlowLimit = p.SlowLimit;
            }
        }
        
        // This list will hold the calculated FAMA values. The base class holds values for MAMA.
        public List<decimal?> Fama { get; } = new();

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            try
            {
                new CoreMesaParameter { FastLimit = FastLimit, SlowLimit = SlowLimit }.Validate();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return IndicatorResult.Failure(ex.Message);
            }

            var samples = PriceDataHelper.ExtractDoubleSeries(candles, PriceType.Median);

            var mama = new double[samples.Length];
            var fama = new double[samples.Length];
            MesaMath.CalculateMesa(samples, (double)FastLimit, (double)SlowLimit, mama, fama);

            _values.Clear();
            Fama.Clear();
            NullableDecimalConversion.AppendTo(mama, _values);
            NullableDecimalConversion.AppendTo(fama, Fama);

            var series = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { IndicatorResult.MainSeriesName, _values.ToList() },
                { "Fama", Fama.ToList() }
            };
            return IndicatorResult.Success(series);
        }
    }
}
