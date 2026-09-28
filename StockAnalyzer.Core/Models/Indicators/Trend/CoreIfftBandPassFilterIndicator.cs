using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// IFFT Band-Pass Filter indicator.
/// For each bar it transforms the trailing window via <see cref="FftBandPassFilter"/>, which
/// auto-detects the dominant (highest-magnitude) non-DC frequency bin and reconstructs only
/// that bin plus <see cref="BandWidthBins"/> neighboring bins on each side, and plots the
/// resulting causal, self-tuning band-pass reconstruction as a single overlay line. Pure C#
/// (no Python dependency); causal (never repaints). Distinct from the Python-backed
/// <c>FFTCycle</c> ("Dominant Cycle") indicator, which reports a scalar period/strength/phase
/// via batch Hanning-window peak detection rather than a reconstructed price-scale waveform.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.IFFTBandPassFilter)]
public class CoreIfftBandPassFilterIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.IfftBandPassFilterDefaultWindowSize;
    public int BandWidthBins { get; set; } = IndicatorDefaultConstants.IfftBandPassFilterDefaultBandWidthBins;

    public override string Name => $"IFFT Band-Pass Filter ({WindowSize}, {BandWidthBins})";
    public override bool IsOverlay => false;

    // Median (High+Low)/2 price, matching the sibling IFFT family.
    public override PriceType PriceSource { get; set; } = PriceType.Median;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreIfftBandPassFilterParameter p)
        {
            WindowSize = p.WindowSize;
            BandWidthBins = p.BandWidthBins;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var priceSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceSource);
        int n = priceSeries.Count;

        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = (double)priceSeries[i];
        }

        var trend = new double[n];
        FftBandPassFilter.RollingCausalTrend(samples, WindowSize, BandWidthBins, trend);

        for (int i = 0; i < n; i++)
        {
            _values.Add(double.IsNaN(trend[i]) ? (decimal?)null : (decimal)trend[i]);
        }

        return IndicatorResult.Success(_values);
    }
}
