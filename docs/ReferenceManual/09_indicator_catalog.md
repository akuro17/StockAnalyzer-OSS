# 09. Indicator Catalog & Polymorphic Parameter Reference

## 1. Overview & Polymorphism Rules

In StockAnalyzer, technical indicators are configured via `CoreIndicatorSettings`. Because different indicators have completely different mathematical parameters (e.g., SMA needs only `Period`, while MACD needs `ShortPeriod`, `LongPeriod`, and `SignalPeriod`), parameter objects inherit from `CoreIndicatorParameterBase` and are serialized polymorphically.

### The `"$type"` Invariant
`WorkspacePolymorphicResolver` discovers all non-abstract subclasses of `CoreIndicatorParameterBase`. When serializing or deserializing a JSON object containing `ParameterObject` or `Parameters`:
- The JSON object **MUST** contain a `"$type"` property matching the exact C# parameter class name (e.g. `"$type": "CoreSmaParameter"`).
- Omitting `"$type"` causes a deserialization exception or falls back to an empty base object, ignoring user parameters.

---

<!-- BEGIN GENERATED CATALOG -->
## 2. Core Indicator Types & Parameter Catalog

This section is generated from the production code (`IndicatorFactory`, each indicator's default settings, its parameter class and its calculated output series). Every registered indicator is listed. Use the **Ordinal** (or the member name string) for `TypeEnum`/`Type`/`IndicatorType`, the **Parameter Class** as `"$type"` and the listed property names verbatim (unknown property names are silently ignored and the default is used). Enum-typed parameter properties are written as integers (see Section 3).

### Category: Chart

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `PivotPointsAdvanced` | `67` | `(none)` | (no parameters) | `true` | `"Main"` |
| `ClassicPivotPoints` | `130` | `(none)` | (no parameters) | `true` | `"Main"`, `"R1"`, `"R2"`, `"R3"`, `"S1"`, `"S2"`, `"S3"` |
| `VolumeProfile` | `131` | `CoreVolumeProfileParameter` | `Period` (int, default `200`), `RowCount` (int, default `50`), `Opacity` (double, default `0.3`), `Mode` (VolumeDistributionMode, default `0 (Proportional)`), `Side` (DisplaySide, default `0 (Left)`), `ValueAreaColor` (IndicatorColor?, default `#FFFFEB3B`) | `true` | `"Main"` |
| `MarketStructureShift` | `138` | `CoreMarketStructureShiftParameter` | `ZigZagThreshold` (decimal, default `5.0`) | `true` | `"BOS_Lines"`, `"CHoCH_Lines"` |
| `GranvilleLaw` | `140` | `CoreGranvilleLawParameter` | `MaPeriod` (int, default `200`), `SlopePeriod` (int, default `5`), `DeviationThreshold` (decimal, default `10.0`), `BounceTolerance` (decimal, default `0.5`), `FlatThreshold` (decimal, default `0.05`), `ShowSubWindowBar` (bool, default `true`) | `true` | `"MA"`, `"Signals"` |
| `TimeAtPrice` | `179` | `CoreTimeAtPriceParameter` | `Period` (int, default `200`), `RowCount` (int, default `50`), `PriceType` (PriceType, default `0 (Close)`), `PriceSource` (PriceType, default `0 (Close)`), `Opacity` (double, default `0.3`), `Side` (DisplaySide, default `0 (Left)`), `ValueAreaColor` (IndicatorColor?, default `#FFFFEB3B`) | `true` | `"Main"` |

### Category: Math

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `MurreyMath` | `74` | `CoreMurreyMathParameter` | `Period` (int, default `64`, range `[1, 10000]`) | `true` | `"Main"`, `"Level0_8"`, `"Level1_8"`, `"Level2_8"`, `"Level3_8"`, `"Level4_8"`, `"Level5_8"`, `"Level6_8"`, `"Level7_8"`, `"Level8_8"` |

### Category: Oscillator

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `Momentum` | `16` | `CoreSmaParameter` | `Period` (int, default `10`, range `[1, 1000]`) | `false` | `"Main"` |
| `SMI` | `17` | `CoreSmiParameter` | `Period` (int, default `14`), `Smooth1` (int, default `5`), `Smooth2` (int, default `3`), `SignalPeriod` (int, default `5`) | `false` | `"Main"`, `"SignalLine"` |
| `BandWidth` | `18` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `BollingerBandsRatio` | `19` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `FisherTransform` | `23` | `CoreSmaParameter` | `Period` (int, default `10`, range `[1, 1000]`) | `false` | `"Main"`, `"Signal"` |
| `UltimateOscillator` | `24` | `CoreUltimateOscillatorParameter` | `Period1` (int, default `7`), `Period2` (int, default `14`), `Period3` (int, default `28`) | `false` | `"Main"` |
| `RSI` | `34` | `CoreRsiParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `MACD` | `35` | `CoreMacdParameter` | `ShortPeriod` (int, default `12`, range `[1, 1000]`), `LongPeriod` (int, default `26`, range `[1, 1000]`), `SignalPeriod` (int, default `9`, range `[1, 1000]`) | `false` | `"Main"`, `"MacdLine"`, `"Signal"`, `"Histogram"` |
| `AwesomeOscillator` | `59` | `CoreAwesomeOscillatorParameter` | `FastPeriod` (int, default `5`), `SlowPeriod` (int, default `34`) | `false` | `"Main"` |
| `AccelerationOscillator` | `60` | `CoreAcceleratorOscillatorParameter` | `FastPeriod` (int, default `5`), `SlowPeriod` (int, default `34`), `SmoothPeriod` (int, default `5`) | `false` | `"Main"` |
| `CCI` | `61` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `ROC` | `62` | `CoreRocParameter` | `Period` (int, default `12`, range `[1, 1000]`), `DisplayStyle` (RocDisplayStyle, default `0 (Line)`) | `false` | `"Main"` |
| `RCI` | `63` | `CoreSmaParameter` | `Period` (int, default `9`, range `[1, 1000]`) | `false` | `"Main"` |
| `Stoch` | `64` | `CoreStochasticParameter` | `KPeriod` (int, default `14`, range `[1, 1000]`), `DPeriod` (int, default `3`, range `[1, 1000]`), `Smooth` (int, default `3`, range `[1, 1000]`) | `false` | `"Main"`, `"Slow %K"`, `"Slow %D"`, `"PercentK"`, `"PercentD"` |
| `WilliamsR` | `88` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `ThreeLineBreakSignal` | `93` | `(none)` | (no parameters) | `false` | `"Main"`, `"Histogram"` |
| `DarvasBoxSignal` | `94` | `CoreDarvasBoxSignalParameter` | `HighPeriod` (int, default `20`), `ConfirmationPeriod` (int, default `3`) | `false` | `"Main"` |
| `VortexSignal` | `95` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `ElderForceIndex` | `96` | `CoreSmaParameter` | `Period` (int, default `13`, range `[1, 1000]`) | `false` | `"Main"` |
| `CMO` | `98` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `ElderImpulse` | `99` | `CoreElderImpulseParameter` | `EmaPeriod` (int, default `13`), `MacdFastPeriod` (int, default `12`), `MacdSlowPeriod` (int, default `26`), `MacdSignalPeriod` (int, default `9`) | `false` | `"Main"` |
| `ConnorsRsi` | `104` | `CoreConnorsRsiParameter` | `RsiPeriod` (int, default `3`), `StreakPeriod` (int, default `2`), `PercentRankPeriod` (int, default `100`) | `false` | `"Main"` |
| `DeMarker` | `105` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `DPO` | `106` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `KST` | `107` | `CoreKstParameter` | `Roc1` (int, default `10`), `Roc2` (int, default `15`), `Roc3` (int, default `20`), `Roc4` (int, default `30`), `Sma1` (int, default `10`), `Sma2` (int, default `10`), `Sma3` (int, default `10`), `Sma4` (int, default `15`) | `false` | `"Main"` |
| `PPO` | `108` | `CorePpoParameter` | `FastPeriod` (int, default `12`), `SlowPeriod` (int, default `26`) | `false` | `"Main"` |
| `PriceOscillator` | `109` | `(none)` | (no parameters) | `false` | `"Main"` |
| `RVI` | `110` | `CoreSmaParameter` | `Period` (int, default `10`, range `[1, 1000]`) | `false` | `"Main"`, `"SignalLine"` |
| `PsychologicalLine` | `125` | `CoreSmaParameter` | `Period` (int, default `12`, range `[1, 1000]`) | `false` | `"Main"` |
| `MaDeviationRate` | `126` | `CoreSmaParameter` | `Period` (int, default `25`, range `[1, 1000]`) | `false` | `"Main"` |
| `PrimeNumberOscillator` | `142` | `CorePrimeNumberOscillatorParameter` | `ScaleMultiplier` (decimal, default `10.0`, range `[1, 1000]`), `ConsecutiveExtremaPeriods` (int, default `2`, range `[1, 10]`), `LookbackPeriod` (int, default `5`, range `[2, 50]`), `Tolerance` (decimal, default `0.0`, range `[0, 10]`) | `false` | `"Main"`, `"BuySignals"`, `"SellSignals"` |
| `AdaptiveRSI` | `149` | `CoreAdaptiveRsiParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `5`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`) | `false` | `"Main"`, `"DominantPeriod"` |
| `SSACycle` | `159` | `CoreSSACycleParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `EmbeddingDimension` (int, default `20`, range `[2, 256]`), `DeltaPair` (double, default `0.25`, range `[0.01, 0.5]`) | `false` | `"Main"`, `"Cycle"`, `"InPhase"`, `"Quadrature"`, `"Phase"`, `"DominantPeriod"` |
| `SSAEntropy` | `160` | `CoreSSAEntropyParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `EmbeddingDimension` (int, default `20`, range `[2, 256]`), `DetrendMode` (SsaDetrendMode, default `0 (LeastSquaresLinear)`) | `false` | `"Main"` |
| `SSASqueeze` | `161` | `CoreSSASqueezeParameter` | `WindowSize` (int, default `64`, range `[4, 2048]`), `EmbeddingDimension` (int, default `20`, range `[2, 512]`), `NumComponents` (int, default `2`, range `[1, 100]`), `SsaMultiplier` (decimal, default `2.0`, range `[0.1, 10]`), `AtrPeriod` (int, default `20`, range `[2, 100]`), `AtrMultiplier` (decimal, default `1.5`, range `[0.1, 10]`), `MomentumPeriod` (int, default `12`, range `[2, 50]`), `SqueezeThreshold` (decimal, default `1.0`, range `[0.1, 5]`) | `false` | `"Main"`, `"Momentum"`, `"SqueezeStatus"`, `"SqueezeRatio"` |
| `SSASNR` | `162` | `CoreSSASNRParameter` | `WindowSize` (int, default `64`, range `[4, 2048]`), `EmbeddingDimension` (int, default `20`, range `[2, 512]`), `NumComponents` (int, default `2`, range `[1, 100]`), `ThresholdHigh` (decimal, default `10.0`, range `[-20, 40]`), `ThresholdLow` (decimal, default `3.0`, range `[-20, 40]`) | `false` | `"Main"`, `"SNR"`, `"SNR_dB"`, `"SignalPurity"`, `"ThresholdHigh"`, `"ThresholdHighSeries"`, `"ThresholdLow"`, `"ThresholdLowSeries"` |
| `SSAAnomaly` | `163` | `CoreSSAAnomalyParameter` | `WindowSize` (int, default `40`, range `[4, 2048]`), `EmbeddingDimension` (int, default `15`, range `[2, 512]`), `NumComponents` (int, default `2`, range `[1, 100]`), `AutoRank` (bool, default `true`), `DetrendMethod` (SsaDetrendMode, default `0 (LeastSquaresLinear)`), `PriceSource` (PriceType, default `0 (Close)`), `EnterThreshold` (decimal, default `2.0`, range `[0.1, 10]`), `ExitThreshold` (decimal, default `1.0`, range `[0.1, 5]`), `CoolDownPeriod` (int, default `3`, range `[1, 20]`), `MinDuration` (int, default `2`, range `[1, 20]`) | `false` | `"Main"`, `"ZScore"`, `"AnomalyState"`, `"AnomalyStateSeries"`, `"EnterThreshold"`, `"EnterThresholdSeries"`, `"ExitThreshold"`, `"ExitThresholdSeries"` |
| `EfficiencyRatio` | `177` | `CoreEfficiencyRatioParameter` | `Period` (int, default `10`) | `false` | `"Main"` |
| `ClothoidOscillator` | `180` | `CoreClothoidOscillatorParameter` | `Period` (int, default `14`, range `[4, 200]`), `DecayAlpha` (double, default `1.5`, range `[0, 5]`), `SignalPeriod` (int, default `9`, range `[1, 50]`), `PriceSource` (PriceType, default `0 (Close)`) | `false` | `"Main"`, `"Signal"`, `"Histogram"`, `"RawCurvatureRate"` |
| `Ratiocator` | `185` | `CoreRatiocatorParameter` | `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`), `CalculationMode` (RatiocatorCalculationMode, default `2 (RollingRatio)`), `LookbackPeriod` (int, default `20`, range `[1, 1000]`), `SmoothingPeriod` (int, default `10`, range `[0, 1000]`) | `false` | `"Main"`, `"Signal"` |

### Category: Other

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `FFTCycle` | `25` | `CoreFFTCycleParameter` | `WindowSize` (int, default `64`, range `[4, 512]`) | `false` | `"Main"`, `"CycleStrength"`, `"Oscillator"` |
| `Correlation` | `30` | `CoreCorrelationParameter` | `Period` (int, default `20`, range `[2, 1000]`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`), `CalculationMode` (CorrelationCalculationMode, default `0 (PriceLevel)`) | `false` | `"Main"` |
| `LinearRegression` | `31` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `true` | `"Main"` |
| `RegressionSlope` | `32` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `WilderSmoothing` | `66` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `true` | `"Main"` |
| `FourierTransform` | `69` | `CoreFourierTransformParameter` | `TargetPeriod` (int, default `20`, range `[2, 500]`) | `false` | `"Main"` |
| `HilbertTransform` | `70` | `CoreHilbertTransformParameter` | `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `6`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`), `SmoothBeta` (decimal, default `0.1`, range `[0.01, 1]`), `DeltaLimit` (decimal, default `3.0`, range `[0.5, 20]`) | `false` | `"Main"`, `"InPhase"`, `"Quadrature"` |
| `CoppockCurve` | `100` | `CoreCoppockCurveParameter` | `LongRocPeriod` (int, default `14`), `ShortRocPeriod` (int, default `11`), `WmaPeriod` (int, default `10`) | `false` | `"Main"` |
| `SchaffTrendCycle` | `103` | `CoreSchaffTrendCycleParameter` | `CyclePeriod` (int, default `10`), `ShortPeriod` (int, default `23`), `LongPeriod` (int, default `50`), `SmoothPeriod` (int, default `3`) | `false` | `"Main"` |
| `ZScore` | `116` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `KylesLambda` | `117` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `StructuralDtw` | `139` | `CoreStructuralDtwParameter` | `Period` (int, default `14`), `Lag` (int, default `14`), `WarpingRadius` (int, default `3`) | `false` | `"Main"` |
| `IFFTInstantaneousPhase` | `146` | `CoreIfftInstantaneousPhaseParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `LeadSineShiftDegrees` (double, default `45`, range `[0, 180]`) | `false` | `"Main"`, `"SineWave"`, `"LeadSine"`, `"PhaseDelta"`, `"LocalPeriod"`, `"PhaseStability"` |
| `HiddenMarkovModel` | `155` | `CoreHmmParameter` | `States` (int, default `2`), `Period` (int, default `100`), `MaxIterations` (int, default `30`), `Tolerance` (double, default `0.0001`) | `false` | `"Main"` |
| `FrechetOscillator` | `156` | `CoreFrechetOscillatorParameter` | `Period` (int, default `20`), `Lag` (int, default `10`), `PriceSource` (PriceType, default `0 (Close)`) | `false` | `"Main"` |
| `HilbertSine` | `164` | `CoreHilbertSineParameter` | `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `6`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`), `SmoothBeta` (decimal, default `0.1`, range `[0.01, 1]`), `DeltaLimit` (decimal, default `3.0`, range `[0.5, 20]`) | `false` | `"Main"`, `"Sine"`, `"LeadSine"` |
| `HilbertTrendMode` | `166` | `CoreHilbertTrendModeParameter` | `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `6`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`), `SmoothBeta` (decimal, default `0.1`, range `[0.01, 1]`), `DeltaLimit` (decimal, default `3.0`, range `[0.5, 20]`), `StabilityWindow` (int, default `10`, range `[2, 50]`), `StabilityThreshold` (double, default `15`, range `[1, 45]`) | `false` | `"Main"` |
| `ARIMA` | `167` | `CoreArimaParameter` | `P` (int, default `1`), `D` (int, default `1`), `Q` (int, default `1`), `Period` (int, default `30`), `PriceSource` (PriceType, default `0 (Close)`) | `true` | `"Main"` |
| `HoughTrendStrength` | `168` | `CoreHoughTrendStrengthParameter` | `Lookback` (int, default `100`), `PivotWindow` (int, default `3`), `VoteThreshold` (int, default `3`), `MaxLines` (int, default `5`), `Normalization` (HoughNormalizationMode, default `3 (MinMax)`) | `false` | `"Main"` |
| `HoughTrendAngle` | `169` | `CoreHoughTrendAngleParameter` | `Lookback` (int, default `100`), `PivotWindow` (int, default `3`), `VoteThreshold` (int, default `3`), `MaxLines` (int, default `5`), `Normalization` (HoughNormalizationMode, default `3 (MinMax)`) | `false` | `"Main"` |
| `Price` | `170` | `(none)` | (no parameters) | `true` | `"Main"` |
| `PolarPhase` | `174` | `CorePolarPhaseParameter` | `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `6`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`), `SmoothBeta` (decimal, default `0.1`, range `[0.01, 1]`), `DeltaLimit` (decimal, default `3.0`, range `[0.5, 20]`) | `false` | `"Main"`, `"NormalizedInPhase"`, `"NormalizedQuadrature"`, `"PhaseStability"` |
| `PolarCycleAngularFrequency` | `175` | `CorePolarCycleAngularFrequencyParameter` | `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `6`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`), `SmoothBeta` (decimal, default `0.1`, range `[0.01, 1]`), `DeltaLimit` (decimal, default `3.0`, range `[0.5, 20]`) | `false` | `"Main"`, `"PhaseAngularVelocity"`, `"PhaseFrequencyError"` |
| `Autocorrelation` | `176` | `CoreAutocorrelationParameter` | `Period` (int, default `30`, range `[10, 500]`), `WindowSize` (int, default `30`), `Lag` (int, default `0`, range `[0, 200]`), `PriceSource` (PriceType, default `0 (Close)`), `CalculationMode` (CorrelationCalculationMode, default `0 (PriceLevel)`), `MinLag` (int, default `5`, range `[2, 50]`), `MaxLag` (int, default `50`, range `[5, 200]`), `Threshold` (double, default `0.3`, range `[0, 1]`), `MinProminence` (double, default `0.01`, range `[0, 0.5]`), `SmoothingPeriod` (int, default `5`, range `[1, 50]`), `SelectionMode` (AutocorrelationPeakSelectionMode, default `0 (MaxCorrelation)`), `FallbackPeriod` (int, default `20`, range `[2, 200]`), `DefaultPeriod` (int, default `20`), `FallbackPolicy` (AutocorrelationFallbackPolicy, default `0 (HoldPrevious)`), `MaxHoldBars` (int, default `5`, range `[1, 50]`) | `false` | `"Main"`, `"Autocorrelation"`, `"Correlation"`, `"PeakCorrelation"`, `"Period"`, `"DominantPeriod"`, `"RawPeriod"` |
| `CrossCorrelation` | `182` | `CoreCrossCorrelationParameter` | `PriceSource` (PriceType, default `0 (Close)`), `Period` (int, default `20`, range `[2, 1000]`), `Lag` (int, default `0`, range `[-100, 100]`), `EnableDynamicLag` (bool, default `true`), `MinLag` (int, default `-10`, range `[-100, 100]`), `MaxLag` (int, default `10`, range `[-100, 100]`), `Threshold` (double, default `0.3`, range `[0, 1]`), `LagSmoothingPeriod` (int, default `3`, range `[1, 20]`), `PeakSelectionMode` (CrossCorrelationPeakSelectionMode, default `0 (MaxCorrelation)`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`), `CalculationMode` (CorrelationCalculationMode, default `1 (LogReturn)`) | `false` | `"Main"`, `"OptimalLag"` |
| `RollingBeta` | `183` | `CoreRollingBetaParameter` | `Period` (int, default `60`, range `[5, 1000]`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`), `CalculationMode` (BetaCalculationMode, default `0 (SimpleReturn)`) | `false` | `"Main"`, `"RollingCorrelation"` |
| `RollingAlpha` | `184` | `CoreRollingAlphaParameter` | `Period` (int, default `60`, range `[5, 1000]`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`), `CalculationMode` (BetaCalculationMode, default `0 (SimpleReturn)`), `Annualize` (bool, default `true`), `AnnualizationFactor` (int, default `252`, range `[1, 365]`) | `false` | `"Main"` |
| `RollingRSquared` | `186` | `CoreRollingRSquaredParameter` | `Period` (int, default `60`, range `[5, 1000]`), `PriceSource` (PriceType, default `0 (Close)`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`), `CalculationMode` (BetaCalculationMode, default `0 (SimpleReturn)`) | `false` | `"Main"` |
| `CointegrationSpread` | `187` | `CoreCointegrationSpreadParameter` | `Period` (int, default `60`, range `[5, 1000]`), `ZScorePeriod` (int, default `20`, range `[2, 500]`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`), `PriceMode` (CointegrationPriceMode, default `0 (PriceLevel)`) | `false` | `"Main"`, `"ZScore"`, `"HedgeRatio"`, `"Intercept"`, `"ADFStatistic"`, `"HalfLife"` |
| `PairRatio` | `188` | `CorePairRatioParameter` | `PriceSource` (PriceType, default `0 (Close)`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`) | `false` | `"Main"` |
| `PairSpread` | `189` | `CorePairSpreadParameter` | `PriceSource` (PriceType, default `0 (Close)`), `ComparisonSymbol` (string, default `""`), `ComparisonPriceSource` (PriceType, default `0 (Close)`) | `false` | `"Main"` |

### Category: Trend

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `SMA` | `0` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `EMA` | `1` | `CoreEmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `WMA` | `2` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `HMA` | `3` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `TMA` | `4` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `true` | `"Main"` |
| `DEMA` | `5` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `TEMA` | `6` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `VWMA` | `7` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `VAMA` | `8` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `true` | `"Main"` |
| `SMMA` | `9` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `LRMA` | `10` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `true` | `"Main"` |
| `ALMA` | `11` | `CoreAlmaParameter` | `Period` (int, default `9`), `Offset` (double, default `0.85`), `Sigma` (double, default `6`) | `true` | `"Main"` |
| `ZLEMA` | `12` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `LSMA` | `13` | `CoreSmaParameter` | `Period` (int, default `25`, range `[1, 1000]`) | `true` | `"Main"` |
| `GMA` | `14` | `(none)` | (no parameters) | `true` | `"EMA3"`, `"EMA5"`, `"EMA8"`, `"EMA10"`, `"EMA12"`, `"EMA15"`, `"EMA30"`, `"EMA35"`, `"EMA40"`, `"EMA45"`, `"EMA50"`, `"EMA60"`, `"Main"` |
| `TRIX` | `15` | `CoreTrixParameter` | `Period` (int, default `15`, range `[1, 10000]`) | `false` | `"Main"` |
| `ParabolicSAR` | `44` | `CoreParabolicSarParameter` | `AccelerationStart` (decimal, default `0.02`), `AccelerationStep` (decimal, default `0.02`), `AccelerationMax` (decimal, default `0.2`) | `true` | `"Main"`, `"IsUpTrend"` |
| `ADX` | `45` | `CoreAdxParameter` | `Period` (int, default `14`) | `false` | `"Main"`, `"PlusDI"`, `"MinusDI"` |
| `ADXR` | `46` | `CoreAdxrParameter` | `Period` (int, default `14`), `AdxrPeriod` (int, default `14`) | `false` | `"Main"` |
| `DMI` | `47` | `CoreDmiParameter` | `Period` (int, default `14`) | `false` | `"Main"`, `"PlusDI"`, `"MinusDI"` |
| `Aroon` | `48` | `CoreAroonParameter` | `Period` (int, default `25`) | `false` | `"Main"`, `"AroonUp"`, `"AroonDown"` |
| `SuperTrend` | `49` | `CoreSuperTrendParameter` | `Period` (int, default `10`), `Multiplier` (decimal, default `3.0`) | `true` | `"Main"` |
| `Dma` | `51` | `CoreDmaParameter` | `Period` (int, default `20`), `Displacement` (int, default `5`) | `true` | `"Main"` |
| `KAMA` | `52` | `CoreKamaParameter` | `Period` (int, default `10`), `Fast` (int, default `2`), `Slow` (int, default `30`) | `true` | `"Main"` |
| `JMA` | `54` | `CoreJmaParameter` | `Period` (int, default `7`), `Phase` (double, default `0`) | `true` | `"Main"` |
| `Ichimoku` | `86` | `CoreIchimokuParameter` | `TenkanSample` (int, default `9`, range `[1, 10000]`), `KijunSample` (int, default `26`, range `[1, 10000]`), `SenkouBSample` (int, default `52`, range `[1, 10000]`), `Offset` (int, default `26`, range `[0, 100]`) | `true` | `"Main"`, `"TenkanSen"`, `"KijunSen"`, `"SenkouSpanA"`, `"SenkouSpanB"`, `"ChikouSpan"` |
| `ZigZag` | `87` | `CoreZigZagParameter` | `Threshold` (decimal, default `5.0`) | `true` | `"Main"` |
| `ElderRay` | `97` | `CoreSmaParameter` | `Period` (int, default `13`, range `[1, 1000]`) | `false` | `"Main"`, `"BullPower"`, `"BearPower"` |
| `Vortex` | `102` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"`, `"VIPlus"`, `"VIMinus"` |
| `KalmanFilter` | `111` | `CoreKalmanFilterParameter` | `Q` (decimal, default `0.01`), `R` (decimal, default `0.1`) | `true` | `"Main"` |
| `FRAMA` | `112` | `CoreFRAMAParameter` | `Period` (int, default `16`) | `true` | `"Main"` |
| `MtfLaggedEma` | `119` | `CoreMtfLaggedEmaParameter` | `Period` (int, default `20`), `TimeFrameMultiplier` (int, default `4`), `Lag` (int, default `1`) | `true` | `"Main"` |
| `Envelope` | `124` | `CoreEnvelopeParameter` | `Period` (int, default `20`), `Deviation` (decimal, default `0.025`) | `true` | `"Upper"`, `"Main"`, `"Lower"` |
| `MESA` | `136` | `CoreMesaParameter` | `FastLimit` (decimal, default `0.5`), `SlowLimit` (decimal, default `0.05`) | `true` | `"Main"`, `"Fama"` |
| `QStick` | `137` | `CoreSmaParameter` | `Period` (int, default `8`, range `[1, 1000]`) | `false` | `"Main"` |
| `MovingAverageCross` | `141` | `CoreMovingAverageCrossParameter` | `ShortPeriod` (int, default `10`, range `[1, 1000]`), `LongPeriod` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"`, `"ShortMA"`, `"LongMA"` |
| `BSMA` | `144` | `CoreBSmaParameter` | `Period` (int, default `14`), `Degree` (int, default `3`), `Offset` (double, default `0.85`), `Sigma` (double, default `6`) | `true` | `"Main"` |
| `FFTTrendFilter` | `145` | `CoreFFTTrendFilterParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `NumHarmonics` (int, default `4`, range `[1, 256]`) | `true` | `"Main"` |
| `IFFTInstantaneousAmplitude` | `147` | `CoreIfftInstantaneousAmplitudeParameter` | `WindowSize` (int, default `64`, range `[4, 512]`) | `true` | `"Main"` |
| `IFFTBandPassFilter` | `148` | `CoreIfftBandPassFilterParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `BandWidthBins` (int, default `2`, range `[0, 128]`) | `false` | `"Main"` |
| `Highest` | `150` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `Lowest` | `151` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"Main"` |
| `SSA` | `157` | `CoreSSAParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `EmbeddingDimension` (int, default `20`, range `[2, 256]`), `NumComponents` (int, default `2`, range `[1, 100]`) | `true` | `"Main"` |
| `HilbertTrendline` | `165` | `CoreHilbertTrendlineParameter` | `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `6`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`), `SmoothBeta` (decimal, default `0.1`, range `[0.01, 1]`), `DeltaLimit` (decimal, default `3.0`, range `[0.5, 20]`) | `true` | `"Main"` |
| `VIDYA` | `171` | `CoreVidyaParameter` | `SmoothPeriod` (int, default `9`), `CmoPeriod` (int, default `9`) | `true` | `"Main"` |
| `ClothoidMovingAverage` | `181` | `CoreClothoidMovingAverageParameter` | `Period` (int, default `20`, range `[2, 500]`), `Offset` (double, default `0.85`, range `[0, 1]`), `Sigma` (double, default `6`, range `[0.5, 20]`), `PriceSource` (PriceType, default `0 (Close)`) | `true` | `"Main"`, `"EffectiveLag"`, `"EffectiveSampleSize"` |

