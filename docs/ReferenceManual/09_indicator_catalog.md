# 09. Indicator Catalog & Polymorphic Parameter Reference

## 1. Overview & Polymorphism Rules

In StockAnalyzer, technical indicators are configured via `CoreIndicatorSettings`. Because different indicators have completely different mathematical parameters (e.g., SMA needs only `Period`, while MACD needs `ShortPeriod`, `LongPeriod`, and `SignalPeriod`), parameter objects inherit from `CoreIndicatorParameterBase` and are serialized polymorphically.

### The `"$type"` Invariant
`WorkspacePolymorphicResolver` discovers all non-abstract subclasses of `CoreIndicatorParameterBase`. When serializing or deserializing a JSON object containing `ParameterObject` or `Parameters`:
- The JSON object **MUST** contain a `"$type"` property matching the exact C# parameter class name (e.g. `"$type": "CoreSmaParameter"`).
- Omitting `"$type"` causes a deserialization exception or falls back to an empty base object, ignoring user parameters.

---

## 2. Core Indicator Types & Parameter Catalog

### Trend & Moving Averages

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `SMA` | `0` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `EMA` | `1` | `CoreEmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `WMA` | `2` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `HMA` | `3` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `TMA` | `4` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `DEMA` | `5` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `TEMA` | `6` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `VWMA` | `7` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `VAMA` | `8` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `SMMA` | `9` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `LRMA` | `10` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `ALMA` | `11` | `CoreAlmaParameter` | `Period` (int, `9`), `Offset` (double, `0.85`), `Sigma` (double, `6.0`) | `true` | `"Main"` |
| `ZLEMA` | `12` | `CoreSmaParameter` | `Period` (int, default `20`, range `[1, 500]`) | `true` | `"Main"` |
| `LSMA` | `13` | `CoreSmaParameter` | `Period` (int, default `25`, range `[1, 500]`) | `true` | `"Main"` |
| `KAMA` | `52` | `CoreKamaParameter` | `Period` (int, `10`), `Fast` (int, `2`), `Slow` (int, `30`) | `true` | `"Main"` |
| `FRAMA` | `112` | `CoreSmaParameter` | `Period` (int, `16`) | `true` | `"Main"` |
| `MESA` | `136` | `CoreMesaParameter` | `FastLimit` (double, `0.5`), `SlowLimit` (double, `0.05`) | `true` | `"Main"`, `"Mama"`, `"Fama"` |
| `SuperTrend` | `49` | `CoreSuperTrendParameter` | `Period` (int, `10`), `Multiplier` (double, `3.0`) | `true` | `"Main"` |
| `ParabolicSAR` | `44` | `CoreParabolicSarParameter` | `Acceleration` (double, `0.02`), `Maximum` (double, `0.2`) | `true` | `"Main"` |
| `Ichimoku` | `86` | `CoreIchimokuParameter` | `TenkanPeriod` (int, `9`), `KijunPeriod` (int, `26`), `SenkouBPeriod` (int, `52`) | `true` | `"Main"`, `"Kijun"`, `"Chikou"`, `"SenkouA"`, `"SenkouB"` |

---

### Momentum & Oscillators

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `RSI` | `34` | `CoreRsiParameter` | `Period` (int, `14`, range `[2, 100]`) | `false` | `"Main"` |
| `MACD` | `35` | `CoreMacdParameter` | `ShortPeriod` (int, `12`), `LongPeriod` (int, `26`), `SignalPeriod` (int, `9`) | `false` | `"Main"`, `"Signal"`, `"Histogram"` |
| `Stoch` | `64` | `CoreStochasticParameter` | `KPeriod` (int, `14`), `DPeriod` (int, `3`), `Slowing` (int, `3`) | `false` | `"Main"`, `"K"`, `"D"` |
| `CCI` | `61` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `ROC` | `62` | `CoreRocParameter` | `Period` (int, `12`) | `false` | `"Main"` |
| `MFI` | `65` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `TRIX` | `15` | `CoreTrixParameter` | `Period` (int, `18`), `SignalPeriod` (int, `9`) | `false` | `"Main"`, `"Signal"` |
| `Momentum` | `16` | `CoreSmaParameter` | `Period` (int, `10`) | `false` | `"Main"` |
| `SMI` | `17` | `CoreSmiParameter` | `KPeriod` (int, `10`), `DPeriod` (int, `3`), `EmaPeriod` (int, `3`) | `false` | `"Main"`, `"Signal"` |
| `WilliamsR` | `88` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `AwesomeOscillator` | `59` | `CoreAwesomeOscillatorParameter` | `FastPeriod` (int, `5`), `SlowPeriod` (int, `34`) | `false` | `"Main"` |
| `AccelerationOscillator` | `60` | `CoreAcceleratorOscillatorParameter` | `FastPeriod` (int, `5`), `SlowPeriod` (int, `34`), `SignalPeriod` (int, `5`) | `false` | `"Main"` |
| `ChaikinOscillator` | `40` | `CoreChaikinOscillatorParameter` | `FastPeriod` (int, `3`), `SlowPeriod` (int, `10`) | `false` | `"Main"` |
| `UltimateOscillator` | `24` | `CoreUltimateOscillatorParameter` | `Period1` (int, `7`), `Period2` (int, `14`), `Period3` (int, `28`) | `false` | `"Main"` |
| `FisherTransform` | `23` | `CoreSmaParameter` | `Period` (int, `10`) | `false` | `"Main"`, `"Signal"` |
| `PPO` | `108` | `CorePpoParameter` | `FastPeriod` (int, `12`), `SlowPeriod` (int, `26`), `SignalPeriod` (int, `9`) | `false` | `"Main"`, `"Signal"` |
| `DPO` | `106` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `KST` | `107` | `CoreKstParameter` | `Roc1` (int, `10`), `Roc2` (int, `15`), `Roc3` (int, `20`), `Roc4` (int, `30`), `Sma1` (int, `10`), `Sma2` (int, `10`), `Sma3` (int, `10`), `Sma4` (int, `15`), `SignalPeriod` (int, `9`) | `false` | `"Main"`, `"Signal"` |
| `DeMarker` | `105` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `RCI` | `63` | `CoreSmaParameter` | `Period` (int, `9`) | `false` | `"Main"` |
| `SchaffTrendCycle` | `103` | `CoreSchaffTrendCycleParameter` | `CyclePeriod` (int, `10`), `FastPeriod` (int, `23`), `SlowPeriod` (int, `50`) | `false` | `"Main"` |
| `ConnorsRsi` | `104` | `CoreConnorsRsiParameter` | `RsiPeriod` (int, `3`), `StreakRsiPeriod` (int, `2`), `PercentRankPeriod` (int, `100`) | `false` | `"Main"` |

---

### Volatility & Channels

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `BB` | `33` | `CoreBollingerBandsParameter` | `Period` (int, `20`), `StdDevMultiplier` (double, `2.0`) | `true` | `"Main"`, `"Upper"`, `"Lower"` |
| `BandWidth` | `18` | `CoreBollingerBandsParameter` | `Period` (int, `20`), `StdDevMultiplier` (double, `2.0`) | `false` | `"Main"` |
| `BollingerBandsRatio` | `19` | `CoreBollingerBandsParameter` | `Period` (int, `20`), `StdDevMultiplier` (double, `2.0`) | `false` | `"Main"` |
| `ATR` | `36` | `CoreAtrParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `NATR` | `121` | `CoreAtrParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `Donchian` | `120` | `CoreSmaParameter` | `Period` (int, `20`) | `true` | `"Main"`, `"Upper"`, `"Lower"` |
| `Keltner` | `50` | `CoreKeltnerParameter` | `Period` (int, `20`), `AtrPeriod` (int, `10`), `Multiplier` (double, `2.0`) | `true` | `"Main"`, `"Upper"`, `"Lower"` |
| `Envelope` | `124` | `CoreEnvelopeParameter` | `Period` (int, `20`), `Percentage` (double, `2.5`) | `true` | `"Main"`, `"Upper"`, `"Lower"` |
| `StandardDeviation` | `29` | `CoreStandardDeviationParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `Variance` | `28` | `CoreStandardDeviationParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `HistoricalVolatility` | `82` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `ChaikinVolatility` | `55` | `CoreChaikinVolatilityParameter` | `EmaPeriod` (int, `10`), `RocPeriod` (int, `10`) | `false` | `"Main"` |
| `YangZhangVolatility` | `56` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `ChoppinessIndex` | `57` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `UlcerIndex` | `123` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"` |

