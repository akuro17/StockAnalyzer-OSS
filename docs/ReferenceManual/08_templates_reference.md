# 08. Domain Templates Schema Reference

## 1. Overview
Templates represent named, reusable configurations stored under `Data/Templates/{TemplateType}/{guid}.json`. They are managed by `StockAnalyzer.Core.Services.TemplateService` (`ITemplateService`).

Each template is saved as an individual file named after its GUID (`{guid}.json`), ensuring atomic updates and eliminating file lock collisions.

---

## 2. Common Template Base Schema (`TemplateBase`)

Every template inherits from `TemplateBase` and includes these top-level fields:

| Field Name | Type | Required | Description |
|---|---|---|---|
| `Id` | `string (GUID)` | Yes | Unique immutable identity for this template. |
| `Name` | `string` | Yes | Human-readable template display name. |
| `TemplateType` | `integer` or `string` | Yes | Discriminator matching `TemplateType` (`0` = `Indicator`, `1` = `Column`, `2` = `Feature`, `3` = `Filter`, `4` = `Theme`, `5` = `Screener`, `6` = `BacktestCondition`). |
| `SchemaVersion` | `integer` | Yes | Version number for backwards-compatibility migrations. |
| `Description` | `string?` | Optional | User description or documentation text. |
| `CreatedAt` | `string (ISO 8601)` | Yes | Creation timestamp in UTC with `Z` suffix. |
| `UpdatedAt` | `string (ISO 8601)` | Yes | Last modified timestamp in UTC with `Z` suffix. |
| `IsSystem` | `boolean` | Optional | `true` if shipped as a built-in read-only template (default `false`). |
| `IsFavorite` | `boolean` | Optional | `true` if pinned by the user (default `false`). |

---

## 3. Template Categories & Schemas

### 3.1. Indicator Template (`TemplateType.Indicator` / `0`)
- **Storage Path**: `Data/Templates/Indicator/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.IndicatorTemplate`
- **Purpose**: Stores a preset bundle of indicators and parameter configurations.

```json
{
  "Id": "11111111-2222-3333-4444-555555555555",
  "Name": "Moving Average Ribbon",
  "TemplateType": 0,
  "SchemaVersion": 1,
  "Description": "3-line EMA trend confirmation bundle",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Indicators": [
    {
      "Id": "a1b2c3d4-0001-0000-0000-000000000000",
      "TypeEnum": 1,
      "DisplayName": "EMA(20)",
      "IsEnabled": true,
      "IsOverlay": true,
      "OverlayPanelId": null,
      "Color": "#FF26A69A",
      "Thickness": 1.5,
      "Style": 0,
      "Offset": 0,
      "PriceSource": 0,
      "ParameterObject": {
        "$type": "CoreEmaParameter",
        "Period": 20
      }
    },
    {
      "Id": "a1b2c3d4-0002-0000-0000-000000000000",
      "TypeEnum": 1,
      "DisplayName": "EMA(50)",
      "IsEnabled": true,
      "IsOverlay": true,
      "OverlayPanelId": null,
      "Color": "#FFFFB74D",
      "Thickness": 1.5,
      "Style": 0,
      "Offset": 0,
      "PriceSource": 0,
      "ParameterObject": {
        "$type": "CoreEmaParameter",
        "Period": 50
      }
    }
  ]
}
```

---

### 3.2. Column Layout Template (`TemplateType.Column` / `1`)
- **Storage Path**: `Data/Templates/Column/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.ColumnTemplate`
- **Purpose**: Stores custom visible column sets for the Ticker List grid.

```json
{
  "Id": "22222222-3333-4444-5555-666666666666",
  "Name": "Momentum & Volatility Columns",
  "TemplateType": 1,
  "SchemaVersion": 1,
  "Description": "Columns showing Price, Changes and Volume",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "ColumnNames": [
    "Col_Symbol",
    "Col_Name",
    "Col_Price",
    "Col_ChangePercent",
    "Col_Volume"
  ]
}
```

---

### 3.3. Filter Condition Template (`TemplateType.Filter` / `3`)
- **Storage Path**: `Data/Templates/Filter/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.FilterTemplate`
- **Purpose**: Saves reusable boolean filter rule trees (`FilterSettings`) for screener scans.

