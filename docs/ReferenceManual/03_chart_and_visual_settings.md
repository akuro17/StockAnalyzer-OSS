# 03. Chart & Visual Settings

This document specifies the individual settings files controlling chart rendering, visual styles, custom themes, fonts, and note editor aesthetics. All files reside in `Data/Config/` and are resolved via `PathDiscovery.ResolveConfigPath()`.

---

## 1. Global Chart Settings (`user_chart_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_chart_settings.json` |
| **Managing Class** | `StockAnalyzer.Core.Services.ChartSettingsManager` (`IChartSettingsManager`) |
| **Model Class** | `StockAnalyzer.Core.Models.Settings.GlobalChartSettings` |
| **Schema Version** | `1` (`SchemaVersion = 1`) |
| **Serialization Naming Policy** | `JsonNamingPolicy.CamelCase` (**All property names MUST be camelCase**) |

> [!IMPORTANT]
> `ChartSettingsManager` serializes and deserializes using strict `camelCase` property naming. Writing PascalCase keys (e.g. `"DefaultStrokeThickness"`) will cause properties to be ignored, falling back to default values.

### Key Properties & Numerical Bounds

#### Drawing Tools & Interaction
- **`defaultStrokeThickness`** (`number`, default `1.0`, range `[0.1, 10.0]`): Default stroke width for drawing tools in DIPs.
- **`drawingDefaultColor`** (`string`, Hex `#AARRGGBB`, default `"#FFFF0000"`): Default shape stroke color.
- **`drawingHandleColor`** (`string`, Hex, default `"#FF0000FF"`): Control point handle color.
- **`anchorPointColor`** (`string`, Hex, default `"#FFFF8000"`): Pivot/anchor point highlight color.
- **`drawingFontSize`** (`number`, default `12.0`): Typography size for text boxes and callouts.
- **`smartGuidesEnabled`** (`boolean`, default `true`): Magnet/alignment snap guides.
- **`smartGuideSnapDistance`** (`number`, default `8.0`): Distance threshold for snapping.
- **`controlPointHideTimeoutSeconds`** (`integer`, default `5`, range `[1, 60]`): Idle duration before deselecting handles.
- **`drawingToolContinuationMode`** (`string` or `integer`, enum: `"ReturnToPointer"` [0], `"ContinuousDrawing"` [1]): Interaction after placing a shape.

#### Standard Price Visuals (Candle / OHLC / Heikin / Line / Area / Dot)
- **`candlestickBullishColor`** / **`candlestickBearishColor`** / **`candlestickNeutralColor`** (`string`, Hex): Candle body colors.
- **`ohlcBullishColor`** / **`ohlcBearishColor`** (`string`, Hex): OHLC tick bar colors.
- **`heikinBullishColor`** / **`heikinBearishColor`** (`string`, Hex): Heikin-Ashi bar colors.
- **`lineColor`** / **`areaBaseColor`** (`string`, Hex): Line chart and area fill base colors.
- **`showLineMarkers`** / **`showAreaMarkers`** (`boolean`, default `false`): Data point marker dots.
- **`dotBaseRadius`** (`number`, default `3.0`): Dot chart point radius.
- **`dotShape`** (`string` or `integer`, enum: `"Circle"` [0], `"Square"` [1], `"Diamond"` [2]): Dot geometric symbol.
- **`dotPriceType`** (`string` or `integer`, enum: `"Close"` [0], `"Open"` [1], `"High"` [2], `"Low"` [3]): Target price field.

#### Renko Chart
- **`renkoSizingMode`** (`string` or `integer`, enum: `"AutoAtr"` [0], `"FixedValue"` [1], `"Percentage"` [2]): Brick size calculation mode.
- **`renkoBrickSize`** (`decimal`, default `1.0`): Absolute size in price units (when FixedValue).
- **`renkoBrickPercent`** (`decimal`, default `1.0`): Percentage brick size (when Percentage).
- **`renkoAtrPeriod`** (`integer`, default `14`): ATR lookback period (when AutoAtr).
- **`renkoAtrMultiplier`** (`decimal`, default `1.0`): ATR multiplier for brick height.
- **`renkoReversal`** (`integer`, default `2`): Brick count required for trend reversal.
- **`renkoShowMultiWavePatterns`** (`boolean`, default `false`): ZigZag / multi-wave trendlines.
- **`renkoShowGhostProjections`** (`boolean`, default `false`): Next-brick threshold projection guides.

#### Kagi Chart
- **`kagiReversalMode`** (`string` or `integer`, enum: `"AutoAtr"` [0], `"FixedValue"` [1], `"Percentage"` [2]): Reversal threshold mode.
- **`kagiReversalAmount`** (`decimal`, default `1.0`): Reversal amount in price units.
- **`kagiReversalPercent`** (`decimal`, default `1.0`): Reversal amount in percent.
- **`kagiAtrPeriod`** (`integer`, default `14`) & **`kagiAtrMultiplier`** (`decimal`, default `1.0`).
- **`kagiLineThickness`** (`number`, default `1.0`): Normal line thickness (Yang thick line is doubled).

