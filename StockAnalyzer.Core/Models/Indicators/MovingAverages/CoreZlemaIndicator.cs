using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.ZLEMA)]
    public class CoreZlemaIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"ZLEMA ({Period})";

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
            if (series == null || series.Count == 0) return IndicatorResult.Success(_values);
            if (Period <= 0)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            int lag = (Period - 1) / 2;
            var emaData = new List<decimal?>(series.Count);
            for (int i = 0; i < series.Count; i++)
            {
                if (i < lag)
                {
                    emaData.Add(series[i]);
                }
                else
                {
                    decimal? curr = series[i];
                    decimal? lagged = series[i - lag];
                    emaData.Add(curr.HasValue && lagged.HasValue ? 2m * curr.Value - lagged.Value : null);
                }
            }

            _values.AddRange(IndicatorCalculationHelper.CalculateEmaWithNulls(emaData, Period));
            return IndicatorResult.Success(_values);
        }
    }
}
