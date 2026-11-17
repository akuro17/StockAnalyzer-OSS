using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// WebAI UI-exposure Batch 2: Hilbert DetrendMode, the effective dynamic period metadata and the extracted Kalman projection engine.
/// </summary>
public class UiExposureBatch2Tests
{
    private const int SeriesLength = 160;

    private static decimal[] CreateWavePrices(int n = SeriesLength)
    {
        var prices = new decimal[n];
        for (int i = 0; i < n; i++)
        {
            prices[i] = 100m + 0.05m * i + (decimal)(3.0 * Math.Sin(2.0 * Math.PI * i / 17.0));
        }

        return prices;
    }

    private static List<CoreCandleData> CreateCandles(int n)
    {
        var list = new List<CoreCandleData>(n);
        var start = new DateTime(2024, 1, 1);
        foreach (var close in CreateWavePrices(n))
        {
            list.Add(new CoreCandleData(start.AddDays(list.Count), close - 0.5m, close + 1m, close - 1m, close, 1000));
        }

        return list;
    }

    // ---------------------------------------------------------------- Hilbert DetrendMode

    [Fact]
    public void HilbertDetrendMode_DefaultsToFirDetrender_OnTheParametersAndEveryParameterClass()
    {
        Assert.Equal(HilbertDetrendMode.FirDetrender, new HilbertDecompositionParameters().DetrendMode);
        Assert.Equal(HilbertDetrendMode.FirDetrender, new CoreHilbertSineParameter().DetrendMode);
        Assert.Equal(HilbertDetrendMode.FirDetrender, new CoreHilbertTransformParameter().DetrendMode);
        Assert.Equal(HilbertDetrendMode.FirDetrender, new CoreHilbertTrendModeParameter().DetrendMode);
        Assert.Equal(HilbertDetrendMode.FirDetrender, new CoreHilbertTrendlineParameter().DetrendMode);
    }

    [Fact]
    public void HilbertDecompose_ExplicitFirDetrender_IsIdenticalToTheDefaultParameters()
    {
        var prices = CreateWavePrices();

        var byDefault = HilbertDecompositionEngine.Decompose(prices);
        var explicitFir = HilbertDecompositionEngine.Decompose(prices, new HilbertDecompositionParameters(DetrendMode: HilbertDetrendMode.FirDetrender));

        for (int i = 0; i < prices.Length; i++)
        {
            Assert.Equal(byDefault[i], explicitFir[i]);
        }
    }

    [Theory]
    [InlineData(HilbertDetrendMode.Wma4)]
    [InlineData(HilbertDetrendMode.HighPass)]
    public void HilbertDecompose_AlternativeDetrenders_ChangeTheResult_AndStayCausal(HilbertDetrendMode mode)
    {
        var prices = CreateWavePrices();
        var parameters = new HilbertDecompositionParameters(DetrendMode: mode);

        var fir = HilbertDecompositionEngine.Decompose(prices);
        var full = HilbertDecompositionEngine.Decompose(prices, parameters);

        Assert.Contains(Enumerable.Range(0, prices.Length), i => full[i].InPhase != fir[i].InPhase || full[i].Quadrature != fir[i].Quadrature);

        // Causality: a shorter prefix reproduces exactly the same samples.
        int prefix = prices.Length - 40;
        var shortRun = HilbertDecompositionEngine.Decompose(prices.AsSpan(0, prefix), parameters);
        for (int i = 0; i < prefix; i++)
        {
            Assert.Equal(full[i], shortRun[i]);
        }
    }

    [Theory]
    [InlineData(HilbertDetrendMode.Wma4)]
    [InlineData(HilbertDetrendMode.HighPass)]
    public void HilbertDecompose_AlternativeDetrenders_ProduceValidBoundedCyclePeriods(HilbertDetrendMode mode)
    {
        var parameters = new HilbertDecompositionParameters(DetrendMode: mode);
        var result = HilbertDecompositionEngine.Decompose(CreateWavePrices(), parameters);

        for (int i = 0; i < SeriesLength; i++)
        {
            Assert.InRange(result[i].DominantCycle, (decimal)parameters.MinPeriod, (decimal)parameters.MaxPeriod);
            Assert.True(double.IsFinite(result[i].PhaseRad));
        }

        Assert.Contains(Enumerable.Range(0, SeriesLength), i => result[i].IsValid);
    }

    [Fact]
    public void HilbertDecompose_HighPass_CutoffFollowsDefaultPeriod()
    {
        var prices = CreateWavePrices();
        var a = HilbertDecompositionEngine.Decompose(prices, new HilbertDecompositionParameters(DefaultPeriod: 10, DetrendMode: HilbertDetrendMode.HighPass));
        var b = HilbertDecompositionEngine.Decompose(prices, new HilbertDecompositionParameters(DefaultPeriod: 30, DetrendMode: HilbertDetrendMode.HighPass));

        Assert.Contains(Enumerable.Range(0, SeriesLength), i => a[i].InPhase != b[i].InPhase);
    }

