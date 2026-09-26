using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.FourierTransform)]
public class CoreFourierTransformIndicator : CoreIndicatorBase
{
    public int TargetPeriod { get; set; } = IndicatorDefaultConstants.FourierTransformDefaultTargetPeriod;
    public override string Name => $"Fourier Transform ({TargetPeriod})";
    public override bool IsOverlay => false;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreFourierTransformParameter p)
        {
            TargetPeriod = p.TargetPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        var samples = PriceDataHelper.ExtractDoubleSeries(candles, PriceType.Median);

        var amplitude = new double[samples.Length];
        GoertzelMath.CalculateRollingAmplitude(samples, TargetPeriod, amplitude);

        _values.Clear();
        NullableDecimalConversion.AppendTo(amplitude, _values);
        return IndicatorResult.Success(_values);
    }
}
