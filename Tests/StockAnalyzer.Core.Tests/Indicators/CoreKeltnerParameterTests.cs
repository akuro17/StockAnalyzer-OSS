using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>WebAiExpansion plan 12 T7 (D-5 A, D-2 A): the Keltner parameter checks every range of its attributes and declares the first non-null index as its warm-up.</summary>
public class CoreKeltnerParameterTests
{
    [Fact]
    public void Defaults_PassValidate()
    {
        new CoreKeltnerParameter().Validate();
    }

    [Theory]
    [InlineData(20, 10, 19)]   // the default: max(EmaPeriod - 1, AtrPeriod)
    [InlineData(1, 1, 1)]
    [InlineData(5, 30, 30)]
    [InlineData(50, 20, 49)]
    public void GetRequiredWarmupBars_IsTheFirstNonNullIndexOfTheChannel(int emaPeriod, int atrPeriod, int expected)
    {
        var parameter = new CoreKeltnerParameter { EmaPeriod = emaPeriod, AtrPeriod = atrPeriod };

        Assert.Equal(expected, parameter.GetRequiredWarmupBars());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(IndicatorDefaultConstants.DefaultPeriodMax)]
    public void Validate_AcceptsThePeriodBounds(int period)
    {
        new CoreKeltnerParameter { EmaPeriod = period, AtrPeriod = period }.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IndicatorDefaultConstants.DefaultPeriodMax + 1)]
    public void Validate_RejectsAnEmaPeriodOutsideTheRange_NamingTheProperty(int period)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKeltnerParameter { EmaPeriod = period }.Validate());

        Assert.Equal(nameof(CoreKeltnerParameter.EmaPeriod), ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IndicatorDefaultConstants.DefaultPeriodMax + 1)]
    public void Validate_RejectsAnAtrPeriodOutsideTheRange_NamingTheProperty(int period)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKeltnerParameter { AtrPeriod = period }.Validate());

        Assert.Equal(nameof(CoreKeltnerParameter.AtrPeriod), ex.ParamName);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("10.0")]
    public void Validate_AcceptsTheMultiplierBounds(string multiplier)
    {
        new CoreKeltnerParameter { Multiplier = decimal.Parse(multiplier, System.Globalization.CultureInfo.InvariantCulture) }.Validate();
    }

    [Theory]
    [InlineData("0.09")]
    [InlineData("0")]
    [InlineData("-2.0")]
    [InlineData("10.1")]
    public void Validate_RejectsAMultiplierOutsideTheRange_NamingTheProperty(string multiplier)
    {
        decimal value = decimal.Parse(multiplier, System.Globalization.CultureInfo.InvariantCulture);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CoreKeltnerParameter { Multiplier = value }.Validate());

        Assert.Equal(nameof(CoreKeltnerParameter.Multiplier), ex.ParamName);
    }
}
