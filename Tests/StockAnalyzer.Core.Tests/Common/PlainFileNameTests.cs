using StockAnalyzer.Core.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

public sealed class PlainFileNameTests
{
    [Theory]
    [InlineData("model.onnx", true)]
    [InlineData("model_m01.onnx.metrics.json", true)]
    [InlineData("a b.txt", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("sub/model.onnx", false)]
    [InlineData("sub\\model.onnx", false)]
    [InlineData("../model.onnx", false)]
    [InlineData("bad|name.onnx", false)]
    public void IsValid_AcceptsOnlyASingleSafePathSegment(string? name, bool expected) =>
        Assert.Equal(expected, PlainFileName.IsValid(name));
}
