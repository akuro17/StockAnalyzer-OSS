using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Tests.TestHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests
{
    public class CoreEgarchIndicatorTests
    {
        private static List<CoreCandleData> CreateTestCandles(int count)
        {
            var startDate = DateTime.Today;
            return Enumerable.Range(0, count).Select(i => new CoreCandleData(
                startDate.AddDays(i), 100 + i, 102 + i, 98 + i, 100 + i, 1000
            )).ToList();
        }

        [Fact]
        public void Calculate_Synchronous_ReturnsFailure()
        {
            var indicator = new CoreEgarchIndicator { P = 1, Q = 1 };
            var candles = CreateTestCandles(10);

            // Synchronous Calculate catches exceptions and returns failure
            var result = indicator.Calculate(candles);
            Assert.False(result.IsSuccessful);
            Assert.Contains("EGARCH requires async execution", result.ErrorMessage);
        }

        private class EgarchMockPythonService : MockPythonServiceBase
        {
            public override Task<string> CalculateEgarchAsync(int p = 1, int q = 1)
            {
                var json = @"{ ""status"": ""ok"", ""result"": [null, null, null, null, 1.0, 1.0, 1.0] }";
                return Task.FromResult(json);
            }
        }

        [Fact]
        public async Task CalculateAsync_WithMockService_ReturnsExpectedValues()
        {
            var indicator = new CoreEgarchIndicator { P = 1, Q = 1 };
            var candles = CreateTestCandles(7);
            var mockService = new EgarchMockPythonService();
            var result = await indicator.CalculateAsync(candles, new CoreExecutionContext(mockService));

            Assert.True(result.IsSuccessful, "Result should be successful");
            Assert.NotNull(result.MainValues);
            
            var values = result.MainValues;
            Assert.Equal(7, values.Count);
            
            // Verify expected values from MockPythonService
            Assert.Null(values[0]);
            Assert.Null(values[1]);
            Assert.Null(values[2]);
            Assert.Null(values[3]);
            Assert.Equal(1.0m, values[4]);
            Assert.Equal(1.0m, values[5]);
            Assert.Equal(1.0m, values[6]);
        }
    }
}
