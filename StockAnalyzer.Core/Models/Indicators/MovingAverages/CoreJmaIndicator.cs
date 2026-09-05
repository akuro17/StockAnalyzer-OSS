using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.JMA)]
public class CoreJmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 7;
    public double Phase { get; set; } = 0;

    public override string Name => $"JMA ({Period}, {Phase})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreJmaParameter p)
        {
            Period = p.Period;
            Phase = p.Phase;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);
        var prices = PriceDataHelper.ExtractPriceSeries(candles, PriceSource).Select(p => p ?? 0m).ToList();
        if (prices.Count < Period || Period <= 1)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, prices.Count));
            return IndicatorResult.Success(_values);
        }

        var results = new decimal?[prices.Count];

        // Constants
        double phase_ratio = Phase < -100 ? 0.5 : (Phase > 100 ? 2.5 : Phase / 100.0 + 1.5);
        double beta = 0.45 * (Period - 1) / (0.45 * (Period - 1) + 2);
        double log_val = Math.Log(Math.Sqrt(Period - 1));
        double kv = log_val / (Math.Log(2.0) * 2.0);
        double len = Math.Floor(kv + 2);
        double power = Math.Max(len, 2.0);
        double alpha = Math.Pow(beta, power);

        // State variables
        double e0 = 0, e1 = 0, e2 = 0, jma = 0;

        // Initialization with the first price
        e0 = (double)prices[0];
        jma = (double)prices[0];

        // Start loop from the second price
        for (int i = 1; i < prices.Count; i++)
        {
            double price = (double)prices[i];

            double prev_e0 = e0;
            e0 = (1 - alpha) * price + alpha * prev_e0;

            double prev_e1 = e1;
            e1 = (price - e0) * (1 - beta) + beta * prev_e1;

            double prev_e2 = e2;
            e2 = (e0 + phase_ratio * e1 - jma) * Math.Pow(1 - alpha, 2) + Math.Pow(alpha, 2) * prev_e2;

            jma = jma + e2;

            if (i >= Period)
            {
                results[i] = (decimal)jma;
            }
        }
        _values.AddRange(results);

        return IndicatorResult.Success(_values);
    }
}
