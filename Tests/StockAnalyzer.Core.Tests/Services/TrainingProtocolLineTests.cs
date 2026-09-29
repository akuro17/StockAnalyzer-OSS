using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    public class TrainingProtocolLineTests
    {
        [Fact]
        public void TryParseStage_ValidLine_ReturnsStageName()
        {
            Assert.True(TrainingProtocolLine.TryParseStage("STAGE:dataset", out var stage));
            Assert.Equal("dataset", stage);
        }

        [Theory]
        [InlineData("PROGRESS:not-a-number")]
        [InlineData("")]
        public void TryParseStage_NonStageLine_ReturnsFalse(string line)
        {
            Assert.False(TrainingProtocolLine.TryParseStage(line, out _));
        }

        [Theory]
        [InlineData("PROGRESS:0", 0)]
        [InlineData("PROGRESS:57", 57)]
        [InlineData("PROGRESS:100", 100)]
        [InlineData("PROGRESS:150", 100)] // clamped to [0,100]
        [InlineData("PROGRESS:-5", 0)]    // clamped to [0,100]
        public void TryParsePercent_ValidLine_ClampsToRange(string line, int expected)
        {
            Assert.True(TrainingProtocolLine.TryParsePercent(line, out var percent));
            Assert.Equal(expected, percent);
        }

        [Theory]
        [InlineData("PROGRESS:abc")]
        [InlineData("STAGE:load")]
        public void TryParsePercent_InvalidLine_ReturnsFalse(string line)
        {
            Assert.False(TrainingProtocolLine.TryParsePercent(line, out _));
        }

        [Fact]
        public void TryParseMetric_ValidJson_ReturnsDictionary()
        {
            var ok = TrainingProtocolLine.TryParseMetric(
                "METRIC:{\"accuracy\":0.41,\"n_samples\":791}", out var metric);

            Assert.True(ok);
            Assert.NotNull(metric);
            Assert.Equal(0.41, metric!["accuracy"]);
            Assert.Equal(791.0, metric["n_samples"]);
        }

        [Fact]
        public void TryParseMetric_MalformedJson_ReturnsFalseWithoutThrowing()
        {
            Assert.False(TrainingProtocolLine.TryParseMetric("METRIC:{not json", out var metric));
            Assert.Null(metric);
        }

        [Fact]
        public void TryParseMetric_NonMetricLine_ReturnsFalse()
        {
            Assert.False(TrainingProtocolLine.TryParseMetric("STAGE:done", out _));
        }

        [Theory]
        [InlineData("ARTIFACT:onnx:I:\\stock\\artifacts\\model.onnx", "onnx", "I:\\stock\\artifacts\\model.onnx")]
        [InlineData("ARTIFACT:metrics:/tmp/model.onnx.metrics.json", "metrics", "/tmp/model.onnx.metrics.json")]
        public void TryParseArtifact_ValidLine_SplitsKindAndPath(string line, string expectedKind, string expectedPath)
        {
            Assert.True(TrainingProtocolLine.TryParseArtifact(line, out var kind, out var path));
            Assert.Equal(expectedKind, kind);
            Assert.Equal(expectedPath, path);
        }

        [Theory]
        [InlineData("ARTIFACT:no-colon-here")]
        [InlineData("ARTIFACT::")]
        [InlineData("STAGE:load")]
        public void TryParseArtifact_InvalidLine_ReturnsFalse(string line)
        {
            Assert.False(TrainingProtocolLine.TryParseArtifact(line, out _, out _));
        }
    }
}
