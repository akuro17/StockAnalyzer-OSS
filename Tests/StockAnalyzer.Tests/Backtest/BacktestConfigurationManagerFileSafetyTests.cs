using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

public sealed class BacktestConfigurationManagerFileSafetyTests
{
    [Fact]
    public async Task CorruptJson_LoadError_DoesNotModifyOriginalFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"backtest_configuration_corrupt_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ invalid json");
            byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var manager = new BacktestConfigurationManager(new MockStockAnalyzerSettings(), path);

            BacktestConfigurationLoadResult result = await manager.LoadAsync();
            byte[] after = SHA256.HashData(await File.ReadAllBytesAsync(path));

            Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
            Assert.Equal(before, after);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
