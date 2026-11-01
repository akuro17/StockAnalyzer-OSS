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
- **`drawingToolContinuationMode`** (`string`, enum: `"ReturnToPointer"`, `"ContinuousDrawing"`): Interaction after placing a shape.

#### Standard Price Visuals (Candle / OHLC / Heikin / Line / Area / Dot)
- **`candlestickBullishColor`** / **`candlestickBearishColor`** / **`candlestickNeutralColor`** (`string`, Hex): Candle body colors.
- **`ohlcBullishColor`** / **`ohlcBearishColor`** (`string`, Hex): OHLC tick bar colors.
- **`heikinBullishColor`** / **`heikinBearishColor`** (`string`, Hex): Heikin-Ashi bar colors.
- **`lineColor`** / **`areaBaseColor`** (`string`, Hex): Line chart and area fill base colors.
- **`showLineMarkers`** / **`showAreaMarkers`** (`boolean`, default `false`): Data point marker dots.
- **`dotBaseRadius`** (`number`, default `3.0`): Dot chart point radius.
- **`dotShape`** (`string`, enum: `"Circle"`, `"Square"`, `"Diamond"`): Dot geometric symbol.
- **`dotPriceType`** (`string`, enum: `"Close"`, `"Open"`, `"High"`, `"Low"`): Target price field.

#### Renko Chart
- **`renkoSizingMode`** (`string`, enum: `"AutoAtr"`, `"FixedValue"`, `"Percentage"`): Brick size calculation mode.
- **`renkoBrickSize`** (`decimal`, default `1.0`): Absolute size in price units (when FixedValue).
- **`renkoBrickPercent`** (`decimal`, default `1.0`): Percentage brick size (when Percentage).
- **`renkoAtrPeriod`** (`integer`, default `14`): ATR lookback period (when AutoAtr).
- **`renkoAtrMultiplier`** (`decimal`, default `1.0`): ATR multiplier for brick height.
- **`renkoReversal`** (`integer`, default `2`): Brick count required for trend reversal.
- **`renkoShowMultiWavePatterns`** (`boolean`, default `false`): ZigZag / multi-wave trendlines.
- **`renkoShowGhostProjections`** (`boolean`, default `false`): Next-brick threshold projection guides.

#### Kagi Chart
- **`kagiReversalMode`** (`string`, enum: `"AutoAtr"`, `"FixedValue"`, `"Percentage"`): Reversal threshold mode.
- **`kagiReversalAmount`** (`decimal`, default `1.0`): Reversal amount in price units.
- **`kagiReversalPercent`** (`decimal`, default `1.0`): Reversal amount in percent.
- **`kagiAtrPeriod`** (`integer`, default `14`) & **`kagiAtrMultiplier`** (`decimal`, default `1.0`).
- **`kagiLineThickness`** (`number`, default `1.0`): Normal line thickness (Yang thick line is doubled).

#### Point & Figure (PnF)
- **`pnfSizingMode`** (`string`, enum: `"AutoAtr"`, `"FixedValue"`, `"Percentage"`).
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

---

## 2. Theme Customization (`user_theme.json`)

| Attribute | Detail |
|---|---|
| **File Location** | `Data/Config/user_theme.json` |
| **Managing Class** | `StockAnalyzer.Core.Theme.ThemeManager` (`IThemeManager`) |

Stores user-customized palette overrides for the active theme.

```json
{
  "ChartBackground": "#1E1E1E",
  "GridLineColor": "#2D2D30",
  "AxisTextColor": "#CCCCCC",
  "CrosshairColor": "#808080",
  "BullishColor": "#26A69A",
  "BearishColor": "#EF5350",
  "NeutralColor": "#787B86",
  "VolumeUpColor": "#4D26A69A",
  "VolumeDownColor": "#4DEF5350"
}
```

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
