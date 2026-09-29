using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

/// <summary>
/// IFFT Instantaneous Amplitude (Analytic Signal) indicator.
/// For each bar it reconstructs the analytic signal <c>z[n]</c> of the trailing window via
/// <see cref="FftAnalyticSignal"/> (forward FFT, negative frequencies zeroed / positive
/// frequencies doubled, then inverse FFT) and plots the instantaneous amplitude
/// <c>|z[n]|</c> of the most recent bar as a single overlay line.
/// Because <c>Re(z[n])</c> equals the median price, <c>|z[n]| ≥ price</c>, so the line traces
/// just above price (an upper amplitude line). Pure C# (no Python dependency); causal.
/// The phase / sine-wave series live in the companion sub-panel indicator
/// (<c>IFFTInstantaneousPhase</c>).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.IFFTInstantaneousAmplitude)]
public class CoreIfftInstantaneousAmplitudeIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.IfftInstantaneousAmplitudeDefaultWindowSize;

    public override string Name => $"IFFT Instantaneous Amplitude ({WindowSize})";
    public override bool IsOverlay => true;

    // Median (High+Low)/2 price, matching the companion IFFTInstantaneousPhase indicator.
    public override PriceType PriceSource { get; set; } = PriceType.Median;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreIfftInstantaneousAmplitudeParameter p)
        {
            WindowSize = p.WindowSize;
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

        var phase = new double[n];
        var envelope = new double[n];
        FftAnalyticSignal.RollingCausalAnalyticSignal(samples, WindowSize, phase, envelope);

        for (int i = 0; i < n; i++)
        {
            _values.Add(double.IsNaN(envelope[i]) ? (decimal?)null : (decimal)envelope[i]);
        }

        return IndicatorResult.Success(_values);
    }
}
