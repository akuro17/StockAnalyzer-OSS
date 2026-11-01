# 04. Ticker List & Screener Settings

This document specifies user settings for the Ticker List grid navigation, autoplay behavior, and custom scatter plot analytics metrics.

---

## 1. Ticker List Settings (`user_tickers_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_tickers_settings.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("user_tickers_settings.json")` |
| **Managing Class** | `StockAnalyzer.Avalonia.Services.TickersSettingsManager` (`ITickersSettingsManager`) |
| **Bound Configuration** | `appsettings.json` section `"Tickers"` defines bounds |

### Properties Schema

| Field Name | Type | Allowed Range | Description |
|---|---|---|---|
| `AutoPlayIntervalSeconds` | `integer?` | `[1, 3600]` | Delay in seconds between automatically stepping to the next symbol in the ticker list. |
| `AutoPlayStopAtListEnd` | `boolean?` | `true / false` | If `true`, autoplay halts when reaching the end of the current ticker list. |
| `AutoPlayStopOnListChange` | `boolean?` | `true / false` | If `true`, autoplay halts immediately if the active watchlist or filter criteria change. |
| `ColumnSelectionScope` | `integer?` | Enum Ordinal | Scope of column visibility customization (`0` = Global, `1` = PerProfile). |

> [!NOTE]
> All fields are nullable in the JSON file. An absent property safely falls back to the default configured in `appsettings.json`. If a value is out of bounds, the entire file is rejected and in-memory defaults remain active without corrupting the file.

### Reference Example
```json
{
  "AutoPlayIntervalSeconds": 5,
  "AutoPlayStopAtListEnd": true,
  "AutoPlayStopOnListChange": true,
  "ColumnSelectionScope": 0
}
```

---

## 2. Registered Scatter Metrics (`scatter_metrics.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/scatter_metrics.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("scatter_metrics.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Analysis.ScatterMetricRegistry` (`IScatterMetricRegistry`) |
| **Model Class** | `StockAnalyzer.Core.Models.Analysis.ScatterMetricFile` |
| **Current Format Version** | `1` (`CurrentFormatVersion = 1`) |
| **Capacity Limit** | Configured by `ScatterMetrics:MaxRegisteredMetrics` (default `32`) |

The Scatter Plot tool allows correlating any technical indicator or price metric against another across a ticker universe. Registered custom metrics persist in this file.

### Root Schema (`ScatterMetricFile`)

| Property | Type | Required | Description |
|---|---|---|---|
| `FormatVersion` | `integer` | Yes | Format version (must equal `1`). |
| `Metrics` | `array of ScatterMetricDefinition` | Yes | List of registered scatter metric specifications. |

### Metric Item Schema (`ScatterMetricDefinition`)

| Property | Type | Description |
|---|---|---|
| `Id` | `string (GUID)` | Immutable identity of this registered metric entry. |
| `Source` | `object` | Detailed metric calculation and parameter payload (`ScreenerIndicatorSideConfig`). |

#### `Source` Object Properties (`ScreenerIndicatorSideConfig`)
- **`Category`** (`string (Enum)`): Source data domain (`"Indicator"`, `"Price"`, `"Metadata"`, `"Fundamental"`).
- **`IndicatorType`** (`string (Enum)`): Valid indicator type from `IndicatorType` enum (e.g. `"RelativeStrengthIndex"`, `"RateOfChange"`).
- **`CustomDisplayName`** (`string?`): Optional alias rendered on scatter chart axes.
- **`TimeFrame`** (`string (Enum)`): Analysis timeframe (`"D1"`, `"W1"`, `"MN1"`).
- **`Offset`** (`integer`, default `0`, range `[0, MaxOffset]`): Historical bar lookback offset ($[-n]$ bars prior to latest bar).
- **`Parameters`** (`object`): Dictionary of parameter values for the indicator (e.g. `{"Period": 14}`).

### Reference Example
```json
{
  "FormatVersion": 1,
  "Metrics": [
    {
      "Id": "4f9b2d8e-1a3c-4e5f-8b7a-9c0d1e2f3a4b",
      "Source": {
        "Category": "Indicator",
        "IndicatorType": "RelativeStrengthIndex",
        "CustomDisplayName": "RSI 14",
        "TimeFrame": "D1",
        "Offset": 0,
        "Parameters": {
          "Period": 14
        }
      }
    },
    {
      "Id": "8e7d6c5b-4a3f-2e1d-0c9b-8a7f6e5d4c3b",
      "Source": {
        "Category": "Indicator",
        "IndicatorType": "RateOfChange",
        "CustomDisplayName": "ROC 20 (W1)",
        "TimeFrame": "W1",
        "Offset": 1,
        "Parameters": {
          "Period": 20
        }
      }
    }
  ]
}
```