#### Point & Figure (PnF)
- **`pnfSizingMode`** (`string` or `integer`, enum: `"AutoAtr"` [0], `"FixedValue"` [1], `"Percentage"` [2]).
- **`pnfBoxSize`** (`decimal`, default `1.0`) / **`pnfBoxPercent`** (`decimal`, default `1.0`).
- **`pnfReversal`** (`integer`, default `3`): Box count reversal threshold (standard 3-box reversal).
- **`pnfShowDoubleBreakout`** / **`pnfShowTripleBreakout`** / **`pnfShowTrendlineBreakout`** (`boolean`): Classical PnF chart pattern overlays.

#### Three Line Break
- **`threeLineBreakBullishColor`** / **`threeLineBreakBearishColor`** (`string`, Hex).
- **`threeLineBreakShowGhostProjections`** (`boolean`, default `false`).

#### Reverse Watch (Phases 1..8)
- **`reverseWatchPhase1Color`** .. **`reverseWatchPhase8Color`** (`string`, Hex): Palette for the 8 cyclical market phases.
- **`reverseWatchIsMaBased`** (`boolean`, default `true`): Calculate deviations against moving average.
- **`reverseWatchPeriod`** (`integer`, default `20`): Lookback period.

#### Seasonality Chart & Heatmap
- **`seasonalityStartMonth`** (`integer`, default `1` [January], range `1..12`).
- **`seasonalityYearsToOverlay`** (`integer`, default `10`, range `1..50`): Multi-year comparison count.
- **`seasonalityYearColor1`** .. **`seasonalityYearColor10`** (`string`, Hex): Per-year overlay trace colors.
- **`scatterHeatmapThresholdPercent`** (`number`, default `2.0`): Percent return threshold for heatmap saturation.

#### Backtest Equity Curve Styling
- **`backtestEquityColorMode`** (`integer`, enum: `0` = Single [uniform color], `1` = PreviousBar [bar-by-bar delta], `2` = Drawdown [color changes when underwater]).
- **`backtestEquityLineColor`** (`string`, Hex, default `"#FF007ACC"`): Line color for Single mode.
- **`backtestEquityUpColor`** (`string`, Hex, default `"#FF4CAF50"`): Upward color for PreviousBar/Drawdown mode.
- **`backtestEquityDownColor`** (`string`, Hex, default `"#FFF44336"`): Downward/underwater color.

### Complete Reference Example (`user_chart_settings.json`)

```json
{
  "defaultStrokeThickness": 1.5,
  "drawingDefaultColor": "#FFFF0000",
  "drawingHandleColor": "#FF0000FF",
  "anchorPointColor": "#FFFF8000",
  "drawingFontSize": 12.0,
  "smartGuidesEnabled": true,
  "smartGuideSnapDistance": 8.0,
  "controlPointHideTimeoutSeconds": 5,
  "candlestickBullishColor": "#FF4CAF50",
  "candlestickBearishColor": "#FFF44336",
  "candlestickNeutralColor": "#FF787B86",
  "ohlcBullishColor": "#FF4CAF50",
  "ohlcBearishColor": "#FFF44336",
  "heikinBullishColor": "#FF4CAF50",
  "heikinBearishColor": "#FFF44336",
  "lineColor": "#FF007ACC",
  "areaBaseColor": "#4D007ACC",
  "showLineMarkers": false,
  "showAreaMarkers": false,
  "dotBaseRadius": 3.0,
  "dotShape": 0,
  "dotPriceType": 0,
  "renkoSizingMode": 0,
  "renkoBrickSize": 1.0,
  "renkoBrickPercent": 1.0,
  "renkoAtrPeriod": 14,
  "renkoAtrMultiplier": 1.0,
  "renkoReversal": 2,
  "renkoShowMultiWavePatterns": false,
  "renkoShowGhostProjections": false,
  "kagiReversalMode": 0,
  "kagiReversalAmount": 1.0,
  "kagiReversalPercent": 1.0,
  "kagiAtrPeriod": 14,
  "kagiAtrMultiplier": 1.0,
  "kagiLineThickness": 1.0,
  "pnfSizingMode": 0,
  "pnfBoxSize": 1.0,
  "pnfBoxPercent": 1.0,
  "pnfReversal": 3,
  "pnfShowDoubleBreakout": false,
  "pnfShowTripleBreakout": false,
  "pnfShowTrendlineBreakout": false,
  "threeLineBreakBullishColor": "#FF4CAF50",
  "threeLineBreakBearishColor": "#FFF44336",
  "threeLineBreakShowGhostProjections": false,
  "seasonalityStartMonth": 1,
  "seasonalityYearsToOverlay": 10,
  "scatterHeatmapThresholdPercent": 2.0,
  "backtestEquityColorMode": 0,
  "backtestEquityLineColor": "#FF007ACC",
  "backtestEquityUpColor": "#FF4CAF50",
  "backtestEquityDownColor": "#FFF44336"
}
```

