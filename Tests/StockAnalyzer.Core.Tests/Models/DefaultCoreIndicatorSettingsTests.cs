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
        Assert.Equal(67, defaults.Count);
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
            IndicatorType.TrueHigh, IndicatorType.TrueLow, IndicatorType.TrueRange,
            IndicatorType.Price
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
    public void GetDefault_KamaDefaults_ShouldMatchConstants()
    {
        var kama = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.KAMA);

        var param = Assert.IsType<CoreKamaParameter>(kama.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.KamaPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.KamaFastPeriod, param.Fast);
        Assert.Equal(IndicatorDefaultConstants.KamaSlowPeriod, param.Slow);
        Assert.Equal(IndicatorDefaultConstants.KamaColor.R, kama.Color.R);
        Assert.Equal(IndicatorDefaultConstants.KamaColor.G, kama.Color.G);
        Assert.Equal(IndicatorDefaultConstants.KamaColor.B, kama.Color.B);
        Assert.Equal(IndicatorDefaultConstants.DefaultOverlayThickness, kama.Thickness);
        Assert.True(kama.IsOverlay);
    }

    [Fact]
#pragma warning disable CS0618 // Deliberately testing the deprecated (Obsolete) enum member
    public void GetDefault_Ama_ShouldNotBeRegisteredInDefaults()
    {
        var defaults = DefaultCoreIndicatorSettings.GetDefault();
        Assert.DoesNotContain(defaults, s => s.TypeEnum == IndicatorType.AMA);
    }