```json
{
  "Id": "33333333-4444-5555-6666-777777777777",
  "Name": "Tech Sector Tag Filter",
  "TemplateType": 3,
  "SchemaVersion": 1,
  "Description": "Filters symbols belonging to Technology sector",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "RootSettings": {
    "Id": "f1f1f1f1-0000-0000-0000-000000000000",
    "Name": "Main Criteria",
    "ParentId": "00000000-0000-0000-0000-000000000000",
    "Rules": [
      {
        "Field": "Sector",
        "Operator": "Equals",
        "Value": "Technology",
        "IsCompareToField": false,
        "CompareMode": "String"
      }
    ],
    "Children": []
  }
}
```

| `FilterRule` Property | Type | Description |
|---|---|---|
| `Field` | `string` | Target column or field (e.g. `"Sector"`, `"Industry"`, `"Tag"`, `"Price"`). |
| `Operator` | `string` | Comparison operator (`"Equals"`, `"NotEquals"`, `"Contains"`, `"NotContains"`). |
| `Value` | `string` | String representation of filter value. |
| `IsCompareToField` | `boolean` | `true` if comparing against another field instead of a literal value. |
| `CompareMode` | `string` | Evaluation mode (`"Numeric"`, `"String"`, `"Indicator"`). |

---

### 3.4. Screener Indicator Template (`TemplateType.Screener` / `5`)
- **Storage Path**: `Data/Templates/Screener/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.ScreenerIndicatorTemplate`
- **Schema Version**: `2` (`CurrentSchemaVersion = 2`)
- **Purpose**: Saves screener query condition entries.

```json
{
  "Id": "44444444-5555-6666-7777-888888888888",
  "Name": "RSI Oversold Screener",
  "TemplateType": 5,
  "SchemaVersion": 2,
  "Description": "Daily RSI below 30 threshold",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Entries": [
    {
      "Label": "A",
      "IsEnabled": true,
      "LogicalOperator": 0,
      "CategoryType": 0,
      "LeftHand": {
        "CategoryType": 0,
        "IndicatorType": 34,
        "OutputName": "Main",
        "CustomDisplayName": "RSI(14)",
        "TimeFrame": 1,
        "Offset": 0,
        "Parameters": {
          "Period": 14
        }
      },
      "Operator": 1,
      "TargetMode": 0,
      "RightNumericValue": 30.0,
      "RightStringValue": "",
      "RightHand": {
        "CategoryType": 0,
        "IndicatorType": 0,
        "OutputName": "Main",
        "CustomDisplayName": "",
        "TimeFrame": 1,
        "Offset": 0,
        "Parameters": {}
      }
    }
  ]
}
```

---

### 3.5. Theme Template (`TemplateType.Theme` / `4`)
- **Storage Path**: `Data/Templates/Theme/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.ThemeTemplate`
- **Purpose**: Saves comprehensive custom color schemes (`ThemeColors`).

```json
{
  "Id": "55555555-6666-7777-8888-999999999999",
  "Name": "Nord Dark Preset",
  "TemplateType": 4,
  "SchemaVersion": 1,
  "Description": "Cool arctic-inspired color palette",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Colors": {
    "ChartBackground": "#FF2E3440",
    "GridLine": "#FF3B4252",
    "AxisText": "#FFD8DEE9",
    "Crosshair": "#FF88C0D0",
    "CrosshairLineAlpha": "#A088C0D0",
    "CrosshairText": "#FFD8DEE9",
    "ZeroLine": "#FF4C566A",
    "Bullish": "#FFA3BE8C",
    "Bearish": "#FFBF616A",
    "BullishWick": "#FFA3BE8C",
    "BearishWick": "#FFBF616A",
    "VolumeUp": "#FFA3BE8C",
    "VolumeDown": "#FFBF616A",
    "SemanticPlus": "#FFA3BE8C",
    "SemanticMinus": "#FFBF616A",
    "SemanticNeutral": "#FFE5E9F0",
    "ShellBackground": "#FF2E3440",
    "ShellText": "#FFE5E9F0",
    "ShellSecondaryText": "#FFD8DEE9",
    "ShellAccent": "#FF88C0D0",
    "ShellBorder": "#FF4C566A",
    "ButtonBackground": "#FF88C0D0",
    "ButtonText": "#FF2E3440",
    "ButtonHover": "#FF81A1C1",
    "ButtonPressed": "#FF5E81AC",
    "ControlBackground": "#FF3B4252",
    "ControlBackgroundHover": "#FF434C5E",
    "ControlBackgroundPressed": "#FF4C566A",
    "IsDark": true
  }
}
```