---

## 2. Theme Customization (`user_theme.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_theme.json` |
| **Managing Class** | `StockAnalyzer.Core.Theme.ThemeManager` (`IThemeManager`) |
| **Root DTO Class** | `ThemePersistenceData` (`Mode`, `Colors`) |
| **Color Model Class** | `StockAnalyzer.Core.Theme.ThemeColors` |

Stores user-customized palette overrides for the active theme.

> [!NOTE]
> The root JSON object is `ThemePersistenceData`, which wraps `Mode` and `Colors`. Note that color property names inside `Colors` do NOT have a `Color` suffix (e.g. `GridLine`, `AxisText`, `Bullish`, NOT `GridLineColor`).

### Complete Reference Example (`user_theme.json`)

```json
{
  "Mode": 1,
  "Colors": {
    "ChartBackground": "#FF181A20",
    "GridLine": "#FF2A2E39",
    "AxisText": "#FF787B86",
    "Crosshair": "#FF758696",
    "CrosshairLineAlpha": "#A0758696",
    "CrosshairText": "#FF758696",
    "ZeroLine": "#FF444444",
    "Bullish": "#FF4CAF50",
    "Bearish": "#FFF44336",
    "BullishWick": "#FF4CAF50",
    "BearishWick": "#FFF44336",
    "VolumeUp": "#FF4CAF50",
    "VolumeDown": "#FFF44336",
    "SemanticPlus": "#FF4CAF50",
    "SemanticMinus": "#FFF44336",
    "SemanticNeutral": "#FFE0E0E0",
    "HeaderBackground": "#B41E1E1E",
    "VolumeProfileBorder": "#64C8C8C8",
    "GeometricLabelText": "#C8C8C8C8",
    "ShellBackground": "#FF181A20",
    "ShellText": "#FFE0E0E0",
    "ShellSecondaryText": "#FFBAC2CE",
    "ShellAccent": "#FF648CFF",
    "ShellBorder": "#FF363A45",
    "ButtonBackground": "#FF007ACC",
    "ButtonText": "#FFFFFFFF",
    "ButtonHover": "#FF1C97EA",
    "ButtonPressed": "#FF005A9E",
    "ControlBackground": "#FF2A2E39",
    "ControlBackgroundHover": "#FF363A45",
    "ControlBackgroundPressed": "#FF444444",
    "IsDark": true
  }
}
```

- **`Mode`** (`integer`, enum `AppThemeMode`): `0` = Light, `1` = Dark, `2` = System.

---

## 3. Font Typography Settings (`user_font_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_font_settings.json` |
| **Managing Class** | `StockAnalyzer.Avalonia.Services.FontSettingsManager` (`IFontSettingsManager`) |
| **Allowed Range** | `12.0` to `24.0` DIPs per font size property |

```json
{
  "BaseFontSize": 14.0,
  "TitleFontSize": 16.0,
  "DetailFontSize": 12.0,
  "HelperFontSize": 12.0,
  "TooltipFontSize": 14.0
}
```

---

## 4. Ticker Notes Appearance Settings (`user_notes_settings.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_notes_settings.json` |
| **Managing Class** | `StockAnalyzer.Avalonia.Services.NotesSettingsManager` |

Controls the Markdown note editor and timeline view styles for Dark and Light theme modes.

```json
{
  "ThumbnailSizePixels": 120.0,
  "ImageDisplayMode": 0,
  "MaxRenderedThumbnails": 4,
  "ReadMoreMaxCharacters": 300,
  "ReadMoreMaxLines": 6,
  "ThreadCollapseThreshold": 3,
  "TailVisibleCount": 2,
  "ConnectorLineLength": 24.0,
  "DashLength": 4.0,
  "TimelinePageSize": 50,
  "UpcomingCatalystsLimit": 5,
  "UpcomingCatalystsPastDaysRetention": 7,
  "BodyFontSize": 14.0,
  "BodyTextColorDark": "#FFFFFFFF",
  "BodyTextColorLight": "#FF1E1E1E",
  "BodyBackgroundColorDark": "#FF252526",
  "BodyBackgroundColorLight": "#FFF3F3F3",
  "UrlColorDark": "#FF4FC3F7",
  "UrlColorLight": "#FF0288D1",
  "HashtagColorDark": "#FF81C784",
  "HashtagColorLight": "#FF388E3C",
  "HighlightColorDark": "#FFFFD54F",
  "HighlightColorLight": "#FFFBC02D"
}
```
