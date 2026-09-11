using Xunit;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Tests.Utilities
{
    public class RoundingHelperTests
    {
        [Fact]
        public void FormatInvariant_ShouldFormatDecimalWithDotSeparator()
        {
            decimal val = 1234.56m;
            string result = val.FormatInvariant("F2");
            Assert.Equal("1234.56", result);
        }

        [Fact]
        public void FormatInvariant_ShouldFormatDoubleWithDotSeparator()
        {
            double val = 9876.54;
            string result = val.FormatInvariant("F2");
            Assert.Equal("9876.54", result);
        }

        [Fact]
        public void RoundToNiceNumber_ShouldReturnExpectedNiceNumber()
        {
            decimal result = RoundingHelper.RoundToNiceNumber(14m);
            Assert.Equal(10m, result);
        }
    }
}
