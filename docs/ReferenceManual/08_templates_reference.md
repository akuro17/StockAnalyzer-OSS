# 08. Domain Templates Schema Reference

## 1. Overview
Templates represent named, reusable configurations stored under `Data/Templates/{TemplateType}/{guid}.json`. They are managed by `StockAnalyzer.Core.Services.TemplateService` (`ITemplateService`).

Each template is saved as an individual file named after its GUID (`{guid}.json` or `{guid:N}.json`), ensuring atomic updates and eliminating file lock collisions.

---

## 2. Common Template Base Schema (`TemplateBase`)

Every template inherits from `TemplateBase` and includes these top-level fields:

| Field Name | Type | Required | Description |
|---|---|---|---|
| `Id` | `string (GUID)` | Yes | Unique immutable identity for this template. |
| `Name` | `string` | Yes | Human-readable template display name. |
| `TemplateType` | `string (Enum)` | Yes | Discriminator matching `TemplateType` (e.g. `"Indicator"`, `"Column"`, `"Filter"`). |
| `SchemaVersion` | `integer` | Yes | Version number for backwards-compatibility migrations (default `1`). |
| `Description` | `string?` | Optional | User description or documentation text. |
| `CreatedAt` | `string (ISO 8601)` | Yes | Creation timestamp in UTC. |
| `UpdatedAt` | `string (ISO 8601)` | Yes | Last modified timestamp in UTC. |

---

## 3. Template Categories & Schemas

### 3.1. Indicator Template (`TemplateType.Indicator`)
- **Storage Path**: `Data/Templates/Indicator/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.IndicatorTemplate`
- **Purpose**: Stores a preset bundle of indicators and parameter configurations.

```json
{
  "Id": "11111111-2222-3333-4444-555555555555",
  "Name": "Moving Average Ribbon",
  "TemplateType": "Indicator",
  "SchemaVersion": 1,
  "Description": "3-line EMA trend confirmation bundle",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Indicators": [
    {
      "Id": "a1b2c3d4-0001-0000-0000-000000000000",
      "Type": "ExponentialMovingAverage",
      "Name": "EMA(20)",
      "IsVisible": true,
      "TimeFrame": "D1",
      "PanelIndex": 0,
      "Color": "#FF26A69A",
      "Parameters": { "Period": 20 }
    },
    {
      "Id": "a1b2c3d4-0002-0000-0000-000000000000",
      "Type": "ExponentialMovingAverage",
      "Name": "EMA(50)",
      "IsVisible": true,
      "TimeFrame": "D1",
      "PanelIndex": 0,
      "Color": "#FFFFB74D",
      "Parameters": { "Period": 50 }
    },
    {
      "Id": "a1b2c3d4-0003-0000-0000-000000000000",
      "Type": "ExponentialMovingAverage",
      "Name": "EMA(200)",
      "IsVisible": true,
      "TimeFrame": "D1",
      "PanelIndex": 0,
      "Color": "#FFEF5350",
      "Parameters": { "Period": 200 }
    }
  ]
}
```

---

### 3.2. Column Layout Template (`TemplateType.Column`)
- **Storage Path**: `Data/Templates/Column/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.ColumnTemplate`
- **Purpose**: Stores custom visible column sets for the Ticker List grid.

```json
{
  "Id": "22222222-3333-4444-5555-666666666666",
  "Name": "Momentum & Volatility Columns",
  "TemplateType": "Column",
  "SchemaVersion": 1,
  "Description": "Columns showing RSI, ATR and 52-week High distance",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "ColumnNames": [
    "Symbol",
    "ClosePrice",
    "DailyChangePercent",
    "RSI_14",
    "ATR_14",
    "Volume"
  ]
}
```

---

### 3.3. Filter Condition Template (`TemplateType.Filter`)
- **Storage Path**: `Data/Templates/Filter/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.FilterTemplate`
- **Purpose**: Saves reusable boolean filter trees for screener scans.