---

### Volume & Liquidity

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `Volume` | `184` | `CoreIndicatorParameterBase` (null/empty) | None | `false` | `"Main"` |
| `VolumeMA` | `127` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `VolumeROC` | `128` | `CoreRocParameter` | `Period` (int, `12`) | `false` | `"Main"` |
| `OBV` | `63` | `CoreIndicatorParameterBase` (null/empty) | None | `false` | `"Main"` |
| `VWAP` | `64` | `CoreIndicatorParameterBase` (null/empty) | None | `true` | `"Main"` |
| `AnchoredVWAP` | `114` | `CoreAnchoredVwapParameter` | `AnchorDate` (DateTime, `"2024-01-01"`) | `true` | `"Main"` |
| `RollingVWAP` | `172` | `CoreRollingVwapParameter` | `Period` (int, `20`) | `true` | `"Main"` |
| `ADL` | `38` | `CoreIndicatorParameterBase` (null/empty) | None | `false` | `"Main"` |
| `CMF` | `39` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `EOM` | `41` | `CoreSmaParameter` | `Period` (int, `14`), `Divisor` (double, `10000.0`) | `false` | `"Main"` |
| `BOP` | `68` | `CoreIndicatorParameterBase` (null/empty) | None | `false` | `"Main"` |
| `AmihudIlliquidity` | `43` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `PVT` | `166` | `CoreIndicatorParameterBase` (null/empty) | None | `false` | `"Main"` |
| `VPT` | `39` | `CoreIndicatorParameterBase` (null/empty) | None | `false` | `"Main"` |
| `VolumeProfile` | `131` | `CoreVolumeProfileParameter` | `Bars` (int, `200`), `PriceStep` (decimal, `1.0`) | `true` | `"Main"`, `"Poc"`, `"Vah"`, `"Val"` |
| `TimeAtPrice` | `179` | `CoreTimeAtPriceParameter` | `Bars` (int, `200`), `PriceStep` (decimal, `1.0`) | `true` | `"Main"` |

