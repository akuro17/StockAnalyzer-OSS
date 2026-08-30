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
        Assert.Equal(48, defaults.Count);
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
        var parameterlessTypes = new[]
        {
            IndicatorType.Volume, IndicatorType.PVT, IndicatorType.VPT, IndicatorType.OBV,
            IndicatorType.VWAP,
            IndicatorType.TrueHigh, IndicatorType.TrueLow, IndicatorType.TrueRange
        };
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

    [Fact]
    public void GetDefault_PrimeNumberBandsDefaults_ShouldMatchConstants()
    {
        var pnb = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.PrimeNumberBands);

        var param = Assert.IsType<CorePrimeNumberBandsParameter>(pnb.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.PrimeNumberBandsPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.PrimeNumberBandsScaleMultiplier, param.ScaleMultiplier);
        Assert.True(pnb.IsOverlay);
        Assert.Equal(2, pnb.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_FFTCycleDefaults_ShouldMatchConstants()
    {
        var fftCycle = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.FFTCycle);

        Assert.IsType<CoreFFTCycleParameter>(fftCycle.ParameterObject);
        Assert.False(fftCycle.IsOverlay);
        Assert.Equal(3, fftCycle.SeriesColors.Count);
        Assert.Contains(fftCycle.SeriesColors, c => c.TargetSeries.Contains("Main") && c.DisplayName == "FFT Cycle");
        Assert.Contains(fftCycle.SeriesColors, c => c.TargetSeries.Contains("CycleStrength") && c.DisplayName == "FFT Cycle Strength");
        Assert.Contains(fftCycle.SeriesColors, c => c.TargetSeries.Contains("Oscillator") && c.DisplayName == "FFT Cycle Oscillator");
    }

    [Fact]
    public void GetDefault_AdaptiveRsiDefaults_ShouldMatchConstants()
    {
        var adaptiveRsi = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.AdaptiveRSI);

        var param = Assert.IsType<CoreAdaptiveRsiParameter>(adaptiveRsi.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.AdaptiveRsiDefaultWindowSize, param.WindowSize);
        Assert.Equal(IndicatorDefaultConstants.AdaptiveRsiDefaultPeriod, param.DefaultPeriod);
        Assert.Equal(IndicatorDefaultConstants.AdaptiveRsiMinPeriod, param.MinPeriod);
        Assert.Equal(IndicatorDefaultConstants.AdaptiveRsiMaxPeriod, param.MaxPeriod);
        Assert.False(adaptiveRsi.IsOverlay);
        Assert.Equal(2, adaptiveRsi.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_HilbertTransformDefaults_ShouldMatchConstants()
    {
        var ht = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.HilbertTransform);

        var param = Assert.IsType<CoreHilbertTransformParameter>(ht.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformDefaultPeriod, param.DefaultPeriod);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformMinPeriod, param.MinPeriod);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformMaxPeriod, param.MaxPeriod);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformDefaultSmoothBeta, param.SmoothBeta);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformDefaultDeltaLimit, param.DeltaLimit);
        Assert.False(ht.IsOverlay);
        Assert.Equal(3, ht.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_VwapDefaults_ShouldMatchConstants()
    {
        var vwap = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.VWAP);

        Assert.Null(vwap.ParameterObject);
        Assert.Equal(PriceType.Typical, vwap.PriceSource);
        Assert.True(vwap.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volume, vwap.Category);
    }

    [Fact]
    public void AutoHeal_WithLegacyFftCyclePeriodDisplayName_MigratesToFftCycle()
    {
        // Arrange legacy instance loaded from old workspace JSON
        var legacyFftCycle = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.FFTCycle,
            SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
            {
                new SeriesColorConfig { Name = "Period", DisplayName = "FFT Cycle Period", Color = IndicatorDefaultConstants.FftCycleColor, TargetSeries = new System.Collections.Generic.List<string> { "Main" } },
                new SeriesColorConfig { Name = "Strength", DisplayName = "FFT Cycle Strength", Color = IndicatorDefaultConstants.FftCycleStrengthColor, TargetSeries = new System.Collections.Generic.List<string> { "CycleStrength" } },
                new SeriesColorConfig { Name = "Oscillator", DisplayName = "FFT Cycle Oscillator", Color = IndicatorDefaultConstants.FftCycleOscillatorColor, TargetSeries = new System.Collections.Generic.List<string> { "Oscillator" } }
            }
        };

        // Act
        DefaultCoreIndicatorSettings.AutoHeal(legacyFftCycle);

        // Assert
        var periodConfig = legacyFftCycle.SeriesColors.First(c => c.Name == "Period");
        Assert.Equal("FFT Cycle", periodConfig.DisplayName);
    }
}
