# 04. Ticker List & Screener Settings

This document specifies user settings for the Ticker List grid navigation, autoplay behavior, column registry definitions, and custom scatter plot analytics metrics.

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
| `ColumnSelectionScope` | `integer?` | `0` or `1` | Scope of column visibility customization: **`0` = `PerList`** (each ticker list retains its own distinct column layout), **`1` = `Shared`** (all lists share a single common column layout). |

> [!NOTE]
> All fields are nullable in the JSON file. An absent property safely falls back to the default configured in `appsettings.json`. If a value is out of bounds, the entire file is rejected and in-memory defaults remain active without corrupting the file.

### Reference Example (`user_tickers_settings.json`)
```json
{
  "AutoPlayIntervalSeconds": 5,
  "AutoPlayStopAtListEnd": true,
  "AutoPlayStopOnListChange": true,
  "ColumnSelectionScope": 0
}
```

---

## 2. Standard Ticker Columns Catalog (`WatchlistColumnRegistry`)

When customizing column visibility, column order, or column templates (`ColumnTemplate`), reference the canonical column IDs defined in `WatchlistColumnRegistry`:

| Column ID | Display Name | Category | Description |
|---|---|---|---|
| `Col_Select` | Select | Basic | Row selection checkbox for batch operations |
| `Col_Symbol` | Symbol | Basic | Ticker symbol (e.g. `"AAPL"`) |
| `Col_Name` | Name | Basic | Full company official name |
| `Col_Sector` | Sector | Basic | GICS sector classification |
| `Col_Industry` | Industry | Basic | GICS industry group classification |
| `Col_Open` | Open | PriceVolume | Current day open price |
| `Col_High` | High | PriceVolume | Current day high price |
| `Col_Low` | Low | PriceVolume | Current day low price |
| `Col_Price` | Close | PriceVolume | Latest close / market price |
| `Col_Change` | Change | PriceVolume | Absolute price change vs previous close |
| `Col_ChangePercent`| Change% | PriceVolume | Percentage change vs previous close |
| `Col_Volume` | Volume | PriceVolume | Trading volume in shares |
| `Col_MarketCap` | Market Cap | Fundamental | Total company market capitalization |
| `Col_PE` | P/E | Fundamental | Price-to-Earnings ratio |
| `Col_DividendYield`| Div Yield | Fundamental | Indicated annual dividend yield (%) |
| `Col_Note` | Notes | Basic | User-entered note or strategy comment |
| `Col_Reminder` | Reminder | Basic | User-entered scheduled alert or reminder |
| `Col_Long` | Long | Strategy | Target or actual Long entry price |
| `Col_ExitLong` | TP Long | Strategy | Long take-profit target price |
| `Col_StopLossLong`| SL Long | Strategy | Long stop-loss trigger price |
| `Col_Short` | Short | Strategy | Target or actual Short entry price |
| `Col_ExitShort` | TP Short | Strategy | Short take-profit target price |
| `Col_StopLossShort`| SL Short | Strategy | Short stop-loss trigger price |
| `Col_IsLong` | IsLong | Signals | Active Long condition signal flag |
| `Col_IsTPLong` | IsTP Long | Signals | Active Take-Profit Long signal flag |
| `Col_IsSLLong` | IsSL Long | Signals | Active Stop-Loss Long signal flag |
| `Col_IsShort` | IsShort | Signals | Active Short condition signal flag |
| `Col_IsTPShort` | IsTP Short | Signals | Active Take-Profit Short signal flag |
| `Col_IsSLShort` | IsSL Short | Signals | Active Stop-Loss Short signal flag |

---

## 3. Registered Scatter Metrics (`scatter_metrics.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/scatter_metrics.json` |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("scatter_metrics.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.Analysis.ScatterMetricRegistry` (`IScatterMetricRegistry`) |
| **Model Class** | `StockAnalyzer.Core.Models.Analysis.ScatterMetricFile` |
| **Current Format Version** | `1` (`CurrentFormatVersion = 1`) |
| **Capacity Limit** | Configured by `ScatterMetrics:MaxRegisteredMetrics` (default `32`) |

The Scatter Plot tool correlates technical indicators and price metrics across a ticker universe. Registered custom metrics persist in this file.

### Root Schema (`ScatterMetricFile`)

| Property | Type | Required | Description |
|---|---|---|---|
| `FormatVersion` | `integer` | Yes | Format version (must equal `1`). |
| `Metrics` | `array of ScatterMetricDefinition` | Yes | List of registered scatter metric specifications. |

### Metric Item Schema (`ScatterMetricDefinition`)

| Property | Type | Description |
|---|---|---|
| `Id` | `string (GUID)` | Immutable identity of this registered metric entry. |
| `Source` | `object` | Metric calculation payload (`ScreenerIndicatorSideConfig`). |

#### `Source` Object Properties (`ScreenerIndicatorSideConfig`)

| Property | Type | Required | Description |
|---|---|---|---|
| `CategoryType` | `integer` or `string` | Yes | Category discriminator: **`0` = `Indicator`**, **`1` = `Column`**, **`2` = `Criteria`**. |
| `IndicatorType` | `integer` or `string` | If Indicator | Valid `IndicatorType` (Ordinal or Name, e.g. `34` or `"RSI"`, `0` or `"SMA"`, `104` or `"ROC"`). See `09_indicator_catalog.md`. |
| `OutputName` | `string` | Optional | Output series name (defaults to `"Main"`). Multi-line indicators can specify `"Signal"`, `"Histogram"`, `"UpperBand"`, etc. |
| `TimeFrame` | `integer` or `string` | Optional | Analysis timeframe (`1` / `"D1"`, `7` / `"W1"`, `8` / `"MN1"`). |
| `Offset` | `integer` | Optional | Historical bar lookback offset ($[-n]$ bars prior to latest bar; range `[0, MaxOffset]`, default `0`). |
| `CustomDisplayName`| `string?` | Optional | Custom display alias rendered on scatter chart axes. |
| `Parameters` | `object` | Optional | Dictionary of parameter values for the indicator (e.g. `{"Period": 14}`). |

### Reference Example (`scatter_metrics.json`)

```json
{
  "FormatVersion": 1,
  "Metrics": [
    {
      "Id": "4f9b2d8e-1a3c-4e5f-8b7a-9c0d1e2f3a4b",
      "Source": {
        "CategoryType": 0,
        "IndicatorType": 34,
        "OutputName": "Main",
        "CustomDisplayName": "RSI 14",
        "TimeFrame": 1,
        "Offset": 0,
        "Parameters": {
          "Period": 14
        }
      }
    },
    {
      "Id": "8e7d6c5b-4a3f-2e1d-0c9b-8a7f6e5d4c3b",
      "Source": {
        "CategoryType": 0,
        "IndicatorType": 104,
        "OutputName": "Main",
        "CustomDisplayName": "ROC 20 (W1)",
        "TimeFrame": 7,
        "Offset": 1,
        "Parameters": {
          "Period": 20
        }
      }
    }
  ]
}
```
