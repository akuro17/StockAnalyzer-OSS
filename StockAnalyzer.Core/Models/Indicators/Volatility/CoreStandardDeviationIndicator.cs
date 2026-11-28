using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.StandardDeviation)]
    public class CoreStandardDeviationIndicator : CoreIndicatorBase
    {
        public override string Name => $"StdDev({Period})";
        private int _period = 20;
        public int Period
        {
            get => _period;
            set => _period = Math.Max(1, value);
        }

        public bool IsPopulation { get; set; } = false;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreStandardDeviationParameter sdp)
            {
                Period = sdp.Period;
                IsPopulation = sdp.IsPopulation;
            }
            else if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            if (Period <= 0 || series.Count < Period)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(new List<decimal?>(_values));
            }

            for (int i = 0; i < series.Count; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                decimal sum = 0m;
                bool hasNull = false;
                int start = i - Period + 1;
                for (int j = 0; j < Period; j++)
                {
                    decimal? val = series[start + j];
                    if (!val.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    sum += val.Value;
                }

                if (hasNull)
                {
                    _values.Add(null);
                    continue;
                }

                decimal mean = sum / Period;
                decimal sumSqDiff = 0m;
                for (int j = 0; j < Period; j++)
                {
                    decimal diff = series[start + j]!.Value - mean;
                    sumSqDiff += diff * diff;
                }

                decimal variance = Period <= 1
                    ? 0m
                    : (IsPopulation ? sumSqDiff / Period : sumSqDiff / (Period - 1));
                _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(System.Math.Sqrt((double)variance)));
            }

            return IndicatorResult.Success(new List<decimal?>(_values));
        }
    }
}