    [Fact]
    public void HilbertIndicators_ForwardTheDetrendModeFromTheirParameters()
    {
        var series = CreateWavePrices().Select(p => (decimal?)p).ToList();

        var firSine = new CoreHilbertSineIndicator();
        var hpSine = new CoreHilbertSineIndicator();
        hpSine.Configure(new CoreHilbertSineParameter { DetrendMode = HilbertDetrendMode.HighPass });
        Assert.Equal(HilbertDetrendMode.HighPass, hpSine.DetrendMode);

        var firValues = firSine.CalculateSeries(series).MainValues;
        var hpValues = hpSine.CalculateSeries(series).MainValues;
        Assert.Contains(Enumerable.Range(0, series.Count), i => firValues[i] != hpValues[i]);

        var trend = new CoreHilbertTrendlineIndicator();
        trend.Configure(new CoreHilbertTrendlineParameter { DetrendMode = HilbertDetrendMode.Wma4 });
        Assert.Equal(HilbertDetrendMode.Wma4, trend.DetrendMode);

        var mode = new CoreHilbertTrendModeIndicator();
        mode.Configure(new CoreHilbertTrendModeParameter { DetrendMode = HilbertDetrendMode.HighPass });
        Assert.Equal(HilbertDetrendMode.HighPass, mode.DetrendMode);

        var transform = new CoreHilbertTransformIndicator();
        transform.Configure(new CoreHilbertTransformParameter { DetrendMode = HilbertDetrendMode.Wma4 });
        Assert.Equal(HilbertDetrendMode.Wma4, transform.DetrendMode);
    }

    // ---------------------------------------------------------------- Effective dynamic period metadata

    [Theory]
    [InlineData("12.4", 12)]
    [InlineData("12.5", 13)]
    [InlineData("0.2", 1)]
    [InlineData("-3", 1)]
    public void EffectiveDynamicPeriodDisplay_RoundsHalfAwayFromZero_AndFloorsAtOneBar(string raw, int expected)
    {
        var periods = new List<decimal?> { decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) };