---

### Directional & Trend Strength

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `ADX` | `45` | `CoreAdxParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `ADXR` | `46` | `CoreAdxrParameter` | `Period` (int, `14`), `Interval` (int, `14`) | `false` | `"Main"` |
| `DMI` | `47` | `CoreDmiParameter` | `Period` (int, `14`) | `false` | `"Main"`, `"PlusDi"`, `"MinusDi"` |
| `Aroon` | `48` | `CoreAroonParameter` | `Period` (int, `25`) | `false` | `"Main"`, `"Up"`, `"Down"`, `"Oscillator"` |
| `Vortex` | `102` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"`, `"PlusVi"`, `"MinusVi"` |
| `MassIndex` | `101` | `CoreSmaParameter` | `Period` (int, `25`), `EmaPeriod` (int, `9`) | `false` | `"Main"` |
| `ZigZag` | `87` | `CoreZigZagParameter` | `ThresholdPercent` (double, `5.0`) | `true` | `"Main"` |

---

### Spectral, Cycles & Decomposition

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `FFTCycle` | `25` | `CoreSmaParameter` | `WindowSize` (int, `64`) | `false` | `"Main"` |
| `IFFTInstantaneousPhase` | `146` | `CoreSmaParameter` | `WindowSize` (int, `64`) | `false` | `"Main"` |
| `IFFTInstantaneousAmplitude` | `147` | `CoreSmaParameter` | `WindowSize` (int, `64`) | `false` | `"Main"` |
| `IFFTBandPassFilter` | `148` | `CoreSmaParameter` | `WindowSize` (int, `64`), `Bandwidth` (double, `0.2`) | `false` | `"Main"` |
| `HilbertTransform` | `70` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"`, `"InPhase"`, `"Quadrature"` |
| `HilbertSine` | `164` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"`, `"LeadSine"` |
| `HilbertTrendline` | `165` | `CoreSmaParameter` | `Period` (int, `20`) | `true` | `"Main"` |
| `HilbertTrendMode` | `166` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `SSA` | `157` | `CoreSSAParameter` | `WindowLength` (int, `30`), `ComponentCount` (int, `2`) | `true` | `"Main"` |
| `SSACycle` | `159` | `CoreSSACycleParameter` | `WindowLength` (int, `30`), `PairIndex` (int, `1`) | `false` | `"Main"` |
| `SSAEntropy` | `160` | `CoreSSAEntropyParameter` | `WindowLength` (int, `30`) | `false` | `"Main"` |
| `SSASqueeze` | `161` | `CoreSSASqueezeParameter` | `WindowLength` (int, `30`) | `false` | `"Main"` |
| `SSASNR` | `162` | `CoreSSASNRParameter` | `WindowLength` (int, `30`) | `false` | `"Main"` |
| `SSAAnomaly` | `163` | `CoreSSAAnomalyParameter` | `WindowLength` (int, `30`) | `false` | `"Main"` |
| `PolarAmplitudeRatio` | `173` | `CorePolarAmplitudeRatioParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `PolarPhase` | `174` | `CorePolarPhaseParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `PolarCycleAngularFrequency` | `175` | `CorePolarCycleAngularFrequencyParameter` | `Period` (int, `20`) | `false` | `"Main"` |

