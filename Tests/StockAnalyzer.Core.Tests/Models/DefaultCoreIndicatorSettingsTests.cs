using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using System.Linq;

namespace StockAnalyzer.Core.Tests.Models;

public class DefaultCoreIndicatorSettingsTests
{
    [Fact]
    public void GetDefault_ShouldReturnExpectedNumberOfIndicators()
    {
        var defaults = DefaultCoreIndicatorSettings.GetDefault();
        Assert.Equal(34, defaults.Count);
    }

    [Fact]
    public void GetDefault_AllIndicators_ShouldHaveNonNullTypeEnum()
    {
        var defaults = DefaultCoreIndicatorSettings.GetDefault();
        foreach (var setting in defaults)
        {
            Assert.NotNull(setting.TypeEnum);
        }
    }

    [Fact]
    public void GetDefault_AllParameterizedIndicators_ShouldHaveNonNullParameterObject()
    {
        // Indicators listed here intentionally have no parameter object
        var parameterlessTypes = new[] { IndicatorType.Volume, IndicatorType.PVT, IndicatorType.VPT, IndicatorType.OBV };
        var defaults = DefaultCoreIndicatorSettings.GetDefault()
            .Where(s => !parameterlessTypes.Contains(s.TypeEnum!.Value));
        foreach (var setting in defaults)
        {
            Assert.NotNull(setting.ParameterObject);
        }
    }

    [Fact]
    public void GetDefault_SmaDefaults_ShouldMatchConstants()
    {
        var sma = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SMA);

        var param = Assert.IsType<CoreSmaParameter>(sma.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.SmaPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.SmaColor.R, sma.Color.R);
        Assert.Equal(IndicatorDefaultConstants.SmaColor.G, sma.Color.G);
        Assert.Equal(IndicatorDefaultConstants.SmaColor.B, sma.Color.B);
        Assert.Equal(IndicatorDefaultConstants.DefaultOverlayThickness, sma.Thickness);
    }

    [Fact]
    public void GetDefault_EmaDefaults_ShouldMatchConstants()
    {
        var ema = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.EMA);

        var param = Assert.IsType<CoreEmaParameter>(ema.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.EmaPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.EmaColor.R, ema.Color.R);
    }

    [Fact]
    public void GetDefault_BollingerDefaults_ShouldMatchConstants()
    {
        var bb = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.BB);

        var param = Assert.IsType<CoreBollingerBandsParameter>(bb.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.BollingerPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.BollingerStdDevMultiplier, param.StdDevMultiplier);
        Assert.Equal(2, bb.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_RsiDefaults_ShouldMatchConstants()
    {
        var rsi = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.RSI);

        var param = Assert.IsType<CoreRsiParameter>(rsi.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.RsiPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.RsiMinValue, rsi.MinValue);
        Assert.Equal(IndicatorDefaultConstants.RsiMaxValue, rsi.MaxValue);
        Assert.False(rsi.IsOverlay);
    }

    [Fact]
    public void GetDefault_ParabolicSarDefaults_ShouldMatchConstants()
    {
        var psar = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.ParabolicSAR);

        var param = Assert.IsType<CoreParabolicSarParameter>(psar.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.ParabolicAccelerationStart, param.AccelerationStart);
        Assert.Equal(IndicatorDefaultConstants.ParabolicAccelerationStep, param.AccelerationStep);
        Assert.Equal(IndicatorDefaultConstants.ParabolicAccelerationMax, param.AccelerationMax);
        Assert.True(psar.IsOverlay);
        Assert.True(psar.UseUpDownColors);
    }

    [Fact]
    public void GetDefault_VolumeProfileDefaults_ShouldMatchConstants()
    {
        var vp = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.VolumeProfile);

        var param = Assert.IsType<CoreVolumeProfileParameter>(vp.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfileRowCount, param.RowCount);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfileOpacity, param.Opacity);
        Assert.True(vp.IsOverlay);
    }
}
