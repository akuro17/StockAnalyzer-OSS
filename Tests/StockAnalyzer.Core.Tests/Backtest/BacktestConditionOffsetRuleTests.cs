using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Y:\Temp\sa_improvement_plan_BacktestOffsetRuleSSoT.md: the single definition of the condition Offset bound.</summary>
public class BacktestConditionOffsetRuleTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(BacktestConditionOffsetRule.MinOffset, true)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    public void IsValid_AcceptsOnlyOffsetsAtOrAboveTheMinimum(int offset, bool expected)
    {
        Assert.Equal(expected, BacktestConditionOffsetRule.IsValid(offset));
    }

    [Theory]
    [InlineData(0, 10, true)]
    [InlineData(10, 10, true)]
    [InlineData(11, 10, false)]
    public void IsWithinLimit_IncludesTheLimitItself(int offset, int max, bool expected)
    {
        Assert.Equal(expected, BacktestConditionOffsetRule.IsWithinLimit(offset, max));
    }

    [Fact]
    public void DescribeAboveLimit_NamesTheSideTheValueAndTheLimit()
    {
        string message = BacktestConditionOffsetRule.DescribeAboveLimit(1, "Left", 600, 500);

        Assert.Contains("ConditionEntries[1].Left.Offset", message);
        Assert.Contains("600", message);
        Assert.Contains("500", message);
    }

    [Fact]
    public void Describe_NamesTheEntrySideAndValue()
    {
        string message = BacktestConditionOffsetRule.Describe(2, "Right", -7);

        Assert.Contains("ConditionEntries[2].Right.Offset", message);
        Assert.Contains("-7", message);
    }
}
