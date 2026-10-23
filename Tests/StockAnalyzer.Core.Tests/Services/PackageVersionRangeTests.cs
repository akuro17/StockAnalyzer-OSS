using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PackageVersionRangeTests
    {
        [Theory]
        [InlineData("2.3.3", 3)]
        [InlineData("1.0", 2)]
        [InlineData("23.0.1", 24)]
        [InlineData(" 1.39.0 ", 2)]
        // N-03 A: a 0.x pin is bounded by the next major as well, not by the next minor.
        [InlineData("0.5.3", 1)]
        public void UpperMajor_ReturnsNextMajor(string pin, int expected)
        {
            Assert.Equal(expected, PackageVersionRange.UpperMajor(pin));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("latest")]
        public void UpperMajor_WithoutLeadingNumber_ReturnsNull(string? pin)
        {
            Assert.Null(PackageVersionRange.UpperMajor(pin));
        }

        [Fact]
        public void UpgradeSpecifier_BuildsRangeFromPin()
        {
            Assert.Equal("pandas>=2.3.3,<3.0.0", PackageVersionRange.UpgradeSpecifier("pandas", "2.3.3"));
            Assert.Equal("yfinance>=1.0,<2.0.0", PackageVersionRange.UpgradeSpecifier("yfinance", "1.0"));
        }

        [Fact]
        public void UpgradeSpecifier_WithoutUsablePin_ReturnsNull()
        {
            Assert.Null(PackageVersionRange.UpgradeSpecifier("pandas", null));
            Assert.Null(PackageVersionRange.UpgradeSpecifier("pandas", "latest"));
        }
    }
}
