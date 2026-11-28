using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.UlcerIndex)]
    public class CoreUlcerIndexIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"Ulcer ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            if (Period <= 0)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            for (int i = 0; i < series.Count; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                decimal maxClose = 0m;
                bool hasNull = false;
                for (int j = 0; j < Period; j++)
                {
                    decimal? val = series[i - j];
                    if (!val.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    maxClose = Math.Max(maxClose, val.Value);
                }

                if (hasNull)
                {
                    _values.Add(null);
                    continue;
                }

                if (maxClose == 0m)
                {
                    _values.Add(0m); // Avoid division by zero when non-null prices are <= 0
                    continue;
                }

                decimal sumSq = 0m;
                for (int j = 0; j < Period; j++)
                {
                    decimal dd = (series[i - j]!.Value - maxClose) / maxClose * 100m;
                    sumSq += dd * dd;
                }

                _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt((double)(sumSq / Period))));
            }

            return IndicatorResult.Success(_values);
        }
    }
}