```json
{
  "Id": "33333333-4444-5555-6666-777777777777",
  "Name": "Golden Cross & Oversold",
  "TemplateType": "Filter",
  "SchemaVersion": 1,
  "Description": "SMA 50 > SMA 200 AND RSI < 40",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "RootSettings": {
    "Id": "f1f1f1f1-0000-0000-0000-000000000000",
    "Name": "Main Criteria",
    "IsEnabled": true,
    "LogicalOperator": "And",
    "InvertResult": false,
    "Conditions": [
      {
        "Id": "c1c1c1c1-0000-0000-0000-000000000001",
        "LeftIndicatorType": "SimpleMovingAverage",
        "LeftParameters": { "Period": 50 },
        "Operator": "GreaterThan",
        "RightIndicatorType": "SimpleMovingAverage",
        "RightParameters": { "Period": 200 }
      },
      {
        "Id": "c1c1c1c1-0000-0000-0000-000000000002",
        "LeftIndicatorType": "RelativeStrengthIndex",
        "LeftParameters": { "Period": 14 },
        "Operator": "LessThan",
        "ConstantValue": 40.0
      }
    ],
    "SubFilters": []
  }
}
```

---

### 3.4. Screener Indicator Template (`TemplateType.Screener`)
- **Storage Path**: `Data/Templates/Screener/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.ScreenerIndicatorTemplate`
- **Schema Version**: `2` (`CurrentSchemaVersion = 2`)
- **Purpose**: Saves screener query column criteria and thresholds.

```json
{
  "Id": "44444444-5555-6666-7777-888888888888",
  "Name": "Trend Breakout Screener",
  "TemplateType": "Screener",
  "SchemaVersion": 2,
  "Description": "Scans for 20-day high breakouts with volume surge",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Entries": [
    {
      "Id": "e1e1e1e1-0000-0000-0000-000000000001",
      "IsEnabled": true,
      "Side": {
        "Category": "Price",
        "IndicatorType": "SimpleMovingAverage",
        "TimeFrame": "D1",
        "Offset": 0,
        "Parameters": {}
      },
      "Operator": "GreaterThan",
      "Threshold": 0.0
    }
  ]
}
```

---

### 3.5. Theme Template (`TemplateType.Theme`)
- **Storage Path**: `Data/Templates/Theme/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.ThemeTemplate`
- **Purpose**: Saves comprehensive custom color schemes.

```json
{
  "Id": "55555555-6666-7777-8888-999999999999",
  "Name": "Nord Dark Preset",
  "TemplateType": "Theme",
  "SchemaVersion": 1,
  "Description": "Cool arctic-inspired color palette",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Colors": {
    "ChartBackground": "#2E3440",
    "GridLineColor": "#3B4252",
    "AxisTextColor": "#D8DEE9",
    "CrosshairColor": "#88C0D0",
    "BullishColor": "#A3BE8C",
    "BearishColor": "#BF616A",
    "NeutralColor": "#4C566A"
  }
}
```

---

### 3.6. Machine Learning Feature Specification Template (`TemplateType.Feature`)
- **Storage Path**: `Data/Templates/Feature/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.FeatureSpecTemplate`
- **Purpose**: Defines feature channel combinations for model training runs.

```json
{
  "Id": "66666666-7777-8888-9999-000000000000",
  "Name": "OHLCV + Momentum Features",
  "TemplateType": "Feature",
  "SchemaVersion": 1,
  "Description": "Normalized price returns with RSI and MACD channels",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Spec": {
    "Channels": [
      {
        "Type": "NormalizedPrice",
        "Normalization": "MinMax"
      },
      {
        "Type": "IndicatorChannel",
        "IndicatorType": "RelativeStrengthIndex",
        "Parameters": { "Period": 14 },
        "Normalization": "ZScore"
      }
    ]
  }
}
```

---

### 3.7. Backtest Condition Strategy Template (`TemplateType.BacktestCondition`)
- **Storage Path**: `Data/Templates/BacktestCondition/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.BacktestConditionTemplate`
- **Purpose**: Strategy entry and exit logic tree with optional risk management.

```json
{
  "Id": "77777777-8888-9999-0000-111111111111",
  "Name": "Moving Average Cross & Trailing Stop",
  "TemplateType": "BacktestCondition",
  "SchemaVersion": 1,
  "Description": "Golden cross entry with 3% trailing stop-loss",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "ConditionTree": {
    "EntryLongRoot": {
      "Operator": "And",
      "Conditions": [
        {
          "LeftIndicatorType": "ExponentialMovingAverage",
          "LeftParameters": { "Period": 20 },
          "Operator": "CrossesAbove",
          "RightIndicatorType": "ExponentialMovingAverage",
          "RightParameters": { "Period": 50 }
        }
      ]
    }
  },
  "RiskManagement": {
    "StopLossPercent": 3.0,
    "TakeProfitPercent": 10.0,
    "TrailingStopPercent": 2.5
  }
}
```
