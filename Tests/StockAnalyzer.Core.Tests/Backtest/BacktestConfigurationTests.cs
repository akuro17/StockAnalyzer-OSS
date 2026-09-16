using System;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class BacktestConfigurationTests
{
    private static BacktestConfiguration ValidConfig(Action<BacktestConfigurationBuilder>? customize = null)
    {
        var builder = new BacktestConfigurationBuilder();
        customize?.Invoke(builder);
        return builder.Build();
    }

    private sealed class BacktestConfigurationBuilder
    {
        public decimal InitialCapital = 1000m;
        public decimal CommissionFlat = 0m;
        public decimal CommissionPerUnit = 0m;
        public decimal SlippageRatio = 0m;
        public int TradingDaysPerYear = 252;
        public PositionSizingModel SizingModel = PositionSizingModel.FixedQuantity;
        public decimal SizingParameter = 1m;
        public decimal InitialMarginRatio = 0.30m;
        public decimal MaintenanceMarginRatio = 0.20m;
        public decimal LiquidationPenaltyRatio = 0m;

        public BacktestConfiguration Build() => new()
        {
            InitialCapital = InitialCapital,
            CommissionFlat = CommissionFlat,
            CommissionPerUnit = CommissionPerUnit,
            SlippageRatio = SlippageRatio,
            TradingDaysPerYear = TradingDaysPerYear,
            SizingModel = SizingModel,
            SizingParameter = SizingParameter,
            InitialMarginRatio = InitialMarginRatio,
            MaintenanceMarginRatio = MaintenanceMarginRatio,
            LiquidationPenaltyRatio = LiquidationPenaltyRatio,
        };
    }

    [Fact]
    public void ValidConfiguration_ConstructsAndValidatesWithoutThrowing()
    {
        var config = ValidConfig();
        config.Validate();
        Assert.Equal(1000m, config.InitialCapital);
        Assert.Equal(252, config.TradingDaysPerYear);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InitialCapital_NotPositive_Throws(decimal capital)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.InitialCapital = capital));
    }

    [Fact]
    public void CommissionFlat_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.CommissionFlat = -0.01m));
    }

    [Fact]
    public void CommissionPerUnit_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.CommissionPerUnit = -0.01m));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    [InlineData(1.01)]
    public void SlippageRatio_OutOfRange_Throws(double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.SlippageRatio = (decimal)ratio));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void TradingDaysPerYear_OutOfRange_Throws(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.TradingDaysPerYear = days));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.01)]
    public void InitialMarginRatio_OutOfRange_Throws(double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.InitialMarginRatio = (decimal)ratio));
    }

    [Fact]
    public void MaintenanceMarginRatio_NotPositive_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.MaintenanceMarginRatio = 0m));
    }

    [Fact]
    public void MaintenanceMarginRatio_NotLessThanInitial_ThrowsOnValidate()
    {
        var config = ValidConfig(b =>
        {
            b.InitialMarginRatio = 0.20m;
            b.MaintenanceMarginRatio = 0.20m;
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void LiquidationPenaltyRatio_OutOfRange_Throws(double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidConfig(b => b.LiquidationPenaltyRatio = (decimal)ratio));
    }

    [Fact]
    public void FixedQuantity_NonIntegerSizingParameter_ThrowsOnValidate()
    {
        var config = ValidConfig(b =>
        {
            b.SizingModel = PositionSizingModel.FixedQuantity;
            b.SizingParameter = 1.5m;
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
    }

    [Fact]
    public void FixedQuantity_LessThanOne_ThrowsOnValidate()
    {
        var config = ValidConfig(b =>
        {
            b.SizingModel = PositionSizingModel.FixedQuantity;
            b.SizingParameter = 0m;
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.01)]
    public void PercentOfEquity_OutOfRange_ThrowsOnValidate(double sizingParameter)
    {
        var config = ValidConfig(b =>
        {
            b.SizingModel = PositionSizingModel.PercentOfEquity;
            b.SizingParameter = (decimal)sizingParameter;
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
    }

    [Fact]
    public void PercentOfEquity_ValidFraction_DoesNotThrowOnValidate()
    {
        var config = ValidConfig(b =>
        {
            b.SizingModel = PositionSizingModel.PercentOfEquity;
            b.SizingParameter = 0.5m;
        });
        config.Validate();
    }
}
