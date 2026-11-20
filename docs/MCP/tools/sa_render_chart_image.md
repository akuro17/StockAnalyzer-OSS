# `sa_render_chart_image`

- **Mode**: Read-Only

## Contract & Constraints

Renders the stored bars of one symbol as a PNG chart for an AI that reads images: candles, an optional volume panel, overlay indicator lines on the price panel, up to 2 oscillator panels and buy/sell markers, in the fixed style AiRecognitionV1 (the user's chart colours are never used; text is English letters and digits only; the same input gives the same mapping on every machine and the same pixels on one machine, but the shapes of the letters depend on the default font of the operating system). The result has an image block and, as text and structuredContent, the mapping that turns pixels back into numbers: axes (price range and pixel rows of the price panel), panels (the pixel rows and value range of every panel), legend (colour, pattern and width of every line), and bars (the pixel position of each drawn bar; the last bars only when there are more than the limit, the rest counted in barsOmittedCount; includeBarCoordinates false leaves the list out to save tokens). Do not read prices from the picture: take numbers from bars and from sa_calculate_indicators. Up pixel colour #D32F2F, down #1565C0, a bar with open equal to close #808080 (also in colors). Pixel coordinates: origin at the top left of the image, pixel-edge coordinates, the pixel that holds the point (x, y) is (floor x, floor y). overlays are indicators drawn on the price panel (prices scale: moving averages, bands) and oscillators each get a panel of their own; both use the item shape of sa_calculate_indicators (id, type, parameters, priceSource, sourceIndicatorId, ...), the type names come from sa_get_capabilities, and an indicator whose type belongs to the other list is INVALID_ARGUMENT. Not drawn (INVALID_ARGUMENT): indicators that look ahead (ZigZag, and any indicator that takes one as input; they are listed in nonCausalSeries of sa_calculate_indicators), indicators with their own timeFrame or a second symbol, and types the chart draws in a special way (volume profile, GranvilleLaw, market structure, Renko-like series). Parabolic SAR is drawn as dots and Ichimoku as its four causal lines (TenkanSen, KijunSen, SenkouSpanA, SenkouSpanB) with the Kumo cloud under them; the ChikouSpan looks ahead and is left out (named in notes). At most 3 lines per indicator (signal series, histograms and aliases of the main line are left out). Moving averages take the short, mid and long colours by the rank of their period (a single long average is the short colour); every n-th line of a panel is solid, dashed or dotted by n mod 3, so no line is told apart by colour alone. The indicators are calculated over up to lookbackBars stored bars (default and maximum 10000) and only the last barCount bars are drawn, so a line is the same as in sa_calculate_indicators for the same lookbackBars. endDate (yyyy-MM-dd, inclusive) ends the window at the last bar dated on or before it. markers use {date, side Buy or Sell}; date must be the date of a drawn bar exactly (see bars). Each bar needs at least 4 pixels of plot width (the plot area is the width minus 84 pixels of margins) and the panels need a minimum height: a request that does not fit is INVALID_ARGUMENT and the message states what would. includeHistogram true also draws the histogram series of an oscillator (for example the MACD histogram) as bars from zero behind its lines (#D32F2F for zero or more, #1565C0 below zero, half transparent; the panel range then includes zero and the bars). includeSavedDrawings true also draws the horizontal lines, vertical lines, trend lines, rays and parallel channels the user saved on the price panel (the drawings of the same daily, weekly or monthly chart; hidden objects and layers are skipped) in the fixed colour #111111, whatever colour the user gave them, and lists the visible ones in drawingsDrawn with their pixel positions; a time is placed as the application's chart places it (between two bars linearly, outside the loaded bars by the average bar interval; the bars loaded are lookbackBars), objects are drawn in layer order and an object in no layer is not drawn, and at most MaxImageSavedDrawings are drawn. With endDate, a saved drawing that has a control point dated after endDate is not drawn (it may not have been known then); this is tested before the limit, so such a drawing takes none of the places, and the answer gives savedDrawingsAfterEndDate and a warning. The file stores no creation time, so a drawing whose points are all on or before endDate can still have been made later. A response over the size limit is LIMIT_EXCEEDED; nothing is cut. allowUnregistered true also accepts a symbol that is not in the ticker list but has stored daily bars.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["M1","M5","M15","M30","H1","H4","D1","W1","MN1"]` | Time frame of the bars. Supported: D1, W1, MN1. |
| `endDate` | `string` | No | - | Date (yyyy-MM-dd) of the last bar to draw (inclusive). Default: the latest stored bar. |
| `barCount` | `integer` | No | default: `200`, min: `1`, max: `500` | Bars to draw (the latest ones up to endDate). At most 500; fewer when the width is small. |
| `width` | `integer` | No | default: `1200`, min: `320`, max: `1920` | Image width in pixels. |
| `height` | `integer` | No | default: `800`, min: `240`, max: `1200` | Image height in pixels. |
| `background` | `string` | No | enum: `["White","LightGray"]` | Background colour: White (#FFFFFF, default) or LightGray (#F5F5F5). |
| `includeVolume` | `boolean` | No | - | Draw the volume panel under the price panel. Default true. |
| `includeHistogram` | `boolean` | No | - | Also draw the histogram series of each oscillator (for example the MACD histogram) as bars from zero behind its lines. Default false. |
| `includeSavedDrawings` | `boolean` | No | - | Also draw the user's saved horizontal lines, vertical lines, trend lines, rays and parallel channels on the price panel in the fixed colour #111111 (the drawings of the same daily, weekly or monthly chart). Default false. |
| `includeBarCoordinates` | `boolean` | No | - | Include per-bar pixel coordinate entries in bars array; false saves tokens. Default true. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate for indicator/drawing calculations. Default and max 10000. |
| `overlays` | `array` | No | - | Indicators drawn on the price panel (price-scale types, for example SMA, EMA, BB). At most 5. |
| `oscillators` | `array` | No | - | Indicators that get a panel each (for example RSI, MACD). At most 2. |
| `markers` | `array` | No | - | Buy/sell markers on drawn bars. At most 50. |
| `allowUnregistered` | `boolean` | No | - | Also accept a symbol that is not in the ticker list but has stored daily bars (named in warnings). Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1",
  "barCount": 120,
  "overlays": [
    {
      "id": "sma25",
      "type": "SMA",
      "parameters": {
        "Period": 25
      }
    }
  ],
  "oscillators": [
    {
      "id": "rsi14",
      "type": "RSI",
      "parameters": {
        "Period": 14
      }
    }
  ]
}
```

## Common Errors

- `DATA_UNAVAILABLE`: Required workspace, portfolio, notes, model, or drawing file is unavailable.
- `INSUFFICIENT_MARKET_DATA`: Not enough stored bars or overlapping dates to compute the result.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
- `INVALID_INDICATOR_PARAMETER`: Indicator parameter name is unknown or value is outside allowed bounds.
- `LIMIT_EXCEEDED`: Requested count, window, symbol list, or estimated response tokens exceeded server bounds.
