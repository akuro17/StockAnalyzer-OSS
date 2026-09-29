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

    }
}