        Assert.Equal(expected, EffectiveDynamicPeriodDisplay.GetEffectivePeriod(periods, 0));
    }

    [Fact]
    public void EffectiveDynamicPeriodDisplay_MissingValueOrIndex_HasNoPeriod()
    {
        var periods = new List<decimal?> { null, 8m };

        Assert.Null(EffectiveDynamicPeriodDisplay.GetEffectivePeriod(periods, 0));
        Assert.Null(EffectiveDynamicPeriodDisplay.GetEffectivePeriod(periods, -1));
        Assert.Null(EffectiveDynamicPeriodDisplay.GetEffectivePeriod(periods, 2));
        Assert.Equal(8, EffectiveDynamicPeriodDisplay.GetEffectivePeriod(periods, 1));
    }

    [Fact]
    public void EffectiveDynamicPeriodDisplay_Resolve_RequiresADriverIdAndPeriodsOnTheResult()
    {
        var plain = IndicatorResult.Success(new List<decimal?> { 1m });
        var withPeriods = IndicatorResult.Success(new List<decimal?> { 1m });
        withPeriods.SetEffectiveDynamicPeriods(new List<decimal?> { 7m });

        Assert.Null(EffectiveDynamicPeriodDisplay.Resolve("driver", plain));
        Assert.Null(EffectiveDynamicPeriodDisplay.Resolve(null, withPeriods));
        Assert.Null(EffectiveDynamicPeriodDisplay.Resolve("", withPeriods));
        Assert.Null(EffectiveDynamicPeriodDisplay.Resolve("driver", null));
        Assert.Same(withPeriods.EffectiveDynamicPeriods, EffectiveDynamicPeriodDisplay.Resolve("driver", withPeriods));
    }

    private static List<CoreIndicatorSettings> DrivenEmaSettings() => new()
    {
        new CoreIndicatorSettings
        {
            Id = "AdaptiveEMA",
            IsEnabled = true,
            TypeEnum = IndicatorType.EMA,
            ParameterObject = new CoreSmaParameter { Period = 10 },
            DynamicPeriodIndicatorId = "HT1"
        },
        new CoreIndicatorSettings
        {
            Id = "HT1",
            IsEnabled = true,
            TypeEnum = IndicatorType.HilbertTransform,
            ParameterObject = new CoreHilbertTransformParameter { MinPeriod = 2, MaxPeriod = 50 }
        }
    };

    [Fact]
    public void Pipeline_AttachesEffectiveDynamicPeriods_OnlyToTheDrivenIndicator()
    {
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default);
        var candles = CreateCandles(60);

        var result = service.CalculateIndicators(candles, DrivenEmaSettings());

        var periods = result["AdaptiveEMA"].EffectiveDynamicPeriods;
        Assert.NotNull(periods);
        Assert.Equal(candles.Count, periods!.Count);
        Assert.Null(result["HT1"].EffectiveDynamicPeriods);
    }

    [Fact]
    public async Task PipelineAsync_AttachesEffectiveDynamicPeriods_OnlyToTheDrivenIndicator()
    {
        var service = new AnalysisPipelineService(pythonService: null, indicatorFactory: IndicatorFactory.Default);
        var candles = CreateCandles(60);

        var result = await service.CalculateIndicatorsAsync(candles, DrivenEmaSettings());

        Assert.NotNull(result["AdaptiveEMA"].EffectiveDynamicPeriods);
        Assert.Null(result["HT1"].EffectiveDynamicPeriods);
    }

    // ---------------------------------------------------------------- KalmanProjectionAnalysis

    private static decimal[] LinearCloses(int n, decimal start, decimal step)
    {
        var closes = new decimal[n];
        for (int i = 0; i < n; i++) closes[i] = start + i * step;
        return closes;
    }

    [Fact]
    public void Kalman_EmptyInput_ReturnsTheEmptyResult()
    {
        var result = KalmanProjectionAnalysis.CalculateProjection(ReadOnlySpan<decimal>.Empty, 10, 0.01m, 0.1m, 2m);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void Kalman_LinearRamp_ConvergesToTheSlope_AndExtrapolatesIt()
    {
        var closes = LinearCloses(40, 100m, 2m);

        var result = KalmanProjectionAnalysis.CalculateProjection(closes, 5, 0.01m, 0.1m, 2m);

        Assert.Equal(40, result.SampleCount);
        Assert.InRange(result.TerminalVelocity, 1.99m, 2.01m);
        Assert.Equal(5, result.Steps.Count);
        for (int k = 1; k <= 5; k++)
        {
            Assert.Equal(k, result.Steps[k - 1].StepOffset);
            Assert.Equal(result.TerminalPosition + k * result.TerminalVelocity, result.Steps[k - 1].ProjectedPrice);
        }

        Assert.InRange(result.TerminalGainPosition, 0m, 1m);
        Assert.True(result.PositionVariance > 0m);
        Assert.True(result.VelocityVariance > 0m);
        Assert.True(result.InnovationScale > 0m);
    }

    [Fact]
    public void Kalman_Bands_AreSymmetricAndWidenWithTheHorizon()
    {
        var closes = CreateWavePrices(60);

        var result = KalmanProjectionAnalysis.CalculateProjection(closes, 8, 0.01m, 0.1m, 2m);

        decimal previousHalfWidth = -1m;
        foreach (var step in result.Steps)
        {
            Assert.Equal(step.UpperBand - step.ProjectedPrice, step.ProjectedPrice - step.LowerBand);
            decimal halfWidth = step.UpperBand - step.ProjectedPrice;
            Assert.True(halfWidth > previousHalfWidth);
            previousHalfWidth = halfWidth;
        }
    }

    [Fact]
    public void Kalman_ZeroMultiplier_CollapsesTheBandsOntoTheProjection_AndNegativeIsTreatedAsZero()
    {
        var closes = CreateWavePrices(30);

        foreach (decimal multiplier in new[] { 0m, -1m })
        {
            var result = KalmanProjectionAnalysis.CalculateProjection(closes, 4, 0.01m, 0.1m, multiplier);
            Assert.All(result.Steps, s =>
            {
                Assert.Equal(s.ProjectedPrice, s.UpperBand);
                Assert.Equal(s.ProjectedPrice, s.LowerBand);
            });
        }
    }

    [Fact]
    public void Kalman_Horizon_IsClampedToTheDocumentedRange()
    {
        var closes = CreateWavePrices(20);

        Assert.Single(KalmanProjectionAnalysis.CalculateProjection(closes, 0, 0.01m, 0.1m, 2m).Steps);
        Assert.Equal(KalmanProjectionAnalysis.MaxForecastHorizon,
            KalmanProjectionAnalysis.CalculateProjection(closes, 10_000, 0.01m, 0.1m, 2m).Steps.Count);
    }

    [Fact]
    public void Kalman_NoiseBelowTheFloor_BehavesLikeTheFloor()
    {
        var closes = CreateWavePrices(25);

        var belowFloor = KalmanProjectionAnalysis.CalculateProjection(closes, 3, 0m, -5m, 2m);
        var atFloor = KalmanProjectionAnalysis.CalculateProjection(closes, 3, KalmanProjectionAnalysis.MinNoise, KalmanProjectionAnalysis.MinNoise, 2m);

        Assert.Equal(atFloor.TerminalPosition, belowFloor.TerminalPosition);
        Assert.Equal(atFloor.TerminalVelocity, belowFloor.TerminalVelocity);
        Assert.Equal(atFloor.Steps, belowFloor.Steps);
    }

    [Fact]
    public void Kalman_SingleBar_ProjectsFlatWithoutAnUpdate()
    {
        var result = KalmanProjectionAnalysis.CalculateProjection(new[] { 123.45m }, 3, 0.01m, 0.1m, 2m);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(0m, result.TerminalVelocity);
        Assert.Equal(0m, result.TerminalGainPosition);
        Assert.Equal(0m, result.TerminalGainVelocity);
        Assert.All(result.Steps, s => Assert.Equal(123.45m, s.ProjectedPrice));
    }
}