#pragma warning restore CS0618


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
        Assert.Equal(CoreIndicatorCategory.Chart, vp.Category);

        // AutoHeal verification: healing fixes IsOverlay, Category, and clears OverlayPanelId
        var corrupted = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.VolumeProfile,
            IsOverlay = false,
            Category = CoreIndicatorCategory.Volume,
            OverlayPanelId = "Panel1"
        };
        DefaultCoreIndicatorSettings.AutoHeal(corrupted);
        Assert.True(corrupted.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Chart, corrupted.Category);
        Assert.Null(corrupted.OverlayPanelId);
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
    public void GetDefault_IfftInstantaneousPhaseDefaults_ShouldMatchConstants()
    {
        var phase = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.IFFTInstantaneousPhase);

        Assert.IsType<CoreIfftInstantaneousPhaseParameter>(phase.ParameterObject);
        Assert.False(phase.IsOverlay);
        // SineWave/LeadSine are sin()-bounded to exactly [-1, +1]; the panel's Y axis is fixed
        // to that range so it never auto-scales to a different window based on visible data.
        Assert.Equal(IndicatorDefaultConstants.IfftInstantaneousPhaseSineMinValue, phase.MinValue);
        Assert.Equal(IndicatorDefaultConstants.IfftInstantaneousPhaseSineMaxValue, phase.MaxValue);
        // "Phase" (Main, 0-360 deg) is deliberately absent: IndicatorRenderer suppresses it from
        // the chart (its scale would dominate the sub-panel's Y axis and hide SineWave/LeadSine),
        // so it must not appear in the settings dialogs' per-series color list either.
        Assert.Equal(2, phase.SeriesColors.Count);
        Assert.DoesNotContain(phase.SeriesColors, c => c.TargetSeries.Contains("Main"));
        Assert.Contains(phase.SeriesColors, c => c.TargetSeries.Contains("SineWave") && c.DisplayName == "Sine Wave");
        Assert.Contains(phase.SeriesColors, c => c.TargetSeries.Contains("LeadSine") && c.DisplayName == "Lead Sine");
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
    public void GetDefault_HilbertSineDefaults_ShouldMatchConstants()
    {
        var hs = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.HilbertSine);

        var param = Assert.IsType<CoreHilbertSineParameter>(hs.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformDefaultPeriod, param.DefaultPeriod);
        Assert.False(hs.IsOverlay);
        Assert.Equal(IndicatorDefaultConstants.HilbertSineMinValue, hs.MinValue);
        Assert.Equal(IndicatorDefaultConstants.HilbertSineMaxValue, hs.MaxValue);
        Assert.Equal(2, hs.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_HilbertTrendlineDefaults_ShouldMatchConstants()
    {
        var ht = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.HilbertTrendline);

        var param = Assert.IsType<CoreHilbertTrendlineParameter>(ht.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformDefaultPeriod, param.DefaultPeriod);
        Assert.True(ht.IsOverlay);
        Assert.Single(ht.SeriesColors);
    }

    [Fact]
    public void GetDefault_HilbertTrendModeDefaults_ShouldMatchConstants()
    {
        var htm = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.HilbertTrendMode);

        var param = Assert.IsType<CoreHilbertTrendModeParameter>(htm.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.HilbertTransformDefaultPeriod, param.DefaultPeriod);
        Assert.Equal(IndicatorDefaultConstants.HilbertTrendModeDefaultStabilityThreshold, param.StabilityThreshold);
        Assert.False(htm.IsOverlay);
        Assert.Equal(IndicatorDefaultConstants.HilbertTrendModeMinValue, htm.MinValue);
        Assert.Equal(IndicatorDefaultConstants.HilbertTrendModeMaxValue, htm.MaxValue);
        Assert.Single(htm.SeriesColors);
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

    [Fact]
    public void AutoHeal_WithLegacyIfftInstantaneousPhaseMissingRange_BackfillsMinAndMaxValue()
    {
        // Arrange: an instance persisted (workspace/template JSON) before the -1..1 fixed
        // range was added to the defaults -- MinValue/MaxValue are still null, which without
        // healing keeps the panel auto-scaling forever even after the app is rebuilt.
        var legacyPhase = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.IFFTInstantaneousPhase,
            SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
            {
                new SeriesColorConfig { Name = "SineWave", DisplayName = "Sine Wave", Color = IndicatorDefaultConstants.IfftInstantaneousPhaseSineColor, TargetSeries = new System.Collections.Generic.List<string> { "SineWave" } },
                new SeriesColorConfig { Name = "LeadSine", DisplayName = "Lead Sine", Color = IndicatorDefaultConstants.IfftInstantaneousPhaseLeadSineColor, TargetSeries = new System.Collections.Generic.List<string> { "LeadSine" } }
            }
        };
        Assert.Null(legacyPhase.MinValue);
        Assert.Null(legacyPhase.MaxValue);

        // Act
        DefaultCoreIndicatorSettings.AutoHeal(legacyPhase);

        // Assert
        Assert.Equal(IndicatorDefaultConstants.IfftInstantaneousPhaseSineMinValue, legacyPhase.MinValue);
        Assert.Equal(IndicatorDefaultConstants.IfftInstantaneousPhaseSineMaxValue, legacyPhase.MaxValue);
    }

    [Fact]
    public void AutoHeal_WithUserCustomizedMinMaxValue_DoesNotOverwriteExistingValue()
    {
        // Arrange: the user manually customized the fixed range in the Indicator Properties
        // dialog. AutoHeal must never clobber an explicit, already-set value.
        var customizedPhase = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.IFFTInstantaneousPhase,
            MinValue = -2m,
            MaxValue = 2m
        };

        // Act
        DefaultCoreIndicatorSettings.AutoHeal(customizedPhase);

        // Assert
        Assert.Equal(-2m, customizedPhase.MinValue);
        Assert.Equal(2m, customizedPhase.MaxValue);
    }

    [Fact]
    public void AutoHeal_WithLegacyNullParameterObject_OnTypeWithoutStaticDefaultEntry_BackfillsFromReflectionFallback()
    {
        // Arrange: CMO has no DefaultCoreIndicatorSettings entry, so before the catalog-select fix
        // (DynamicPeriodDriverRegistrationViewModel.OnSelectedCatalogItemChanged) existed, instances
        // registered from its catalog were persisted with a permanently null ParameterObject - leaving
        // Period un-editable forever, even after the app was rebuilt with the fix, because that fix only
        // runs on a fresh catalog selection, never when a previously-saved instance is reloaded from disk.
        var legacyCmo = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.CMO,
            ParameterObject = null
        };

        // Act
        DefaultCoreIndicatorSettings.AutoHeal(legacyCmo);

        // Assert
        var param = Assert.IsType<CoreSmaParameter>(legacyCmo.ParameterObject);
        Assert.Equal(14, param.Period);
    }

    [Fact]
    public void AutoHeal_WithExistingParameterObject_DoesNotOverwriteIt()
    {
        // Arrange: AutoHeal must never clobber a ParameterObject the user already configured.
        var cmo = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.CMO,
            ParameterObject = new CoreSmaParameter { Period = 42 }
        };

        // Act
        DefaultCoreIndicatorSettings.AutoHeal(cmo);

        // Assert
        var param = Assert.IsType<CoreSmaParameter>(cmo.ParameterObject);
        Assert.Equal(42, param.Period);
    }

    [Fact]
    public void AutoHeal_WithNullParameterObject_OnTrulyParameterlessType_StaysNull()
    {
        // Arrange: VWAP has no configurable parameters at all (by design, see
        // GetDefault_VwapDefaults_ShouldMatchConstants above) - the reflection fallback must also
        // yield null for it, so AutoHeal must not fabricate a parameter object where none belongs.
        var legacyVwap = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.VWAP,
            ParameterObject = null
        };

        // Act
        DefaultCoreIndicatorSettings.AutoHeal(legacyVwap);

        // Assert
        Assert.Null(legacyVwap.ParameterObject);
    }

    [Fact]
    public void GetDefault_HmmDefaults_ShouldMatchConstants()
    {
        var hmm = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.HiddenMarkovModel);

        var param = Assert.IsType<CoreHmmParameter>(hmm.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.HmmStates, param.States);
        Assert.Equal(IndicatorDefaultConstants.HmmPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.HmmMaxIterations, param.MaxIterations);
        Assert.Equal(IndicatorDefaultConstants.HmmTolerance, param.Tolerance);
        Assert.Equal(IndicatorDefaultConstants.HmmColor.R, hmm.Color.R);
        Assert.Equal(IndicatorDefaultConstants.HmmColor.G, hmm.Color.G);
        Assert.Equal(IndicatorDefaultConstants.HmmColor.B, hmm.Color.B);
        Assert.Equal(IndicatorDefaultConstants.DefaultSubPanelThickness, hmm.Thickness);
        Assert.Equal(IndicatorDefaultConstants.HmmMinValue, hmm.MinValue);
        Assert.Equal(IndicatorDefaultConstants.HmmMaxValue, hmm.MaxValue);
        Assert.False(hmm.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Math, hmm.Category);
    }

    [Fact]
    public void GetDefault_CorrelationDefaults_ShouldMatchConstants()
    {
        var corr = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.Correlation);

        var param = Assert.IsType<CoreCorrelationParameter>(corr.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.CorrelationPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.CorrelationColor.R, corr.Color.R);
        Assert.Equal(IndicatorDefaultConstants.CorrelationColor.G, corr.Color.G);
        Assert.Equal(IndicatorDefaultConstants.CorrelationColor.B, corr.Color.B);
        Assert.Equal(IndicatorDefaultConstants.DefaultSubPanelThickness, corr.Thickness);
        Assert.Equal(IndicatorDefaultConstants.CorrelationMinValue, corr.MinValue);
        Assert.Equal(IndicatorDefaultConstants.CorrelationMaxValue, corr.MaxValue);
        Assert.False(corr.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Other, corr.Category);
    }

    [Fact]
    public void GetDefault_FrechetOscillatorDefaults_ShouldMatchConstants()
    {
        var frechet = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.FrechetOscillator);

        var param = Assert.IsType<CoreFrechetOscillatorParameter>(frechet.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.FrechetOscillatorDefaultPeriod, param.Period);
        Assert.Equal(IndicatorDefaultConstants.FrechetOscillatorDefaultLag, param.Lag);
        Assert.Equal(IndicatorDefaultConstants.FrechetOscillatorColor.R, frechet.Color.R);
        Assert.Equal(IndicatorDefaultConstants.FrechetOscillatorColor.G, frechet.Color.G);
        Assert.Equal(IndicatorDefaultConstants.FrechetOscillatorColor.B, frechet.Color.B);
        Assert.Equal(IndicatorDefaultConstants.DefaultSubPanelThickness, frechet.Thickness);
        Assert.False(frechet.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, frechet.Category);
    }

    [Fact]
    public void GetDefault_SsaDefaults_ShouldMatchConstants()
    {
        var ssa = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SSA);

        var param = Assert.IsType<CoreSSAParameter>(ssa.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.SsaDefaultWindowSize, param.WindowSize);
        Assert.Equal(IndicatorDefaultConstants.SsaDefaultEmbeddingDimension, param.EmbeddingDimension);
        Assert.Equal(IndicatorDefaultConstants.SsaDefaultNumComponents, param.NumComponents);
        Assert.Equal(IndicatorDefaultConstants.SsaColor.R, ssa.Color.R);
        Assert.Equal(IndicatorDefaultConstants.SsaColor.G, ssa.Color.G);
        Assert.Equal(IndicatorDefaultConstants.SsaColor.B, ssa.Color.B);
        Assert.Equal(IndicatorDefaultConstants.DefaultOverlayThickness, ssa.Thickness);
        Assert.True(ssa.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Trend, ssa.Category);
    }

    [Fact]
    public void GetDefault_SsaResidualBandDefaults_ShouldMatchConstants()
    {
        var ssaBand = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SSAResidualBand);

        var param = Assert.IsType<CoreSSAResidualBandParameter>(ssaBand.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.SsaResidualBandDefaultWindowSize, param.WindowSize);
        Assert.Equal(IndicatorDefaultConstants.SsaResidualBandDefaultEmbeddingDimension, param.EmbeddingDimension);
        Assert.Equal(IndicatorDefaultConstants.SsaResidualBandDefaultNumComponents, param.NumComponents);
        Assert.Equal(IndicatorDefaultConstants.SsaResidualBandDefaultMultiplier, param.Multiplier);
        Assert.Equal(IndicatorDefaultConstants.SsaResidualBandCenterColor.R, ssaBand.Color.R);
        Assert.Equal(IndicatorDefaultConstants.DefaultBandThickness, ssaBand.Thickness);
        Assert.True(ssaBand.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volatility, ssaBand.Category);
        Assert.Equal(2, ssaBand.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_SsaCycleDefaults_ShouldMatchConstants()
    {
        var ssaCycle = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SSACycle);

        var param = Assert.IsType<CoreSSACycleParameter>(ssaCycle.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.SsaCycleDefaultWindowSize, param.WindowSize);
        Assert.Equal(IndicatorDefaultConstants.SsaCycleDefaultEmbeddingDimension, param.EmbeddingDimension);
        Assert.Equal(IndicatorDefaultConstants.SsaCycleDefaultDeltaPair, param.DeltaPair);
        Assert.Equal(IndicatorDefaultConstants.SsaCycleColor.R, ssaCycle.Color.R);
        Assert.Equal(IndicatorDefaultConstants.DefaultSubPanelThickness, ssaCycle.Thickness);
        Assert.False(ssaCycle.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, ssaCycle.Category);
        Assert.Equal(4, ssaCycle.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_SsaEntropyDefaults_ShouldMatchConstants()
    {
        var ssaEntropy = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SSAEntropy);

        var param = Assert.IsType<CoreSSAEntropyParameter>(ssaEntropy.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.SsaEntropyDefaultWindowSize, param.WindowSize);
        Assert.Equal(IndicatorDefaultConstants.SsaEntropyDefaultEmbeddingDimension, param.EmbeddingDimension);
        Assert.Equal(IndicatorDefaultConstants.SsaEntropyColor.R, ssaEntropy.Color.R);
        Assert.Equal(IndicatorDefaultConstants.DefaultSubPanelThickness, ssaEntropy.Thickness);
        Assert.False(ssaEntropy.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, ssaEntropy.Category);
        Assert.Equal(IndicatorDefaultConstants.SsaEntropyMinValue, ssaEntropy.MinValue);
        Assert.Equal(IndicatorDefaultConstants.SsaEntropyMaxValue, ssaEntropy.MaxValue);
    }

    [Fact]
    public void GetDefault_SsaSqueezeDefaults_ShouldMatchConstants()
    {
        var squeeze = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SSASqueeze);

        var param = Assert.IsType<CoreSSASqueezeParameter>(squeeze.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultWindowSize, param.WindowSize);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultEmbeddingDimension, param.EmbeddingDimension);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultNumComponents, param.NumComponents);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultSsaMultiplier, param.SsaMultiplier);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultAtrPeriod, param.AtrPeriod);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultAtrMultiplier, param.AtrMultiplier);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultMomentumPeriod, param.MomentumPeriod);
        Assert.Equal(IndicatorDefaultConstants.SsaSqueezeDefaultSqueezeThreshold, param.SqueezeThreshold);
        Assert.False(squeeze.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, squeeze.Category);
        Assert.Equal(3, squeeze.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_SsaSnrDefaults_ShouldMatchConstants()
    {
        var snr = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SSASNR);

        var param = Assert.IsType<CoreSSASNRParameter>(snr.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.SsaSnrDefaultWindowSize, param.WindowSize);
        Assert.Equal(IndicatorDefaultConstants.SsaSnrDefaultEmbeddingDimension, param.EmbeddingDimension);
        Assert.Equal(IndicatorDefaultConstants.SsaSnrDefaultNumComponents, param.NumComponents);
        Assert.Equal(IndicatorDefaultConstants.SsaSnrDefaultThresholdHigh, param.ThresholdHigh);
        Assert.Equal(IndicatorDefaultConstants.SsaSnrDefaultThresholdLow, param.ThresholdLow);
        Assert.False(snr.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Oscillator, snr.Category);
        Assert.Equal(4, snr.SeriesColors.Count);
    }

    [Fact]
    public void GetDefault_ArimaDefaults_ShouldMatchConstants()
    {
        var arima = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.ARIMA);

        var param = Assert.IsType<CoreArimaParameter>(arima.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.ArimaDefaultP, param.P);
        Assert.Equal(IndicatorDefaultConstants.ArimaDefaultD, param.D);
        Assert.Equal(IndicatorDefaultConstants.ArimaDefaultQ, param.Q);
        Assert.Equal(IndicatorDefaultConstants.ArimaDefaultPeriod, param.Period);
        Assert.True(arima.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Math, arima.Category);
        Assert.Equal(IndicatorDefaultConstants.ArimaColor.R, arima.Color.R);
        Assert.Equal(IndicatorDefaultConstants.ArimaColor.G, arima.Color.G);
        Assert.Equal(IndicatorDefaultConstants.ArimaColor.B, arima.Color.B);
        Assert.Equal(IndicatorDefaultConstants.DefaultOverlayThickness, arima.Thickness);
    }

    [Fact]
    public void GetDefault_PriceDefaults_ShouldHavePriceCloseAndDisplayNameClose()
    {
        var price = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.Price);

        Assert.Equal("Close", price.DisplayName);
        Assert.Equal(PriceType.Close, price.PriceSource);
        Assert.True(price.IsOverlay);
    }

    [Fact]
    public void CoreIndicatorSettings_PriceType_UpdateDisplayNameAndPriceSourceChange_UpdatesDisplayName()
    {
        var settings = new CoreIndicatorSettings
        {
            TypeEnum = IndicatorType.Price,
            PriceSource = PriceType.Close
        };
        settings.UpdateDisplayName();
        Assert.Equal("Close", settings.DisplayName);

        // Change price source to Median
        settings.PriceSource = PriceType.Median;
        Assert.Equal("Median (H+L)/2", settings.DisplayName);

        // Change price source to Heikin-Ashi Close
        settings.PriceSource = PriceType.HeikinAshiClose;
        Assert.Equal("Heikin-Ashi Close", settings.DisplayName);
    }
}

