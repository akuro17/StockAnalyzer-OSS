# 07. Indicator Defaults & Driver Settings

This document covers custom indicator configuration files, source indicator pipelines, and dynamic period drivers. All three files serialize as JSON lists of `CoreIndicatorSettings` using `WorkspacePolymorphicResolver`.

---

## 1. User Indicator Defaults (`user_indicator_defaults.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/IndicatorDefaults/user_indicator_defaults.json` |
| **Path Resolver** | `PathDiscovery.ResolveIndicatorDefaultsPath("user_indicator_defaults.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.IndicatorUserDefaultService` (`IIndicatorUserDefaultService`) |
| **On-Disk Root Type**| `List<CoreIndicatorSettings>` (**JSON Array `[ ... ]`**) |
| **In-Memory Type** | `Dictionary<IndicatorType, CoreIndicatorSettings>` (mapped by `TypeEnum`) |
| **Serialization** | `WorkspacePolymorphicResolver` (requires `"$type"` in `ParameterObject`) |

Whenever a user adds a new indicator to a chart or screener, `IndicatorUserDefaultService` checks this collection for user-customized defaults before falling back to built-in system defaults.

> [!IMPORTANT]
> The on-disk file is serialized as a **JSON Array of `CoreIndicatorSettings`** (`[ ... ]`), NOT a JSON dictionary/object. During `LoadUserDefaultsAsync()`, the service deserializes the array and indexes each entry by its `TypeEnum` into an in-memory dictionary.

### Reference Example (`user_indicator_defaults.json`)

```json
[
  {
    "Id": "00000000-0000-0000-0000-000000000001",
    "TypeEnum": 0,
    "DisplayName": "SMA(25)",
    "IsEnabled": true,
    "IsOverlay": true,
    "OverlayPanelId": null,
    "Color": "#FFFF8000",
    "Thickness": 1.5,
    "Style": 0,
    "Offset": 0,
    "PriceSource": 0,
    "ParameterObject": {
      "$type": "CoreSmaParameter",
      "Period": 25
    }
  },
  {
    "Id": "00000000-0000-0000-0000-000000000002",
    "TypeEnum": 34,
    "DisplayName": "RSI(14)",
    "IsEnabled": true,
    "IsOverlay": false,
    "OverlayPanelId": "SubPanel_Rsi",
    "Color": "#FF9933CC",
    "Thickness": 1.5,
    "Style": 0,
    "Offset": 0,
    "PriceSource": 0,
    "ParameterObject": {
      "$type": "CoreRsiParameter",
      "Period": 14
    }
  }
]
```

---

## 2. Multi-Stage Source Indicators (`source_indicators.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/SourceIndicators/source_indicators.json` |
| **Path Resolver** | `PathDiscovery.ResolveSourceIndicatorsPath("source_indicators.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.SourceIndicatorService` (`ISourceIndicatorService`) |
| **Underlying Store** | `StockAnalyzer.Core.Services.IndicatorListStore` |
| **On-Disk Root Type**| `List<CoreIndicatorSettings>` (**JSON Array `[ ... ]`**) |

Defines registered "Source Indicators" that compute intermediate numerical series. Higher-order composite indicators (Indicator-on-Indicator) can consume these registered outputs by setting `SourceIndicatorId` to the source indicator's `Id`.

### Load State & Corruption Safety
Managed by `IndicatorListStore`:
- If unreadable due to file lock or transient I/O issues, the service remains in the `Unloaded` state and forbids writing to prevent truncating existing definitions.
- If corrupt JSON is detected, the broken file is backed up to `source_indicators.json.corrupted` before starting with an empty store.

### Reference Example (`source_indicators.json`)

```json
[
  {
    "Id": "c1d2e3f4-5678-90ab-cdef-1234567890cd",
    "TypeEnum": 1,
    "DisplayName": "Fast Baseline EMA",
    "IsEnabled": false,
    "IsOverlay": true,
    "OverlayPanelId": null,
    "Color": "#FF336699",
    "Thickness": 1.5,
    "Style": 0,
    "Offset": 0,
    "PriceSource": 0,
    "ParameterObject": {
      "$type": "CoreEmaParameter",
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
| **On-Disk Root Type**| `List<CoreIndicatorSettings>` (**JSON Array `[ ... ]`**) |

Stores registered indicators specifically used as dynamic lookback drivers. Adaptive indicators (such as variable-period moving averages, adaptive bands, or cyclical oscillators) bind to a driver indicator's output value via `DynamicPeriodIndicatorId` to dynamically modulate their own lookback periods on a bar-by-bar basis.

### Reference Example (`dynamic_period_drivers.json`)

```json
[
  {
    "Id": "d2e3f4a5-6789-01bc-def1-2345678901de",
    "TypeEnum": 29,
    "DisplayName": "Volatility Driver SD(20)",
    "IsEnabled": false,
    "IsOverlay": false,
    "OverlayPanelId": null,
    "Color": "#FFCC6600",
    "Thickness": 1.5,
    "Style": 0,
    "Offset": 0,
    "PriceSource": 0,
    "ParameterObject": {
      "$type": "CoreStandardDeviationParameter",
      "Period": 20
    }
  }
]
```
