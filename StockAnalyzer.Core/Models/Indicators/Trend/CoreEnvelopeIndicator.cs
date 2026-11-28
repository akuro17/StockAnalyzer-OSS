using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.Envelope)]
    public class CoreEnvelopeIndicator : CoreIndicatorBase
    {
        public override string Name => "Envelope";

        public int Period { get; set; } = 20;
        public decimal Deviation { get; set; } = 0.025m; // 2.5%

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreEnvelopeParameter p)
            {
                Period = p.Period;
                Deviation = p.Deviation;
            }
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            _values.Clear();

            int count = series?.Count ?? 0;
            var upper = new List<decimal?>(count);
            var main = new List<decimal?>(count);
            var lower = new List<decimal?>(count);

            if (count == 0)
            {
                return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { "Upper", upper }, { "Main", main }, { "Lower", lower } });
            }

            if (Period <= 0 || count < Period)
            {
                for (int i = 0; i < count; i++)
                {
                    upper.Add(null);
                    main.Add(null);
                    lower.Add(null);
                    _values.Add(null);
                }
                return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { "Upper", upper }, { "Main", main }, { "Lower", lower } });
            }

            var smaValues = IndicatorCalculationHelper.CalculateSmaWithNulls(series!, Period);
            for (int i = 0; i < count; i++)
            {
                decimal? sma = smaValues[i];
                if (sma.HasValue)
                {
                    main.Add(sma.Value);
                    upper.Add(sma.Value * (1m + Deviation));
                    lower.Add(sma.Value * (1m - Deviation));
                    _values.Add(sma.Value);
                }
                else
                {
                    main.Add(null);
                    upper.Add(null);
                    lower.Add(null);
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { "Upper", upper }, { "Main", main }, { "Lower", lower } });
        }
    }
}
