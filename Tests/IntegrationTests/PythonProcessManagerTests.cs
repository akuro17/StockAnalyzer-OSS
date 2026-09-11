using System;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    [Collection("PythonIntegration")]
    public class PythonProcessManagerTests
    {
        [Fact]
        public async Task TestPingAsync()
        {
            // Arrange
            var settings = new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings();
            await using var service = new PythonService(settings);
            
            // Act
            // This might take a while on first run to download Python
            await service.InitializeExternalProcessAsync();
            var response = await service.PingExternalProcessAsync();

            // Assert
            Assert.NotNull(response);
            Assert.Contains("pong", response);
        }

        [Fact]
        public async Task TestSendCandlesAsync()
        {
            // Arrange
            var settings = new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings();
            await using var service = new PythonService(settings);
            await service.InitializeExternalProcessAsync();

            var candles = new System.Collections.Generic.List<StockAnalyzer.Core.Models.CandleData>();
            for (int i = 0; i < 100; i++)
            {
                candles.Add(new StockAnalyzer.Core.Models.CandleData(
                    DateTime.Now.AddMinutes(i),
                    100 + i, 105 + i, 95 + i, 102 + i, 1000 + i
                ));
            }

            // Act
            var response = await service.SendCandlesAsync(candles);

            // Assert
            Assert.NotNull(response);
            Assert.Contains("transfer_complete", response);
            Assert.Contains("\"rows\": 100", response);
        }

        [Fact]
        public async Task TestCalculateEgarchAsync()
        {
            // Arrange
            var settings = new StockAnalyzer.Avalonia.Services.MockStockAnalyzerSettings();
            await using var service = new PythonService(settings);
            await service.InitializeExternalProcessAsync();

            var candles = new System.Collections.Generic.List<StockAnalyzer.Core.Models.CandleData>();
            // Generate some random walk data to simulate price movement
            var rand = new Random(42);
            double price = 100;
            for (int i = 0; i < 200; i++) // Need enough data for ARCH model to converge
            {
                price *= (1.0 + (rand.NextDouble() - 0.5) * 0.02);
                candles.Add(new StockAnalyzer.Core.Models.CandleData(
                    DateTime.Now.AddDays(i),
                    (decimal)price, (decimal)(price * 1.01), (decimal)(price * 0.99), (decimal)price, 1000
                ));
            }

            // Act
            // 1. Send Data
            await service.SendCandlesAsync(candles);
            
            // 2. Calculate EGARCH
            var response = await service.CalculateEgarchAsync(p: 1, q: 1);

            // Assert
            Assert.NotNull(response);
            Assert.Contains("result", response);
            // Check for numeric values in result array (not just nulls)
            // We expect some valid volatility numbers at the end
            Assert.DoesNotContain("\"error\"", response);
        }

    }
}
