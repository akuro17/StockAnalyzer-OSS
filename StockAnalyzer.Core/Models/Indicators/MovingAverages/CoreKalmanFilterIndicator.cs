using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.KalmanFilter)]
    public class CoreKalmanFilterIndicator : CoreIndicatorBase
    {
        public override string Name => "Kalman Filter";

        public decimal Q { get; set; } = 0.01m; // Process noise covariance
        public decimal R { get; set; } = 0.1m;  // Measurement noise covariance

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreKalmanFilterParameter p)
            {
                Q = p.Q;
                R = p.R;
            }
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            _values.Clear();

            if (series == null || series.Count == 0)
            {
                return IndicatorResult.Success(_values);
            }

            decimal x = 0m;
            decimal p = 1m;
            bool initialized = false;

            for (int i = 0; i < series.Count; i++)
            {
                if (!series[i].HasValue)
                {
                    _values.Add(null);
                    continue;
                }

                decimal price = series[i]!.Value;
                if (!initialized)
                {
                    x = price;
                    p = 1m;
                    initialized = true;
                    _values.Add(x);
                    continue;
                }

                // Prediction update
                p = p + Q;

                // Measurement update
                decimal k = p / (p + R); // Kalman gain
                x = x + k * (price - x);
                p = (1m - k) * p;

                _values.Add(x);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
