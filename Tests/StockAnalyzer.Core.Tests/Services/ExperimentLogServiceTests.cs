#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ExperimentLogServiceTests
{
    [Fact]
    public async Task AppendAsync_ConcurrentCalls_WritesAllLinesWithoutSharingViolation()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "sa_explog_test_" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            const int lineCount = 20;
            var tasks = Enumerable.Range(0, lineCount)
                .Select(i => Task.Run(() => ExperimentLogService.AppendAsync(tempFile, $"{{\"run\":{i}}}\n")))
                .ToArray();

            await Task.WhenAll(tasks);

            Assert.True(File.Exists(tempFile));
            var lines = (await File.ReadAllLinesAsync(tempFile))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            Assert.Equal(lineCount, lines.Count);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
    }
}
