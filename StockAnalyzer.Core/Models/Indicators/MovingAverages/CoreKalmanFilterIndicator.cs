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

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (candles.Count == 0)
            {
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            decimal x = priceSeries[0] ?? 0m; // Initial state estimate
            decimal p = 1;                   // Initial error covariance

            _values.Add(x);

            for (int i = 1; i < candles.Count; i++)
            {
                // Prediction update
                p = p + Q;

                // Measurement update
                decimal k = p / (p + R); // Kalman gain
                x = x + k * ((priceSeries[i] ?? 0m) - x);
                p = (1 - k) * p;

                _values.Add(x);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
