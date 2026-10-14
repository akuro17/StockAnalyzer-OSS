using System;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class RollingLogBufferTests
{
    [Fact]
    public void AppendLine_WithinCapacity_RetainsAllLines()
    {
        var buffer = new RollingLogBuffer(capacity: 5);
        buffer.AppendLine("line1");
        buffer.AppendLine("line2");
        buffer.AppendLine("line3");

        Assert.Equal(3, buffer.Count);
        var output = buffer.ToString();
        Assert.Contains("line1", output);
        Assert.Contains("line2", output);
        Assert.Contains("line3", output);
    }

    [Fact]
    public void AppendLine_ExceedingCapacity_PurgesOldestLinesAndPreservesOrder()
    {
        var buffer = new RollingLogBuffer(capacity: 3);
        buffer.AppendLine("line1");
        buffer.AppendLine("line2");
        buffer.AppendLine("line3");
        buffer.AppendLine("line4");
        buffer.AppendLine("line5");

        Assert.Equal(3, buffer.Count);
        var output = buffer.ToString().Trim().Split(Environment.NewLine);
        Assert.Equal(3, output.Length);
        Assert.Equal("line3", output[0]);
        Assert.Equal("line4", output[1]);
        Assert.Equal("line5", output[2]);
    }

    [Fact]
    public void AppendLine_NullLine_IsSafelyIgnored()
    {
        var buffer = new RollingLogBuffer(capacity: 5);
        buffer.AppendLine(null);
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.ToString());
    }

    [Fact]
    public async Task AppendLine_ConcurrentAppends_ThreadSafe()
    {
        var buffer = new RollingLogBuffer(capacity: 100);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                buffer.AppendLine($"worker_{worker}_{i}");
            }
        })));

        Assert.Equal(100, buffer.Count);
        var output = buffer.ToString();
        Assert.False(string.IsNullOrWhiteSpace(output));
    }
}