### Category: Volatility

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `VaR` | `26` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `CVaR` | `27` | `CoreCVarParameter` | `Period` (int, default `20`), `ConfidenceLevel` (double, default `0.95`) | `false` | `"Main"` |
| `Variance` | `28` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `StandardDeviation` | `29` | `CoreStandardDeviationParameter` | `Period` (int, default `20`, range `[1, 10000]`), `IsPopulation` (bool, default `false`) | `false` | `"Main"` |
| `BB` | `33` | `CoreBollingerBandsParameter` | `Period` (int, default `20`, range `[1, 1000]`), `StdDevMultiplier` (decimal, default `2.0`, range `[0.1, 10]`) | `true` | `"Main"`, `"Upper"`, `"Lower"`, `"MiddleBand"`, `"UpperBand"`, `"LowerBand"` |
| `ATR` | `36` | `CoreAtrParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `Keltner` | `50` | `(none)` | (no parameters) | `true` | `"Main"`, `"UpperBand"`, `"LowerBand"` |
| `ChaikinVolatility` | `55` | `CoreChaikinVolatilityParameter` | `Period` (int, default `10`), `RocPeriod` (int, default `10`) | `false` | `"Main"` |
| `YangZhangVolatility` | `56` | `CoreSmaParameter` | `Period` (int, default `10`, range `[1, 1000]`) | `false` | `"Main"` |
| `ChoppinessIndex` | `57` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `WilliamsVixFix` | `58` | `CoreSmaParameter` | `Period` (int, default `22`, range `[1, 1000]`) | `false` | `"Main"` |
| `ExpectedMove` | `73` | `CoreExpectedMoveParameter` | `Period` (int, default `14`), `Multiplier` (decimal, default `1.0`) | `false` | `"Main"` |
| `Egarch` | `76` | `CoreEgarchParameter` | `Period` (int, default `250`, range `[50, 2000]`), `RefitInterval` (int, default `20`, range `[1, 250]`), `P` (int, default `1`, range `[1, 3]`), `Q` (int, default `1`, range `[1, 3]`), `MaxIterations` (int, default `1000`, range `[50, 20000]`), `MaxSigmaRatio` (decimal, default `50`, range `[2, 100000]`), `MinSigmaRatio` (decimal, default `0.01`, range `[0, 0.5]`) | `false` | `"Main"` |
| `HistoricalVolatility` | `82` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `MassIndex` | `101` | `CoreMassIndexParameter` | `Period` (int, default `25`), `EmaPeriod` (int, default `9`) | `false` | `"Main"` |
| `GarmanKlassVolatility` | `115` | `(none)` | (no parameters) | `false` | `"Main"` |
| `Donchian` | `120` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `true` | `"UpperBand"`, `"Main"`, `"LowerBand"`, `"Position"` |
| `NATR` | `121` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `RviVolatility` | `122` | `CoreSmaParameter` | `Period` (int, default `10`, range `[1, 1000]`) | `false` | `"Main"` |
| `UlcerIndex` | `123` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `PrimeNumberBands` | `143` | `CorePrimeNumberBandsParameter` | `Period` (int, default `8`, range `[1, 1000]`), `ScaleMultiplier` (decimal, default `10.0`, range `[1, 1000]`) | `true` | `"Main"`, `"Upper"`, `"Lower"`, `"MiddleBand"`, `"UpperBand"`, `"LowerBand"` |
| `TrueHigh` | `152` | `(none)` | (no parameters) | `true` | `"Main"` |
| `TrueLow` | `153` | `(none)` | (no parameters) | `true` | `"Main"` |
| `TrueRange` | `154` | `(none)` | (no parameters) | `false` | `"Main"` |
| `SSAResidualBand` | `158` | `CoreSSAResidualBandParameter` | `WindowSize` (int, default `64`, range `[4, 512]`), `EmbeddingDimension` (int, default `20`, range `[2, 256]`), `NumComponents` (int, default `2`, range `[1, 100]`), `Multiplier` (decimal, default `2.0`, range `[0.1, 10]`), `SigmaMode` (SsaResidualBandSigmaMode, default `0 (ExactDiagonalAverage)`) | `true` | `"Main"`, `"CenterBand"`, `"UpperBand"`, `"LowerBand"`, `"BandWidth"`, `"Upper"`, `"Lower"`, `"Center"` |
| `PolarAmplitudeRatio` | `173` | `CorePolarAmplitudeRatioParameter` | `AtrPeriod` (int, default `14`, range `[2, 200]`), `DefaultPeriod` (int, default `14`, range `[2, 100]`), `MinPeriod` (int, default `6`, range `[2, 50]`), `MaxPeriod` (int, default `50`, range `[10, 200]`), `SmoothBeta` (decimal, default `0.1`, range `[0.01, 1]`), `DeltaLimit` (decimal, default `3.0`, range `[0.5, 20]`) | `false` | `"Main"` |
| `FDI` | `178` | `CoreFDIParameter` | `Period` (int, default `30`), `SmoothingPeriod` (int, default `0`) | `false` | `"Main"` |

### Category: Volume

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `ADL` | `20` | `(none)` | (no parameters) | `false` | `"Main"` |
| `VPT` | `21` | `(none)` | (no parameters) | `false` | `"Main"` |
| `PVI` | `22` | `(none)` | (no parameters) | `false` | `"Main"` |
| `OBV` | `37` | `(none)` | (no parameters) | `false` | `"Main"` |
| `VWAP` | `38` | `(none)` | (no parameters) | `true` | `"Main"` |
| `CMF` | `39` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `ChaikinOscillator` | `40` | `CoreChaikinOscillatorParameter` | `FastPeriod` (int, default `3`), `SlowPeriod` (int, default `10`) | `false` | `"Main"` |
| `EOM` | `41` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `BOP` | `42` | `(none)` | (no parameters) | `false` | `"Main"` |
| `AmihudIlliquidity` | `43` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `MFI` | `65` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `ForceIndex` | `89` | `(none)` | (no parameters) | `false` | `"Main"` |
| `NegativeVolumeIndex` | `90` | `(none)` | (no parameters) | `false` | `"Main"` |
| `AnchoredVWAP` | `114` | `(none)` | (no parameters) | `true` | `"Main"` |
| `PVT` | `118` | `(none)` | (no parameters) | `false` | `"Main"` |
| `VolumeMA` | `127` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 1000]`) | `false` | `"Main"` |
| `VolumeROC` | `128` | `CoreSmaParameter` | `Period` (int, default `14`, range `[1, 1000]`) | `false` | `"Main"` |
| `Volume` | `132` | `(none)` | (no parameters) | `false` | `"Main"` |
| `RollingVWAP` | `172` | `CoreRollingVwapParameter` | `Period` (int, default `20`, range `[1, 10000]`) | `true` | `"Main"` |