---

### Multi-Asset, Pairs & Statistical

| IndicatorType | Ordinal | Parameter Class (`"$type"`) | Parameters (Name, Type, Default, Range) | IsOverlay | Output Series Names |
|---|---|---|---|---|---|
| `PairRatio` | `188` | `CorePairRatioParameter` | `ComparisonTicker` (string, `"SPY"`) | `false` | `"Main"` |
| `PairSpread` | `189` | `CorePairSpreadParameter` | `ComparisonTicker` (string, `"SPY"`) | `false` | `"Main"` |
| `RollingBeta` | `183` | `CoreRollingBetaParameter` | `ComparisonTicker` (string, `"SPY"`), `Period` (int, `60`) | `false` | `"Main"` |
| `RollingAlpha` | `184` | `CoreRollingAlphaParameter` | `ComparisonTicker` (string, `"SPY"`), `Period` (int, `60`) | `false` | `"Main"` |
| `RollingRSquared` | `186` | `CoreRollingRSquaredParameter` | `ComparisonTicker` (string, `"SPY"`), `Period` (int, `60`) | `false` | `"Main"` |
| `CrossCorrelation` | `182` | `CoreCrossCorrelationParameter` | `ComparisonTicker` (string, `"SPY"`), `Period` (int, `60`), `Lag` (int, `0`) | `false` | `"Main"` |
| `CointegrationSpread` | `187` | `CoreCointegrationSpreadParameter` | `ComparisonTicker` (string, `"SPY"`), `Period` (int, `120`) | `false` | `"Main"` |
| `Ratiocator` | `185` | `CoreRatiocatorParameter` | `ComparisonTicker` (string, `"SPY"`), `BasePeriod` (int, `20`) | `false` | `"Main"` |
| `Correlation` | `30` | `CoreCorrelationParameter` | `ComparisonTicker` (string, `"SPY"`), `Period` (int, `20`) | `false` | `"Main"` |
| `Autocorrelation` | `176` | `CoreAutocorrelationParameter` | `Period` (int, `20`), `Lag` (int, `1`) | `false` | `"Main"` |
| `EfficiencyRatio` | `177` | `CoreEfficiencyRatioParameter` | `Period` (int, `10`) | `false` | `"Main"` |
| `FDI` | `178` | `CoreSmaParameter` | `Period` (int, `30`) | `false` | `"Main"` |
| `LinearRegression` | `31` | `CoreSmaParameter` | `Period` (int, `14`) | `true` | `"Main"` |
| `RegressionSlope` | `32` | `CoreSmaParameter` | `Period` (int, `14`) | `false` | `"Main"` |
| `ZScore` | `116` | `CoreSmaParameter` | `Period` (int, `20`) | `false` | `"Main"` |
| `VaR` | `26` | `CoreSmaParameter` | `Period` (int, `20`), `ConfidenceLevel` (double, `0.95`) | `false` | `"Main"` |
| `CVaR` | `27` | `CoreCVarParameter` | `Period` (int, `20`), `ConfidenceLevel` (double, `0.95`) | `false` | `"Main"` |

---

## 3. Minimal Example of Polymorphic Instantiation

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
