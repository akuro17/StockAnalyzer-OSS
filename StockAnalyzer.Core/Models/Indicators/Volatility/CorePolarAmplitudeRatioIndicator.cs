using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// Polar Amplitude Ratio indicator.
/// Builds the analytic signal of the price series with the Ehlers Canonical Homodyne Discriminator
/// (<see cref="HilbertDecompositionEngine"/>, reused unchanged via
/// <see cref="PolarCoordinateDecompositionEngine"/>) and plots the instantaneous amplitude
/// <c>r = sqrt(I^2 + Q^2)</c> normalized by the Average True Range: <c>AmplitudeRatio = r / ATR_L</c>.
/// The ratio is timeframe- and price-band-independent, so it is comparable across tickers.
/// Sub-panel; causal (never repaints).
/// Warm-up / invalid bars are surfaced as <see langword="null"/> here (the engine itself only
/// flags them via <c>IsWarmup</c> / <c>IsValid</c>).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.PolarAmplitudeRatio)]
public class CorePolarAmplitudeRatioIndicator : CoreIndicatorBase
{
    /// <summary>Guard against division by a vanishing ATR when forming the amplitude ratio.</summary>
    public const decimal AmplitudeRatioEpsilon = 1e-10m;

    public int AtrPeriod { get; set; } = IndicatorDefaultConstants.PolarAmplitudeRatioDefaultAtrPeriod;
    public int DefaultPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultPeriod;
    public int MinPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMinPeriod;
    public int MaxPeriod { get; set; } = IndicatorDefaultConstants.HilbertTransformMaxPeriod;
    public decimal SmoothBeta { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta;
    public decimal DeltaLimit { get; set; } = IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit;

    public override string Name => $"Polar Amplitude Ratio ({AtrPeriod})";
    public override bool IsOverlay => false;
    public override PriceType PriceSource { get; set; } = PriceType.Typical;

    internal static decimal? NormalizeAmplitude(decimal amplitude, decimal? atrValue)
    {
        return atrValue is > AmplitudeRatioEpsilon
            ? amplitude / atrValue.Value
            : null;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePolarAmplitudeRatioParameter p)
        {
            AtrPeriod = p.AtrPeriod;
            DefaultPeriod = p.DefaultPeriod;
            MinPeriod = p.MinPeriod;
            MaxPeriod = p.MaxPeriod;
            SmoothBeta = p.SmoothBeta;
            DeltaLimit = p.DeltaLimit;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        var priceSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceSource);
        int n = priceSeries.Count;
        if (n == 0)
        {
            return IndicatorResult.Success(_values);
        }

        // ATR series is sourced from the existing SSoT indicator (no TR / Wilder re-implementation).
        var atrIndicator = new CoreAtrIndicator { Period = AtrPeriod };
        atrIndicator.Calculate(candles);
        IReadOnlyList<decimal?> atrValues = atrIndicator.Values;

        decimal[] prices = ArrayPool<decimal>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                prices[i] = priceSeries[i];
            }

            var decompositionParams = new HilbertDecompositionParameters(
                DefaultPeriod: DefaultPeriod,
                MinPeriod: MinPeriod,
                MaxPeriod: MaxPeriod,
                SmoothBeta: SmoothBeta,
                DeltaLimit: DeltaLimit,
                WarmupBars: IndicatorDefaultConstants.HilbertTransformWarmupBars);

            var decomp = PolarCoordinateDecompositionEngine.Decompose(
                prices.AsSpan(0, n), decompositionParams);

            for (int i = 0; i < n; i++)
            {
                var sample = decomp[i];
                decimal? atrValue = i < atrValues.Count ? atrValues[i] : null;
                decimal? amplitudeRatio = NormalizeAmplitude(sample.Amplitude, atrValue);
                _values.Add(sample.IsWarmup || !sample.IsValid || amplitudeRatio is null
                        ? (decimal?)null
                        : amplitudeRatio.Value);
            }
        }
        finally
        {
            ArrayPool<decimal>.Shared.Return(prices);
        }

        return IndicatorResult.Success(_values);
    }
}
