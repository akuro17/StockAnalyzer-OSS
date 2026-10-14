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
