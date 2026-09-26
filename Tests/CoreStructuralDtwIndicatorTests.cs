using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;

namespace StockAnalyzer.Tests.Models.Indicators.Advanced
{
    public class CoreStructuralDtwIndicatorTests
    {
        private static List<CoreCandleData> CreateCandles(params decimal[] closes)
        {
            return closes
                .Select((c, i) => new CoreCandleData(DateTime.Today.AddDays(i), c, c, c, c, 0))
                .ToList();
        }

        private static async Task<IReadOnlyList<decimal?>> RunAsync(CoreStructuralDtwIndicator indicator, List<CoreCandleData> candles)
        {
            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext());
            Assert.True(result.IsSuccessful);
            Assert.Single(result.SeriesNames);
            return result.GetSeries(IndicatorResult.MainSeriesName);
        }

        [Fact]
        public async Task CalculateAsync_WithoutPythonService_Succeeds()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 2, Lag = 2 };

            var series = await RunAsync(indicator, CreateCandles(1, 2, 3, 4, 5));

            Assert.Equal(5, series.Count);
        }

        [Fact]
        public async Task CalculateAsync_WithNotEnoughData_ReturnsNullArray()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 14, Lag = 14 };

            var series = await RunAsync(indicator, CreateCandles(Enumerable.Repeat(100m, 10).ToArray()));

            Assert.Equal(10, series.Count);
            Assert.All(series, item => Assert.Null(item));
        }

        [Fact]
        public async Task CalculateAsync_EmptyCandles_ReturnsEmptySuccess()
        {
            // The base class short-circuits empty input to IndicatorResult.Empty() before CalculateCore runs.
            var indicator = new CoreStructuralDtwIndicator();

            var result = await indicator.CalculateAsync(new List<CoreCandleData>(), new CoreExecutionContext());

            Assert.True(result.IsSuccessful);
            Assert.Empty(result.SeriesNames);
        }

        [Fact]
        public async Task CalculateAsync_LinearTrend_WarmupIsNullAndDistanceIsZero()
        {
            // Both Z-normalized windows of a linear ramp are identical, so DTW distance is 0.
            var indicator = new CoreStructuralDtwIndicator { Period = 2, Lag = 2 };

            var series = await RunAsync(indicator, CreateCandles(1, 2, 3, 4, 5));

            Assert.Null(series[0]);
            Assert.Null(series[1]);
            Assert.Null(series[2]);
            Assert.Equal(0.0m, series[3]);
            Assert.Equal(0.0m, series[4]);
        }

        [Fact]
        public async Task CalculateAsync_FlatWindows_ReturnsZeroForBothFlatAndNullForOneFlat()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 2, Lag = 2 };

            var series = await RunAsync(indicator, CreateCandles(100, 100, 100, 100, 105));

            Assert.Equal(0.0m, series[3]);
            Assert.Null(series[4]);
        }

        [Fact]
        public async Task CalculateAsync_MissingCandleInWindow_ReturnsNullForAffectedBarsOnly()
        {
            // Index 4 is missing; with Period = 2 and Lag = 2 the windows of bars 4..7 touch it.
            var candles = CreateCandles(1, 2, 3, 4, 5, 6, 7, 8, 9);
            candles[4] = null!;
            var indicator = new CoreStructuralDtwIndicator { Period = 2, Lag = 2 };

            var series = await RunAsync(indicator, candles);

            Assert.Equal(0.0m, series[3]);
            for (int i = 4; i <= 7; i++) Assert.Null(series[i]);
            Assert.Equal(0.0m, series[8]);
        }

        [Fact]
        public async Task CalculateAsync_DifferentShapes_ReturnsPositiveDistance()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 3, Lag = 1, WarpingRadius = 0 };

            var series = await RunAsync(indicator, CreateCandles(1, 3, 2, 5, 4));

            Assert.Null(series[0]);
            Assert.Null(series[1]);
            Assert.NotNull(series[3]);
            Assert.True(series[3] > 0m);
        }

        [Fact]
        public async Task CalculateAsync_DoesNotReferenceFutureBars()
        {
            var indicator = new CoreStructuralDtwIndicator { Period = 3, Lag = 1 };
            var shortSeries = await RunAsync(indicator, CreateCandles(1, 3, 2, 5, 4));
            var longSeries = await RunAsync(new CoreStructuralDtwIndicator { Period = 3, Lag = 1 }, CreateCandles(1, 3, 2, 5, 4, 9, 1));

            for (int i = 0; i < shortSeries.Count; i++)
            {
                Assert.Equal(shortSeries[i], longSeries[i]);
            }
        }
    }
}