---

### 3.6. Machine Learning Feature Specification Template (`TemplateType.Feature` / `2`)
- **Storage Path**: `Data/Templates/Feature/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.FeatureSpecTemplate`
- **Purpose**: Defines feature channel combinations for model training runs.

```json
{
  "Id": "66666666-7777-8888-9999-000000000000",
  "Name": "Price + Momentum Channels",
  "TemplateType": 2,
  "SchemaVersion": 1,
  "Description": "Close price with RSI and SMA channels",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "Spec": {
    "Channels": [
      {
        "Kind": "price",
        "Price": 0,
        "Indicator": null,
        "Params": {},
        "Normalization": 1
      },
      {
        "Kind": "indicator",
        "Price": null,
        "Indicator": 34,
        "Params": {
          "Period": "14"
        },
        "Normalization": 0
      }
    ]
  }
}
```

| `FeatureChannel` Property | Type | Description |
|---|---|---|
| `Kind` | `string` (required) | Discriminator string: `"price"` or `"indicator"`. |
| `Price` | `integer?` | Price type when `Kind == "price"` (`0`=Close, `1`=Open, `2`=High, `3`=Low). |
| `Indicator` | `integer?` or `string?` | Indicator type when `Kind == "indicator"` (`34` or `"RSI"`, `0` or `"SMA"`, etc.). |
| `Params` | `object` | String-to-string dictionary of parameters (e.g. `{"Period": "14"}`). |
| `Normalization` | `integer` or `string` | Normalization mode: `0` = `None`, `1` = `WindowMinMax`, `2` = `WindowZScore`. |

---

### 3.7. Backtest Condition Strategy Template (`TemplateType.BacktestCondition` / `6`)
- **Storage Path**: `Data/Templates/BacktestCondition/{guid}.json`
- **Model**: `StockAnalyzer.Core.Models.Templates.BacktestConditionTemplate`
- **Purpose**: Complete backtest condition tree and risk management rules.

```json
{
  "Id": "77777777-8888-9999-0000-111111111111",
  "Name": "RSI Oversold Entry Strategy",
  "TemplateType": 6,
  "SchemaVersion": 1,
  "Description": "RSI < 30 entry with 3% stop loss and 10% take profit",
  "CreatedAt": "2026-11-02T00:00:00Z",
  "UpdatedAt": "2026-11-02T00:00:00Z",
  "ConditionTree": {
    "EntryLong": {
      "Kind": 1,
      "Operator": 0,
      "Children": [
        {
          "Kind": 0,
          "Comparison": {
            "Left": {
              "IndicatorType": 34,
              "OutputName": "Main",
              "Offset": 0,
              "Parameters": {
                "$type": "CoreRsiParameter",
                "Period": 14
              }
            },
            "Operator": 1,
            "TargetMode": 0,
            "RightNumericValue": 30.0,
            "Right": null,
            "LogicalOperator": 0,
            "Role": 0,
            "Position": 0
          },
          "Children": []
        }
      ]
    },
    "EntryShort": { "Kind": 1, "Operator": 0, "Children": [] },
    "ExitLong": { "Kind": 1, "Operator": 0, "Children": [] },
    "ExitShort": { "Kind": 1, "Operator": 0, "Children": [] },
    "ReverseLong": { "Kind": 1, "Operator": 0, "Children": [] },
    "ReverseShort": { "Kind": 1, "Operator": 0, "Children": [] }
  },
  "RiskManagement": {
    "StopLossPercent": 3.0,
    "TakeProfitPercent": 10.0
  }
}
```
