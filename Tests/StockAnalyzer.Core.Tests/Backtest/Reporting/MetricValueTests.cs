using System;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

public class MetricValueTests
{
    [Theory]
    [InlineData(MetricStatus.InsufficientData)]
    [InlineData(MetricStatus.NotApplicable)]
    [InlineData(MetricStatus.Undefined)]
    [InlineData(MetricStatus.PositiveInfinity)]
    [InlineData(MetricStatus.NumericFailure)]
    public void MetricValue_NonValid_ValueIsNull(MetricStatus status)
    {
        var mv = MetricValue.NonValid(status, MetricUnit.Dimensionless, MetricReason.ZeroDivisor);

        Assert.Null(mv.Value);
        Assert.Equal(status, mv.Status);
    }

    [Theory]
    [InlineData(MetricStatus.InsufficientData)]
    [InlineData(MetricStatus.NotApplicable)]
    [InlineData(MetricStatus.Undefined)]
    [InlineData(MetricStatus.PositiveInfinity)]
    [InlineData(MetricStatus.NumericFailure)]
    public void Constructor_NonValidStatusWithNonNullValue_Throws(MetricStatus status)
    {
        Assert.Throws<ArgumentException>(() => new MetricValue(1m, status, MetricUnit.Dimensionless, MetricReason.None));
    }

    [Fact]
    public void Constructor_ValidStatusWithNullValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MetricValue(null, MetricStatus.Valid, MetricUnit.Dimensionless, MetricReason.None));
    }

    [Fact]
    public void Valid_SetsStatusAndValue()
    {
        var mv = MetricValue.Valid(1.5m, MetricUnit.ReturnRatio);

        Assert.Equal(MetricStatus.Valid, mv.Status);
        Assert.Equal(1.5m, mv.Value);
        Assert.Equal(MetricReason.None, mv.Reason);
    }

    [Fact]
    public void NonValid_ValidStatus_Throws()
    {
        Assert.Throws<ArgumentException>(() => MetricValue.NonValid(MetricStatus.Valid, MetricUnit.Dimensionless, MetricReason.None));
    }
}
