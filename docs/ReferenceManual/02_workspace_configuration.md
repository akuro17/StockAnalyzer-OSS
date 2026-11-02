# 02. Workspace Configuration (`default_workspace.json`)

## 1. File Specification

| Attribute | Detail |
|---|---|
| **Default File Location** | `Data/Config/default_workspace.json` (canonical SSoT: `LayoutConstants.DefaultWorkspaceFileName`) |
| **User Workspaces Location** | `Data/Workspace/{guid}.json` (managed via Workspace Manager) |
| **Path Resolver** | `PathDiscovery.ResolveConfigPath(LayoutConstants.DefaultWorkspaceFileName)` |
| **Managing Class** | `StockAnalyzer.Core.Services.WorkspaceSerializationService` (`IWorkspaceSerializationService`) |
| **Model Class** | `StockAnalyzer.Core.Models.Settings.WorkspaceSettings` |
| **Current Schema Version** | `2` (`CurrentSchemaVersion = 2`) |
| **Serialization Format** | JSON (UTF-8, polymorphic type resolver for indicators via `WorkspacePolymorphicResolver`) |

---

## 2. Root Structure Schema (`WorkspaceSettings`)

| Field Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `SchemaVersion` | `integer` | Yes | `2` | Schema version for format migration. |
| `Name` | `string` | Yes | `"Default Profile"` | Profile display name. |
| `LastModified` | `string (ISO 8601)` | Yes | `Current UTC` | Timestamp of last user workspace modification. |
| `SelectedTicker` | `string?` | Optional | `"AAPL"` | Active selected symbol across synchronized views. |
| `SelectedTimeframe` | `string?` | Optional | `"Daily"` | Active timeframe. MUST be a `TimeframeType` member name string: `"M1"`, `"M5"`, `"M15"`, `"H1"`, `"H4"`, `"Daily"`, `"Weekly"`, `"Monthly"` (case-insensitive). Do NOT use integers or `"D1"`. |
| `MaxCandleCount` | `integer` | Optional | `200` | Number of historical bars loaded on chart initialization. |
| `ChartType` | `string?` | Optional | `"Candlestick"` | Primary chart visualization type string (must match `ChartType` member names: `"Candlestick"`, `"OHLCBar"`, `"HeikinAshi"`, `"Line"`, `"Area"`, `"Dot"`, `"Renko"`, `"Kagi"`, `"PointAndFigure"`, `"ThreeLineBreak"`, `"ReverseWatch"`, `"RelativePerformance"`, `"Invisible"`). |
| `IsMainWindowVisible` | `boolean` | Optional | `true` | Visibility state of primary chart window. |
| `IsSubWindowVisible` | `boolean` | Optional | `true` | Visibility state of secondary indicator pane window. |
| `ThemeSettings` | `object` | Optional | `{}` | Key-value overrides for chart colors (decoupled hex strings). |
| `Indicators` | `array of CoreIndicatorSettings` | Optional | `[]` | Active technical indicators configured on the chart. |
| `WatchlistProfiles` | `array of WatchlistProfile` | Optional | `[]` | Saved watchlist and portfolio groups. |

### `ChartType` Values

| Ordinal | Member Name | Description |
|---|---|---|
| `0` | `Candlestick` | Standard Japanese candlestick chart |
| `1` | `HeikinAshi` | Smoothed average-bar Heikin-Ashi |
| `2` | `Line` | Close-price continuous line |
| `3` | `Area` | Shaded area under close-price curve |
| `4` | `Dot` | Discrete price marker dots |
| `5` | `Renko` | Constant brick-size trend chart |
| `6` | `Kagi` | Vertical swing reversal chart |
| `7` | `PointAndFigure` | Classical X and O box-reversal chart |
| `8` | `ThreeLineBreak` | Multi-bar high/low breakout blocks |
| `9` | `OHLCBar` | Western Open-High-Low-Close bar |
| `10` | `ReverseWatch` | Volume vs. price phase-quadrant curve |
| `11` | `RelativePerformance`| Multi-symbol normalized percentage baseline comparison |
| `12` | `Invisible` | Hidden price layer (indicator/drawing canvas only) |

---

## 3. Dock Panel Layout Properties & Tab Identifiers

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

### Valid Tab Identifiers (`TabRegistry`)

When assigning tab strings inside `PanelTabIds`, use only the following registered IDs (these are the `TabRegistry` IDs, NOT the `Tab_*` localization keys). Keys of `PanelTabIds` MUST be `"Left"`, `"Right"`, `"Top"`, `"Bottom"`:
- `"Chart"`: Main candlestick & technical analysis chart.
- `"TickerList"`: Ticker list, watchlists and filters.
- `"DataWindow"`: Data window (values under cursor).
- `"DrawingTools"`: Drawing tool sidebar.
- `"PortfolioSummary"`: Portfolio summary.
- `"Allocation"`: Portfolio allocation panel.
- `"SeasonalityChart"`: Seasonality chart.
- `"NoteTimeline"`: Notes timeline.
- `"DrawingObjects"`: Drawing objects list.
- `"ScatterPlot"` / `"ScatterAnalysis"`: Scatter plot analysis.
- `"PanelChart_{integer}"`: Additional chart panels (suffix is a numeric panel id, e.g. `"PanelChart_2"`).

Backtest, Screener, Training and Settings are separate windows/dialogs, NOT tab IDs.
---

## 4. Active Indicators Schema (`CoreIndicatorSettings`)

Stored in the root array `Indicators`:

