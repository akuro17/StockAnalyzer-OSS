# 02. Workspace Configuration (`workspace.json`)

## 1. File Specification

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/workspace.json` (canonical SSoT: `LayoutConstants.DefaultWorkspaceFileName`) |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath("workspace.json")` |
| **Managing Class** | `StockAnalyzer.Core.Services.WorkspaceSerializationService` (`IWorkspaceSerializationService`) |
| **Model Class** | `StockAnalyzer.Core.Models.Settings.WorkspaceSettings` |
| **Current Schema Version** | `2` (`CurrentSchemaVersion = 2`) |
| **Serialization Format** | JSON (UTF-8, polymorphic type resolver for indicators) |

---

## 2. Root Structure Schema (`WorkspaceSettings`)

| Field Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `SchemaVersion` | `integer` | Yes | `2` | Schema version for format migration. |
| `Name` | `string` | Yes | `"Default Profile"` | Profile display name. |
| `LastModified` | `string (ISO 8601)` | Yes | `Current UTC` | Timestamp of last user workspace modification. |
| `SelectedTicker` | `string?` | Optional | `"AAPL"` | Active selected symbol across synchronized views. |
| `SelectedTimeframe` | `string?` | Optional | `"D1"` | Active timeframe (`"D1"`, `"W1"`, `"MN1"`). |
| `MaxCandleCount` | `integer` | Optional | `200` | Number of historical bars loaded on chart initialization. |
| `ChartType` | `string?` | Optional | `"Candle"` | Primary chart visualization type (`"Candle"`, `"Ohlc"`, `"HeikinAshi"`, `"Line"`, `"Area"`, `"Dot"`). |
| `IsMainWindowVisible` | `boolean` | Optional | `true` | Visibility state of primary chart window. |
| `IsSubWindowVisible` | `boolean` | Optional | `true` | Visibility state of secondary indicator pane window. |
| `ThemeSettings` | `object` | Optional | `{}` | Key-value overrides for chart colors (decoupled hex strings). |

---

## 3. Dock Panel Layout Properties

The workspace manages four perimeter dock panels surrounding the central chart area:

| Field Name | Type | Default | Description |
|---|---|---|---|
| `LeftPanelWidth` | `number` | `200.0` | Width of left dock panel in DIPs. |
| `RightPanelWidth` | `number` | `200.0` | Width of right dock panel in DIPs. |
| `TopPanelHeight` | `number` | `200.0` | Height of top dock panel in DIPs. |
| `BottomPanelHeight` | `number` | `200.0` | Height of bottom dock panel in DIPs. |
| `IsLeftPanelVisible` | `boolean` | `true` | Whether the left dock panel is expanded. |
| `IsRightPanelVisible` | `boolean` | `true` | Whether the right dock panel is expanded. |
| `IsTopPanelVisible` | `boolean` | `false` | Whether the top dock panel is expanded. |
| `IsBottomPanelVisible` | `boolean` | `false` | Whether the bottom dock panel is expanded. |
| `IsLeftPanelPinned` | `boolean` | `true` | Whether left panel is pinned (vs auto-hide). |
| `IsRightPanelPinned` | `boolean` | `true` | Whether right panel is pinned. |
| `IsTopPanelPinned` | `boolean` | `true` | Whether top panel is pinned. |
| `IsBottomPanelPinned` | `boolean` | `true` | Whether bottom panel is pinned. |
| `PanelSelectedTabIndices` | `object` | `{}` | Selected tab index per panel key (`"Left"`, `"Right"`, `"Top"`, `"Bottom"`). |
| `PanelTabIds` | `object` | `{}` | Array of tab identifier strings hosted in each panel. |

---

## 4. Active Indicators Schema (`CoreIndicatorSettings`)

Stored in the root array `Indicators`:

```json
{
  "Id": "00000000-0000-0000-0000-000000000000",
  "Type": "SimpleMovingAverage",
  "Name": "SMA(20)",
  "IsVisible": true,
  "TimeFrame": "D1",
  "PanelIndex": 0,
  "Color": "#FFFF0000",
  "Parameters": {
    "Period": 20
  }
}
```

