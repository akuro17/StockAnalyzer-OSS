using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlWireContractTests
{
    [Fact]
    public void WireDefaultsIncludeDecimalConstantsWithoutChangingTheirValues()
    {
        var defaults = RlContract.ToWireDefaults();
        Assert.Equal(RlContract.Gamma, Assert.IsType<decimal>(defaults[nameof(RlContract.Gamma)]));
        Assert.Equal(RlContract.LearningRate, Assert.IsType<decimal>(defaults[nameof(RlContract.LearningRate)]));
        Assert.Equal(RlContract.InitialMarginRatio, Assert.IsType<decimal>(defaults[nameof(RlContract.InitialMarginRatio)]));
        Assert.Equal(RlContract.PanelHeight, Assert.IsType<decimal>(defaults[nameof(RlContract.PanelHeight)]));
        Assert.Equal(RlContract.AdamEpsilon, Assert.IsType<decimal>(defaults[nameof(RlContract.AdamEpsilon)]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\uFEFF{}\n")]
    public async Task StrictFramesRejectPartialLinesAndBom(string value)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value));
        var reader = new BoundedLineReader(stream, RlContract.IpcFrameByteLimit, strict: true);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StrictFramesRejectMalformedUtf8()
    {
        using var stream = new MemoryStream(new byte[] { 0xff, (byte)'\n' });
        var reader = new BoundedLineReader(stream, RlContract.IpcFrameByteLimit, strict: true);
        await Assert.ThrowsAsync<DecoderFallbackException>(async () => await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StrictFramesPreserveBufferedRequestsWithoutConsumingTheNextFrame()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"sequence\":1}\n{\"sequence\":2}\n"));
        var reader = new BoundedLineReader(stream, RlContract.IpcFrameByteLimit, strict: true);
        Assert.Equal("{\"sequence\":1}", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("{\"sequence\":2}", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }
}
