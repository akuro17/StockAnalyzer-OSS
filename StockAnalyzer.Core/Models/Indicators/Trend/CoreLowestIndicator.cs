using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.Lowest)]
    public class CoreLowestIndicator : CoreIndicatorBase
    {
        public override string Name => $"Lowest ({Period})";

        public int Period { get; set; } = 20;

        public override PriceType PriceSource { get; set; } = PriceType.Low;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            _values.Clear();
            _values.AddRange(RollingExtremeHelper.CalculateRollingMin(series, Period));

            return IndicatorResult.Success(_values);
        }
    }
}