| Property | Type | Description |
|---|---|---|
| `Id` | `string (GUID)` | Unique instance identifier for this indicator on the chart. |
| `Type` | `string (Enum)` | Name of the registered indicator type matching `IndicatorType` enum (e.g. `"SimpleMovingAverage"`, `"RelativeStrengthIndex"`, `"BollingerBands"`). |
| `Name` | `string` | Display label rendered in chart legend. |
| `IsVisible` | `boolean` | Toggle for rendering calculation outputs on the viewport. |
| `TimeFrame` | `string (Enum)` | Dedicated timeframe if calculating multi-timeframe overlays (e.g. `"D1"`, `"W1"`, `"MN1"`). |
| `PanelIndex` | `integer` | `0` renders on the primary price chart; `1`, `2`, ... render in dedicated sub-chart panes below. |
| `Color` | `string (Hex)` | Primary stroke color encoded as 8-character ARGB Hex (`#AARRGGBB`). |
| `Parameters` | `object` | Dictionary of indicator-specific parameters (e.g. `Period`, `Multiplier`, `StandardDeviations`). |

---

## 5. Watchlist Profiles Schema (`WatchlistProfile`)

Stored in the root array `WatchlistProfiles`:

| Property | Type | Description |
|---|---|---|
| `Id` | `string (GUID)` | Unique identifier of the watchlist profile. |
| `Name` | `string` | Human-readable title of the watchlist (e.g. `"Core Tech"`, `"Dividend Yield"`). |
| `Color` | `string (Hex)` | Accent badge color encoded as `#AARRGGBB` (e.g. `"#FF336699"`). |
| `Tickers` | `array of string` | List of ticker symbols belonging to this profile (e.g. `["AAPL", "MSFT", "NVDA"]`). |
| `IsPortfolio` | `boolean` | `true` if this profile is linked to the portfolio tracking engine. |

---

## 6. Complete Reference Example

```json
{
  "SchemaVersion": 2,
  "Name": "Default Profile",
  "LastModified": "2026-11-02T07:00:00Z",
  "SelectedTicker": "AAPL",
  "SelectedTimeframe": "D1",
  "MaxCandleCount": 200,
  "ChartType": "Candle",
  "IsMainWindowVisible": true,
  "IsSubWindowVisible": true,
  "LeftPanelWidth": 240.0,
  "RightPanelWidth": 280.0,
  "TopPanelHeight": 180.0,
  "BottomPanelHeight": 220.0,
  "IsLeftPanelVisible": true,
  "IsRightPanelVisible": true,
  "IsTopPanelVisible": false,
  "IsBottomPanelVisible": true,
  "IsLeftPanelPinned": true,
  "IsRightPanelPinned": true,
  "IsTopPanelPinned": true,
  "IsBottomPanelPinned": true,
  "PanelSelectedTabIndices": {
    "Left": 0,
    "Right": 1,
    "Bottom": 0
  },
  "PanelTabIds": {
    "Left": [ "TickersTab", "WatchlistTab" ],
    "Right": [ "DrawingPropertiesTab", "AnalysisTab" ],
    "Bottom": [ "LogViewerTab", "ScreenerTab" ]
  },
  "ThemeSettings": {
    "ChartBackground": "#FAEABB",
    "GridLineColor": "#D3D3D3"
  },
  "Indicators": [
    {
      "Id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "Type": "SimpleMovingAverage",
      "Name": "SMA(20)",
      "IsVisible": true,
      "TimeFrame": "D1",
      "PanelIndex": 0,
      "Color": "#FFFF0000",
      "Parameters": {
        "Period": 20
      }
    },
    {
      "Id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "Type": "RelativeStrengthIndex",
      "Name": "RSI(14)",
      "IsVisible": true,
      "TimeFrame": "D1",
      "PanelIndex": 1,
      "Color": "#FF0000FF",
      "Parameters": {
        "Period": 14
      }
    }
  ],
  "WatchlistProfiles": [
    {
      "Id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
      "Name": "Megacap Growth",
      "Color": "#FF2B579A",
      "Tickers": [ "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA" ],
      "IsPortfolio": false
    }
  ]
}
```
