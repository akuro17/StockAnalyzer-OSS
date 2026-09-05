using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.FFTTrendFilter)]
public class CoreFFTTrendFilterIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.FftTrendFilterDefaultWindowSize;
    public int NumHarmonics { get; set; } = IndicatorDefaultConstants.FftTrendFilterDefaultNumHarmonics;
    public override string Name => $"FFT Trend Filter ({WindowSize}, {NumHarmonics})";
    public override bool IsOverlay => true;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreFFTTrendFilterParameter p)
        {
            WindowSize = p.WindowSize;
            NumHarmonics = p.NumHarmonics;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        // Median (High+Low)/2 price, matching the previous Python implementation.
        var priceSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceType.Median);
        int n = priceSeries.Count;

        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = (double)priceSeries[i];
        }

        var trend = new double[n];
        FftLowPassFilter.RollingCausalTrend(samples, WindowSize, NumHarmonics, trend);

        for (int i = 0; i < n; i++)
        {
            _values.Add(double.IsNaN(trend[i]) ? (decimal?)null : (decimal)trend[i]);
        }

        return IndicatorResult.Success(_values);
    }
}