```json
{
  "Id": "00000000-0000-0000-0000-000000000001",
  "TypeEnum": 0,
  "DisplayName": "SMA(20)",
  "IsEnabled": true,
  "IsOverlay": true,
  "OverlayPanelId": null,
  "Color": "#FFFF0000",
  "Thickness": 1.5,
  "Style": 0,
  "Offset": 0,
  "PriceSource": 0,
  "ParameterObject": {
    "$type": "CoreSmaParameter",
    "Period": 20
  }
}
```

| Property | Type | Description |
|---|---|---|
| `Id` | `string (GUID)` | Unique instance identifier for this indicator instance. |
| `TypeEnum` | `integer` or `string` | Indicator type enum (Ordinal `0` or `"SMA"`, `34` or `"RSI"`, `33` or `"BB"`, etc. See `09_indicator_catalog.md`). |
| `DisplayName` | `string` | Display label rendered on chart and legends. |
| `IsEnabled` | `boolean` | Toggle for rendering calculation outputs on the viewport. |
| `IsOverlay` | `boolean` | `true` renders directly over the price chart; `false` renders in a sub-chart pane. |
| `OverlayPanelId` | `string?` | Optional sub-panel grouping ID for non-overlay indicators. |
| `Color` | `string (Hex)` | Primary stroke color encoded as 8-character ARGB Hex (`#AARRGGBB`). |
| `Thickness` | `number` | Line stroke width in DIPs (default `1.5`). |
| `Style` | `integer` | Line style enum (`0` = Solid, `1` = Dash, `2` = Dot, `3` = DashDot). |
| `Offset` | `integer` | Bar displacement shift (default `0`). |
| `PriceSource` | `integer` | Price calculation source (`0` = Close, `1` = Open, `2` = High, `3` = Low, `4` = Median, `5` = Typical, `6` = Weighted). |
| `ParameterObject` | `object` | Polymorphic parameter object. MUST include `"$type"` property naming the concrete parameter class (e.g. `"$type": "CoreSmaParameter"`). See `09_indicator_catalog.md`. |

---

## 5. Watchlist Profiles Schema (`WatchlistProfile`)

Stored in the root array `WatchlistProfiles`:

```json
{
  "Id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
  "Name": "Megacap Growth",
  "Color": "#FF2B579A",
  "IsPortfolio": false,
  "Items": [
    {
      "Ticker": "AAPL",
      "AddedAtUtc": "2026-11-02T00:00:00Z"
    },
    {
      "Ticker": "MSFT",
      "AddedAtUtc": "2026-11-02T00:00:00Z"
    }
  ]
}
```

| Property | Type | Required | Description |
|---|---|---|---|
| `Id` | `string (GUID)` | Yes | Unique identifier of the watchlist profile (must not be empty GUID). |
| `Name` | `string` | Yes | Human-readable title of the watchlist (max 64 characters). |
| `Color` | `string (Hex)` | Yes | Accent badge color encoded as `#AARRGGBB` (e.g. `"#FF336699"`). |
| `IsPortfolio` | `boolean` | Yes | `true` if this profile is managed as a portfolio position set. |
| `Items` | `array of WatchlistItem` | Yes | Ordered items enrolled in this profile. |

### `WatchlistItem` Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `Ticker` | `string` | Yes | Stock ticker symbol (automatically normalized to uppercase, e.g. `"AAPL"`). |
| `AddedAtUtc` | `string (ISO 8601)` | Yes | Enrolled timestamp. MUST have zero UTC offset (`Z` suffix, e.g. `"2026-11-02T00:00:00Z"`). |

---

## 6. Complete Reference Example (`default_workspace.json`)

```json
{
  "SchemaVersion": 2,
  "Name": "Default Profile",
  "LastModified": "2026-11-02T07:00:00Z",
  "SelectedTicker": "AAPL",
  "SelectedTimeframe": "Daily",
  "MaxCandleCount": 200,
  "ChartType": "Candlestick",
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
    "Right": 0,
    "Bottom": 0
  },
  "PanelTabIds": {
    "Left": [ "TickerList" ],
    "Right": [ "ScatterPlot" ],
    "Bottom": [ "PortfolioSummary" ]
  },
  "ThemeSettings": {
    "ChartBackground": "#181A20",
    "GridLineColor": "#2A2E39"
  },
  "Indicators": [
    {
      "Id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "TypeEnum": 0,
      "DisplayName": "SMA(20)",
      "IsEnabled": true,
      "IsOverlay": true,
      "OverlayPanelId": null,
      "Color": "#FFFF0000",
      "Thickness": 1.5,
      "Style": 0,
      "Offset": 0,
      "PriceSource": 0,
      "ParameterObject": {
        "$type": "CoreSmaParameter",
        "Period": 20
      }
    },
    {
      "Id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "TypeEnum": 34,
      "DisplayName": "RSI(14)",
      "IsEnabled": true,
      "IsOverlay": false,
      "OverlayPanelId": "SubPanel_Rsi",
      "Color": "#FF0000FF",
      "Thickness": 1.5,
      "Style": 0,
      "Offset": 0,
      "PriceSource": 0,
      "ParameterObject": {
        "$type": "CoreRsiParameter",
        "Period": 14
      }
    }
  ],
  "WatchlistProfiles": [
    {
      "Id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
      "Name": "Megacap Growth",
      "Color": "#FF2B579A",
      "IsPortfolio": false,
      "Items": [
        {
          "Ticker": "AAPL",
          "AddedAtUtc": "2026-11-02T00:00:00Z"
        },
        {
          "Ticker": "MSFT",
          "AddedAtUtc": "2026-11-02T00:00:00Z"
        }
      ]
    }
  ]
}
```
