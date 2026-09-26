using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests
{
    // Regression test: a ~1369-candle dataset (matching a real ticker's Daily history) used to fail with
    // "Received empty response." because the Windows pipe transport mishandled a candle payload larger than one
    // 65536-byte read (~1300+ candles). The FFT Cycle indicator no longer uses Python, so the transfer is
    // exercised directly: the whole Arrow payload must reach server.py and be acknowledged with its row count.
    [Collection("PythonIpc")]
    public class ReproFFTCycleLargeRealisticDatasetTests
    {
        private sealed class LargeTransferSettings : PythonIpcTestSettingsBase
        {
            public override string PipeName => "reprofftcyclelargepipe1";
        }

        [Fact]
        public async Task SendCandlesAsync_With1369RealisticCandles_TransfersEveryRow()
        {
            var pythonService = new PythonService(new LargeTransferSettings());
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var candles = new List<CandleData>();
            var start = new System.DateTime(2021, 1, 1);
            var rnd = new System.Random(7);
            decimal price = 2000m;
            for (int i = 0; i < 1369; i++)
            {
                price += (decimal)(rnd.NextDouble() * 40 - 20);
                if (price < 100m) price = 100m;
                candles.Add(new CandleData(start.AddDays(i), price, price + 30m, price - 30m, price, 100000));
            }

            string responseJson = await pythonService.ExecuteTransactionAsync(() => pythonService.SendCandlesAsync(candles));

            using var doc = JsonDocument.Parse(responseJson);
            Assert.Equal("transfer_complete", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(candles.Count, doc.RootElement.GetProperty("rows").GetInt32());
        }
    }
}
