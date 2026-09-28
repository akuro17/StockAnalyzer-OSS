using System;
using Xunit;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.MathUtils
{
    public class ChartMathTests
    {
        [Theory]
        [InlineData(1.9, 1.0, ChartRoundingMode.Floor, 1.0)]
        [InlineData(-1.1, 1.0, ChartRoundingMode.Floor, -2.0)]
        [InlineData(1.1, 1.0, ChartRoundingMode.Ceiling, 2.0)]
        [InlineData(-1.9, 1.0, ChartRoundingMode.Ceiling, -1.0)]
        [InlineData(2.5, 1.0, ChartRoundingMode.Round, 3.0)]
        [InlineData(-2.5, 1.0, ChartRoundingMode.Round, -3.0)]
        [InlineData(1.4, 1.0, ChartRoundingMode.Round, 1.0)]
        [InlineData(1.6, 1.0, ChartRoundingMode.Round, 2.0)]
        [InlineData(123.456, 0.1, ChartRoundingMode.Floor, 123.4)]
        [InlineData(123.456, 0.1, ChartRoundingMode.Ceiling, 123.5)]
        [InlineData(123.456, 0.1, ChartRoundingMode.Round, 123.5)]
        public void Quantize_CorrectlyRounds(double value, double step, ChartRoundingMode mode, double expected)
        {
            var result = ChartMath.Quantize((decimal)value, (decimal)step, mode);
            Assert.Equal((decimal)expected, result);
        }

        [Fact]
        public void Quantize_NoneMode_ReturnsOriginalValue()
        {
            decimal value = 123.456m;
            decimal step = 1.0m;
            var result = ChartMath.Quantize(value, step, ChartRoundingMode.None);
            Assert.Equal(value, result);
        }

        [Fact]
        public void Quantize_InvalidStep_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ChartMath.Quantize(100m, 0m, ChartRoundingMode.Floor));
            Assert.Throws<ArgumentOutOfRangeException>(() => ChartMath.Quantize(100m, -1m, ChartRoundingMode.Floor));
        }

        [Fact]
        public void Quantize_NiceNumbers_RoundsCorrectly()
        {
            // NiceNumbers mode now implemented (same as Round with MidpointRounding.AwayFromZero)
            var result = ChartMath.Quantize(100m, 1m, ChartRoundingMode.NiceNumbers);
            Assert.Equal(100m, result);

            var result2 = ChartMath.Quantize(123.456m, 5m, ChartRoundingMode.NiceNumbers);
            Assert.Equal(125m, result2);
        }
    }
}