---

## 3. Enum Reference for Parameter Properties

Enums are serialized as integers. The member name in parentheses is for readability only.

- **`AutocorrelationFallbackPolicy`**: `0` = `HoldPrevious`, `1` = `StaticDefault`
- **`AutocorrelationPeakSelectionMode`**: `0` = `MaxCorrelation`, `1` = `FirstPeak`
- **`BetaCalculationMode`**: `0` = `SimpleReturn`, `1` = `LogReturn`
- **`CointegrationPriceMode`**: `0` = `PriceLevel`, `1` = `LogPrice`
- **`CorrelationCalculationMode`**: `0` = `PriceLevel`, `1` = `LogReturn`
- **`CrossCorrelationPeakSelectionMode`**: `0` = `MaxCorrelation`, `1` = `MaxAbsoluteCorrelation`
- **`DisplaySide`**: `0` = `Left`, `1` = `Right`, `2` = `Both`
- **`HoughNormalizationMode`**: `0` = `Raw`, `1` = `Relative`, `2` = `Log`, `3` = `MinMax`, `4` = `ZScore`
- **`PriceType`**: `0` = `Close`, `1` = `Open`, `2` = `High`, `3` = `Low`, `4` = `Median`, `5` = `Midpoint`, `6` = `Typical`, `7` = `Weighted`, `8` = `Average`, `9` = `HeikinAshiOpen`, `10` = `HeikinAshiHigh`, `11` = `HeikinAshiLow`, `12` = `HeikinAshiClose`, `13` = `TrueHigh`, `14` = `TrueLow`
- **`RatiocatorCalculationMode`**: `0` = `RawRatio`, `1` = `NormalizedRatio`, `2` = `RollingRatio`
- **`RocDisplayStyle`**: `0` = `Line`, `1` = `Histogram`
- **`SsaDetrendMode`**: `0` = `LeastSquaresLinear`, `1` = `EndpointLinear`, `2` = `None`
- **`SsaResidualBandSigmaMode`**: `0` = `ExactDiagonalAverage`, `1` = `FastEigenEnergy`
- **`VolumeDistributionMode`**: `0` = `Proportional`, `1` = `Full`
<!-- END GENERATED CATALOG -->

---

## 4. Minimal Example of Polymorphic Instantiation

```json
{
  "Id": "12345678-1234-1234-1234-123456789abc",
  "TypeEnum": 35,
  "DisplayName": "MACD(12, 26, 9)",
  "IsEnabled": true,
  "IsOverlay": false,
  "OverlayPanelId": "SubPanel_Macd",
  "Color": "#FF26A69A",
  "Thickness": 1.5,
  "Style": 0,
  "Offset": 0,
  "PriceSource": 0,
  "ParameterObject": {
    "$type": "CoreMacdParameter",
    "ShortPeriod": 12,
    "LongPeriod": 26,
    "SignalPeriod": 9
  }
}
```
