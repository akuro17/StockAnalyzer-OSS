# 07. Indicator Defaults & Driver Settings

This document covers custom indicator configuration files, source indicator pipelines, and dynamic period drivers.

---

## 1. User Indicator Defaults (`user_indicator_defaults.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/IndicatorDefaults/user_indicator_defaults.json` |
| **Path Resolver** | `PathDiscovery.ResolveIndicatorDefaultsPath("user_indicator_defaults.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.IndicatorUserDefaultService` (`IIndicatorUserDefaultService`) |
| **Root Type** | `Dictionary<IndicatorType, CoreIndicatorSettings>` |

Whenever a user adds a new indicator to a chart or screener, StockAnalyzer checks this dictionary for user-customized defaults before falling back to built-in system constants.

### Invariants & Color Serialization
- Keys represent valid `IndicatorType` values (e.g. `"SimpleMovingAverage"`, `"RelativeStrengthIndex"`).
- Color values use `IndicatorColorJsonConverter` which serializes colors as 8-character ARGB Hex strings (`#AARRGGBB`).

### Reference Example
```json
{
  "SimpleMovingAverage": {
    "Id": "00000000-0000-0000-0000-000000000001",
    "Type": "SimpleMovingAverage",
    "Name": "SMA Default",
    "IsVisible": true,
    "TimeFrame": "D1",
    "PanelIndex": 0,
    "Color": "#FFFF8000",
    "Parameters": {
      "Period": 25
    }
  },
  "RelativeStrengthIndex": {
    "Id": "00000000-0000-0000-0000-000000000002",
    "Type": "RelativeStrengthIndex",
    "Name": "RSI Default",
    "IsVisible": true,
    "TimeFrame": "D1",
    "PanelIndex": 1,
    "Color": "#FF9933CC",
    "Parameters": {
      "Period": 14,
      "OverboughtLevel": 70.0,
      "OversoldLevel": 30.0
    }
  }
}
```

---

## 2. Multi-Stage Source Indicators (`source_indicators.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/SourceIndicators/source_indicators.json` |
| **Path Resolver** | `PathDiscovery.ResolveSourceIndicatorsPath("source_indicators.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.SourceIndicatorService` (`ISourceIndicatorService`) |
| **Underlying Store** | `StockAnalyzer.Core.Services.IndicatorListStore` |
| **Root Type** | `List<CoreIndicatorSettings>` |

Defines registered "Source Indicators" that compute intermediate numerical series. Higher-order composite indicators can consume these registered outputs instead of raw OHLCV prices.

### Load State & Corruption Safety
Managed by `IndicatorListStore`:
- If unreadable due to file lock or transient I/O issues, the service remains in the `Unloaded` state and forbids writing to prevent truncating existing definitions.
- If corrupt JSON is detected, the broken file is backed up to `source_indicators.json.corrupted` before starting with an empty store.

### Reference Example
```json
[
  {
    "Id": "c1d2e3f4-5678-90ab-cdef-1234567890cd",
    "Type": "ExponentialMovingAverage",
    "Name": "Fast Baseline EMA",
    "IsVisible": false,
    "TimeFrame": "D1",
    "PanelIndex": 0,
    "Color": "#FF336699",
    "Parameters": {
      "Period": 12
    }
  }
]
```

---

## 3. Dynamic Period Drivers (`dynamic_period_drivers.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/DynamicPeriodDrivers/dynamic_period_drivers.json` |
| **Path Resolver** | `PathDiscovery.ResolveDynamicPeriodDriversPath("dynamic_period_drivers.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.DynamicPeriodDriverService` (`IDynamicPeriodDriverService`) |
| **Underlying Store** | `StockAnalyzer.Core.Services.IndicatorListStore` |
| **Root Type** | `List<CoreIndicatorSettings>` |

Stores registered indicators specifically used as dynamic lookback drivers. Adaptive indicators (such as variable-period moving averages, adaptive bands, or cyclical oscillators) bind to a driver indicator's output value to dynamically adjust their own lookback periods per bar.

### Reference Example
```json
[
  {
    "Id": "d2e3f4a5-6789-01bc-def1-2345678901de",
    "Type": "StandardDeviation",
    "Name": "Volatility Driver SD(20)",
    "IsVisible": false,
    "TimeFrame": "D1",
    "PanelIndex": 0,
    "Color": "#FFCC6600",
    "Parameters": {
      "Period": 20
    }
  }
]
```
