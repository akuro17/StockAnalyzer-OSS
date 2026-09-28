using System;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

public class HeadlessDispatcherTests
{
    [Theory]
    [InlineData("5", 5)]
    [InlineData("0.5", 0.5)]
    public void ParseSettleTimeout_ValidPositiveSeconds_IsUsed(string raw, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), HeadlessDispatcher.ParseSettleTimeout(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ParseSettleTimeout_MissingOrInvalid_FallsBackToDefault(string? raw)
    {
        Assert.Equal(TimeSpan.FromSeconds(2), HeadlessDispatcher.ParseSettleTimeout(raw));
    }
}
