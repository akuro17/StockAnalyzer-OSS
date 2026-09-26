using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.FFTCycle)]
public class CoreFFTCycleIndicator : CoreIndicatorBase
{
    public int WindowSize { get; set; } = IndicatorDefaultConstants.FftCycleDefaultWindowSize;
    public override string Name => $"FFT Cycle ({WindowSize})";
    public override bool IsOverlay => false;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreFFTCycleParameter p)
        {
            WindowSize = p.WindowSize;
        }
    }

    public List<decimal?> CycleStrength { get; } = new();
    public List<decimal?> Oscillator { get; } = new();

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        var samples = PriceDataHelper.ExtractDoubleSeries(candles, PriceType.Median);

        var cycle = new double[samples.Length];
        var strength = new double[samples.Length];
        var oscillator = new double[samples.Length];
        FftCycleMath.CalculateRollingFftCycle(samples, WindowSize, cycle, strength, oscillator);

        _values.Clear();
        CycleStrength.Clear();
        Oscillator.Clear();
        NullableDecimalConversion.AppendTo(cycle, _values);
        NullableDecimalConversion.AppendTo(strength, CycleStrength);
        NullableDecimalConversion.AppendTo(oscillator, Oscillator);
        return CreateAutomaticResult();
    }
}
