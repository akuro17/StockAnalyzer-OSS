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

When customizing column visibility, column order, or column templates (`ColumnTemplate` / `TickerListVisibleColumns`), reference the **`MemberName`** (the property identifier used in JSON configurations), NOT the `HeaderKey` (which is the localization resource key):

| MemberName (JSON ID) | HeaderKey (Resource) | Display Name | Category | Description |
|---|---|---|---|---|
| `IsChecked` | `Col_Select` | Select | Basic | Row selection checkbox for batch operations |
| `Symbol` | `Col_Symbol` | Symbol | Basic | Ticker symbol (e.g. `"AAPL"`) |
| `Name` | `Col_Name` | Name | Basic | Full company official name |
| `Sector` | `Col_Sector` | Sector | Basic | GICS sector classification |
| `Industry` | `Col_Industry` | Industry | Basic | GICS industry group classification |
| `Open` | `Col_Open` | Open | PriceVolume | Current day open price |
| `High` | `Col_High` | High | PriceVolume | Current day high price |
| `Low` | `Col_Low` | Low | PriceVolume | Current day low price |
| `Close` | `Col_Price` | Close | PriceVolume | Latest close / market price |
| `Change` | `Col_Change` | Change | PriceVolume | Absolute price change vs previous close |
| `ChangePercent` | `Col_Ratio` | Change % | PriceVolume | Percentage price change vs previous close |
| `Volume` | `Col_Volume` | Volume | PriceVolume | Trading volume in shares |
| `MarketCap` | `Col_MarketCap` | Market Cap | Financial | Total company market capitalization |
| `TrailingPE` | `Col_TrailingPE` | Trailing P/E | Valuation | Price-to-Earnings ratio (TTM) |
| `ForwardPE` | `Col_ForwardPE` | Forward P/E | Valuation | Price-to-Earnings forecasted next 12M |
| `PriceToBook` | `Col_PriceToBook` | P/B Ratio | Valuation | Price-to-Book ratio |
| `DividendYield` | `Col_DividendYield` | Div Yield | Ratio | Indicated annual dividend yield (%) |
| `ReturnOnEquity` | `Col_ReturnOnEquity` | ROE | Ratio | Return on Equity (%) |
| `ReturnOnAssets` | `Col_ReturnOnAssets` | ROA | Ratio | Return on Assets (%) |
| `GrossMargins` | `Col_GrossMargins` | Gross Margin | Ratio | Gross profit margin (%) |
| `OperatingMargins` | `Col_OperatingMargins` | Operating Margin | Ratio | Operating income margin (%) |
| `ProfitMargins` | `Col_ProfitMargins` | Profit Margin | Ratio | Net profit margin (%) |
| `Beta` | `Col_Beta` | Beta | Financial | Systematic risk / volatility relative to index |
| `FiftyTwoWeekHigh` | `Col_FiftyTwoWeekHigh` | 52W High | Financial | Highest price in last 52 weeks |
| `FiftyTwoWeekLow` | `Col_FiftyTwoWeekLow` | 52W Low | Financial | Lowest price in last 52 weeks |
| `AverageVolume` | `Col_AverageVolume` | Avg Volume | Financial | 3-month average daily volume |

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
| `IndicatorType` | `integer` or `string` | If Indicator | Valid `IndicatorType` (Ordinal or Name, e.g. `34` or `"RSI"`, `0` or `"SMA"`, `62` or `"ROC"`). See `09_indicator_catalog.md`. |
| `OutputName` | `string` | Optional | Output series name (defaults to `"Main"`). Multi-line indicators can specify `"Signal"`, `"Histogram"`, `"UpperBand"`, etc. |
| `TimeFrame` | `integer` or `string` | Optional | Analysis timeframe (`6` / `"D1"`, `7` / `"W1"`, `8` / `"MN1"`). Daily is `6`. |
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
        "TimeFrame": 6,
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
        "IndicatorType": 62,
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
