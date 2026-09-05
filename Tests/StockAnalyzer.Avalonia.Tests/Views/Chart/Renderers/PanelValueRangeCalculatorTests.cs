using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers
{
    public class PanelValueRangeCalculatorTests
    {
        [Fact]
        public void Calculate_WithIndicatorHavingSignalSeries_ExcludesSignalsFromRange()
        {
            // Given an oscillator like PNO with Main series in [-0.2, +0.2]
            // and Buy/Sell signals having candle stock prices [3000, 3100]
            var setting = new CoreIndicatorSettings
            {
                Id = "pno_test_1",
                TypeEnum = IndicatorType.PrimeNumberOscillator,
                IsOverlay = false
            };

            var seriesDict = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "Main", new List<decimal?> { -0.2m, 0.0m, 0.15m, -0.1m, 0.2m } },
                { "BuySignals", new List<decimal?> { null, 3000m, null, null, null } },
                { "SellSignals", new List<decimal?> { null, null, null, 3100m, null } }
            };
            var result = IndicatorResult.Success(seriesDict);

            var indicatorResults = new Dictionary<string, IIndicatorResult>
            {
                { setting.Id, result }
            };

            var candles = new List<CoreCandleData>();
            for (int i = 0; i < 5; i++)
            {
                candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 3000m, 3100m, 2900m, 3050m, 1000));
            }

            var snapshot = new ChartDataSnapshot(
                candles: candles,
                indicatorResults: indicatorResults,
                indicatorSettings: new[] { setting },
                startIndex: 0,
                count: 5
            );

            var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snapshot, setting);

            // The range should be around [-0.2, +0.2] + 5% buffer, NOT up to 3100
            // Range = 0.2 - (-0.2) = 0.4. Buffer = 0.4 * 0.05 = 0.02
            // Expected min = -0.22, max = +0.22
            Assert.True(minVal < 0m, $"Min value ({minVal}) should be negative.");
            Assert.True(maxVal <= 1.0m, $"Max value ({maxVal}) should not exceed 1.0 (must not include stock price ~3000).");
            Assert.Equal(-0.22m, minVal);
            Assert.Equal(0.22m, maxVal);
        }

        [Fact]
        public void Calculate_ForIfftInstantaneousPhase_ExcludesMainDegreesSeriesFromRange()
        {
            // Given IFFT Instantaneous Phase: Main is 0-360 degrees (not charted),
            // SineWave/LeadSine are the actually-drawn series in [-1, +1].
            var setting = new CoreIndicatorSettings
            {
                Id = "ifft_phase_test_1",
                TypeEnum = IndicatorType.IFFTInstantaneousPhase,
                IsOverlay = false
            };

            var seriesDict = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "Main", new List<decimal?> { 10m, 90m, 180m, 270m, 350m } },
                { "SineWave", new List<decimal?> { -0.9m, 0.1m, 0.8m, -0.3m, 0.5m } },
                { "LeadSine", new List<decimal?> { -0.5m, 0.6m, -0.7m, 0.2m, -0.1m } }
            };
            var result = IndicatorResult.Success(seriesDict);

            var indicatorResults = new Dictionary<string, IIndicatorResult>
            {
                { setting.Id, result }
            };

            var candles = new List<CoreCandleData>();
            for (int i = 0; i < 5; i++)
            {
                candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 3000m, 3100m, 2900m, 3050m, 1000));
            }

            var snapshot = new ChartDataSnapshot(
                candles: candles,
                indicatorResults: indicatorResults,
                indicatorSettings: new[] { setting },
                startIndex: 0,
                count: 5
            );

            var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snapshot, setting);

            // The range should stay within SineWave/LeadSine bounds (+5% buffer), NOT stretch to 350.
            Assert.True(maxVal <= 1.5m, $"Max value ({maxVal}) should not include the 0-360 degree Main series.");
            Assert.True(minVal >= -1.5m, $"Min value ({minVal}) should not include the 0-360 degree Main series.");
        }

        [Fact]
        public void Calculate_ForIfftInstantaneousPhase_ExcludesPhaseDeltaLocalPeriodAndStabilityFromRange()
        {
            // PhaseDelta/LocalPeriod/PhaseStability are degree/bar-scaled diagnostic series (not
            // charted, same as Main); they must not pollute the SineWave/LeadSine (-1..1) range.
            var setting = new CoreIndicatorSettings
            {
                Id = "ifft_phase_test_2",
                TypeEnum = IndicatorType.IFFTInstantaneousPhase,
                IsOverlay = false
            };

            var seriesDict = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "Main", new List<decimal?> { 10m, 90m, 180m, 270m, 350m } },
                { "SineWave", new List<decimal?> { -0.9m, 0.1m, 0.8m, -0.3m, 0.5m } },
                { "LeadSine", new List<decimal?> { -0.5m, 0.6m, -0.7m, 0.2m, -0.1m } },
                { "PhaseDelta", new List<decimal?> { 22.5m, -15m, 30m, null, 12m } },
                { "LocalPeriod", new List<decimal?> { 16m, null, 500m, null, 30m } },
                { "PhaseStability", new List<decimal?> { null, null, 8.2m, 4.1m, 2.0m } }
            };
            var result = IndicatorResult.Success(seriesDict);

            var indicatorResults = new Dictionary<string, IIndicatorResult>
            {
                { setting.Id, result }
            };

            var candles = new List<CoreCandleData>();
            for (int i = 0; i < 5; i++)
            {
                candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 3000m, 3100m, 2900m, 3050m, 1000));
            }

            var snapshot = new ChartDataSnapshot(
                candles: candles,
                indicatorResults: indicatorResults,
                indicatorSettings: new[] { setting },
                startIndex: 0,
                count: 5
            );

            var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snapshot, setting);

            Assert.True(maxVal <= 1.5m, $"Max value ({maxVal}) should not include Main/PhaseDelta/LocalPeriod/PhaseStability.");
            Assert.True(minVal >= -1.5m, $"Min value ({minVal}) should not include Main/PhaseDelta/LocalPeriod/PhaseStability.");
        }

        [Fact]
        public void IsSignalSeries_IdentifiesAllKnownSignalSeries()
        {
            Assert.True(PanelValueRangeCalculator.IsSignalSeries("BullishSignals"));
            Assert.True(PanelValueRangeCalculator.IsSignalSeries("BearishSignals"));
            Assert.True(PanelValueRangeCalculator.IsSignalSeries("BuySignals"));
            Assert.True(PanelValueRangeCalculator.IsSignalSeries("SellSignals"));
            Assert.True(PanelValueRangeCalculator.IsSignalSeries("Signals"));

            Assert.False(PanelValueRangeCalculator.IsSignalSeries("Main"));
            Assert.False(PanelValueRangeCalculator.IsSignalSeries("Signal"));
            Assert.False(PanelValueRangeCalculator.IsSignalSeries("Histogram"));
            Assert.False(PanelValueRangeCalculator.IsSignalSeries("Upper"));
            Assert.False(PanelValueRangeCalculator.IsSignalSeries("Lower"));
        }
    }
}
