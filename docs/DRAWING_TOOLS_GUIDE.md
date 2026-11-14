# Chart Drawing Tools & Custom Icons Guide

StockAnalyzer provides a comprehensive suite of interactive chart drawing tools designed for technical analysis, trend projection, harmonic patterns, and visual annotation.

This guide covers:
1. [Drawing Interaction & Shortcut Keys](#1-drawing-interaction--shortcut-keys)
2. [Custom Icon Stamps & Directory Layout](#2-custom-icon-stamps--directory-layout)
3. [Configuration & Persistence](#3-configuration--persistence)

---

## 1. Drawing Interaction & Shortcut Keys

StockAnalyzer's drawing engine is optimized for high responsiveness and fluid interaction. All keyboard shortcuts operate directly within the chart viewport.

### Editing & Clipboard Shortcuts

| Shortcut | Action | Description |
|---|---|---|
| **`Ctrl + Z`** | **Undo** | Reverts the last drawing operation, control point drag, property edit, or deletion. |
| **`Ctrl + Y`**<br>_or_ **`Ctrl + Shift + Z`** | **Redo** | Restores the previously undone operation. |
| **`Ctrl + C`** | **Copy** | Copies currently selected drawing object(s) to the internal drawing clipboard. |
| **`Ctrl + V`** | **Paste** | Pastes copied drawing object(s) at the current mouse cursor location on the chart. |
| **`Ctrl + X`** | **Cut** | Copies selected drawing object(s) to clipboard and removes them from the chart. |
| **`Ctrl + D`** | **Duplicate** | Instantly duplicates selected object(s) in-place with a +20px offset without modifying the system clipboard. |
| **`Ctrl + G`** | **Group** | Combines multiple selected drawing objects into a single managed group. |
| **`Ctrl + Shift + G`** | **Ungroup** | Dissolves the selected group back into individual independent objects. |

### Creation, Finalization & Cancellation

| Shortcut / Action | Tool Types | Description |
|---|---|---|
| **`Enter`**<br>_or_ **Double-Click** | Variable-Length Tools<br>_(Polyline, NURBS, Elliott Wave, Clothoid Arrow, Curve Text)_ | Finalizes the shape with current control points and commits the object. Duplicate consecutive endpoints from rapid clicking are automatically cleaned. |
| **`Ctrl + Click`**<br>_or_ **`Ctrl` Key**<br>_or_ **`Escape`** | Any Drawing Tool | **Cancels in-progress drawing** and immediately returns the active tool to **Pointer** mode without leaving ghost points. |
| **`Shift + Left-Click`** | Existing Object | **Quick Delete**: Clicking directly on an existing unselected or selected object immediately deletes it (locked objects are protected). |

### Magnet Snapping & Viewport Navigation

- **Magnet Snap**: Automatically snaps control points to the nearest candle price levels (**Open**, **High**, **Low**, **Close**) when drawing or dragging handles near candles.
- **Mouse Wheel**: Smooth horizontal chart scrolling.
- **`Ctrl + Mouse Wheel`**: Zoom in and zoom out centered on the cursor position.
- **Handle Precedence**: When pointing near an existing shape's control handle, handle manipulation takes precedence over starting a new drawing.

> [!TIP]
> **Pointer Return Policy**:
> You can choose whether the tool stays active after drawing a shape or automatically returns to Pointer mode under:
> **Settings → Chart → Drawing → After Finishing a Shape** (`ReturnToPointer` vs. `ContinueDrawing`).

---

## 2. Custom Icon Stamps & Directory Layout

StockAnalyzer includes an **Icon Stamp Tool** (`IconObject`) located in the drawing toolbar. You can stamp graphical icons, trade badges, and markers onto chart candles and price levels.

### Directory Location

Custom icons are stored locally in the workspace data folder:

```text
Data/
└── Drawings/
    └── Icon/       # Custom icon root directory
```

If the folder does not exist, StockAnalyzer creates it automatically on startup.

### Supported File Formats

`IIconDrawingService` provides hardware-accelerated Skia rendering and supports the following image formats:

| Format | Extension | Capabilities |
|---|---|---|
| **PNG** | `.png` | Standard raster images, full 32-bit alpha transparency support. |
| **SVG** | `.svg` | Scalable Vector Graphics, crisp rendering at any zoom level or DPI scale. |
| **Windows Icon** | `.ico` | Multi-resolution icon files. |
| **Apple Icon** | `.icns` | macOS icon containers (automatically extracts largest embedded PNG). |

### Automatic Category Grouping via Subfolders

Organize your icons into subdirectories to automatically create **Category Tabs** in the drawing sidebar:

```text
Data/Drawings/Icon/
├── flag_green.png              # Root files appear in "All Icons" / General
├── star_gold.svg
├── Bullish/                    # "Bullish" Category Tab
│   ├── buy_signal.png
│   ├── rocket.svg
│   └── thumbs_up.ico
├── Bearish/                    # "Bearish" Category Tab
│   ├── sell_signal.png
│   ├── caution.svg
│   └── thumbs_down.ico
└── Markers/                    # "Markers" Category Tab
    ├── entry.png
    ├── target.svg
    └── stoploss.png
```

- **Root Level (`Data/Drawings/Icon/*.png`)**: Displayed in the main "All Icons" group.
- **1st-Level Subfolders (`Data/Drawings/Icon/<Category>/`)**: The folder name is automatically registered as a distinct category tab in the icon selection UI.
- Deeper nested folders (2nd-level and beyond) are ignored.

### Dynamic Refresh & Caching

Icons are decoded and cached in memory using a thread-safe cache (`ConcurrentDictionary`). When you add or replace icons while the application is running, reopening the Icon picker or restarting the application reloads the icon cache automatically.

### Adjusting Icon Size & Appearance

1. **Global Default Size**:
   Navigate to **Settings → Chart → Drawing → DrawingIconFontSize** (default: `24px`, range: `8px` to `128px`).
2. **Individual Object Properties**:
   Double-click any placed icon on the chart to open its property dialog to adjust:
   - Size (width/height scaling)
   - Color tint / Opacity
   - Lock state (`IsLocked` prevents accidental dragging or deletion)

---

## 3. Configuration & Persistence

### Storage Format

- All chart drawing objects are saved on a per-ticker and per-timeframe basis:
  ```text
  Data/Drawings/<Symbol>.<Timeframe>.json
  ```
  Example: `Data/Drawings/AAPL.Daily.json`, `Data/Drawings/MSFT.Weekly.json`.

### Gap-Preserving Relative Offsets

Drawings use **`TicksOffset`** coordinate persistence rather than fixed calendar timestamps. This ensures trend lines and harmonic patterns maintain exact geometric alignment across weekend gaps, market holidays, and extended trading hours.

---

## 4. Analytical Drawing Parameters

Double-clicking a placed analytical drawing opens its parameter settings dialog with options stored per object:

- **Trend Line / Extended Line** (`TrendLineObject`):
  - **Extend** (`ExtendMode`, default `None`; the **Extended Line** tool starts with `Both`): `None` draws the segment only; `Right` runs the line on beyond the second click to the chart border, `Left` beyond the first click, `Both` both ways. Data Window values are unchanged (they are already the straight-line price at every time).
- **Parallel Channel** (`ParallelChannelObject`):
  - **Channel Mode** (`ChannelMode`, default `Parallel`): `Parallel` draws the opposite edge parallel to the first two clicks through the third; `FlatOpposite` draws a horizontal opposite edge at the price of the third click, spanning the times of the first two clicks (the centre line is the data midpoint between the two edges). In `FlatOpposite` the Data Window `Channel Width` changes with the time.
- **Disjoint Channel** (`DisjointChannelObject`): four clicks (edge 1 = clicks 1-2, edge 2 = clicks 3-4); unplaced points follow the cursor; `Esc` before the fourth click cancels. **Fill** (`IsFilled`, default on) tints the area between the edges. Data Window rows: `Line 1`, `Line 2`, `Midline`, `Channel Width`. Edges are not extended.
- **Cross Line** (`CrossLineObject`): one click; a horizontal and a vertical line through the point, both spanning the chart. Data Window rows: `Price`, `Line Date`, `Time Offset`.
- **Regression Trend** (`RegressionTrendObject`):
  - **StdDev Multiplier** (`StdDevMultiplier`, default `2.0`, range `0.1`–`10.0`): Adjusts the width of the upper and lower standard deviation channel lines and hit-test band.
  - **Extend Right** (`ExtendRight`, default `false`): Extends the center line and bands rightward along the extrapolated slope across trailing chart bars (and displays extrapolated values in the Data Window on those bars) and onward to the right chart edge.
- **Pitchfork** (`PitchforkObject`):
  - **Pitchfork Type** (`PitchforkType`): `Andrews`, `Schiff`, or `ModifiedSchiff`.
  - **Show Half Lines** (`ShowHalfLines`, default `false`): Toggles dashed ±0.5 parallel lines between the median line and outer tines.
  - **Show Warning Lines** (`ShowWarningLines`, default `false`): Toggles dashed ±1.5 and ±2.0 outer warning lines.
- **Anchored VWAP** (`AnchoredVwapObject`):
  - **Show Band 1** / **Band 1 Multiplier** (default `false`, `1.0`, range `0.1`–`10.0`) and **Show Band 2** / **Band 2 Multiplier** (default `false`, `2.0`, range `0.1`–`10.0`): Toggles dashed volume-weighted standard deviation bands around the anchored VWAP line and adds `Upper1`/`Lower1`/`Upper2`/`Lower2` rows to the Data Window while enabled.
- **Date / Price Range** (`DatePriceRangeObject`):
  - Two clicks define a price/time measurement box with translucent fill, centered orthogonal directional arrows, and summary label box (styled from **Settings → Theme → Main Text** and **Settings → Fonts → Detail**).
  - **Show Price Label** (`ShowPriceLabel`, default `true`), **Show Bar Label** (`ShowBarLabel`, default `true`), **Show Volume Label** (`ShowVolumeLabel`, default `true`): Independently toggle price change (`+0.00` / `+0.00%`), bar count & calendar days, and total/average volume (`N0`) in the chart summary box. Data Window rows: `Price Delta`, `Price Change %`, `Bar Count`, `Calendar Days`, `Total Volume`, `Average Volume`.
- **Elliott Wave** (`ElliottWaveObject`):
  - Variable-point placement up to the selected pattern's point limit (auto-finalizes on the last point; `Enter`, double-click, `Shift+Click`, or `Ctrl+Click` finalizes earlier with at least 2 points and returns to Pointer on `Shift+Click`/`Ctrl+Click`).
  - **Pattern Mode** (`PatternMode`, default `Impulse12345`): `Impulse12345` (6 points `0`–`(5)`), `CorrectionABC` (4 points `0`–`(C)`), or `TriangleABCDE` (6 points `0`–`(E)`). Switching to a shorter mode preserves extra points in memory without displaying or hitting them until switched back.
  - **Show Rule Validation** (`ShowRuleValidation`, default `true`): In `Impulse12345`, highlights violations of Elliott impulse rules R1 (Wave 2 retracing past 0), R2 (Wave 3 shortest of Waves 1/3/5), and R3 (Wave 4 entering Wave 1 territory) in loss color (red). Data Window rows: `Wave 2 Retracement`, `Wave 3 / Wave 1`, `Wave 4 Retracement`, `Rule Status`.
