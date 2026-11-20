# `sa_get_chart_drawings`

- **Mode**: Read-Only

## Contract & Constraints

Returns the drawings the user saved in StockAnalyzer for a symbol on a daily, weekly or monthly chart: the layers and every object (type, panel, control points, saved style and parameters). For the drawing tools sa_calculate_drawing_values can calculate (time-based control points), calculatedValues holds the values the chart's Data tab shows, evaluated at the last stored bar (or the last bar on or before endDate; the bars read are at most 10000), using the options saved with the drawing, and valuesSupported is true. Every other object (text, rectangles, objects drawn on a non-time axis ...) is returned with its full metadata and control points, valuesSupported false and calculatedValues empty. A LongPosition or ShortPosition whose Entry, Stop and Target are not in the valid order is still calculated and a warning says so. With endDate every object also carries lookAheadSuspect: true when a control point is dated after endDate (compared by calendar date), so the points and calculatedValues of that object use a later point; the object is still returned and a warning counts them. The file stores no creation time, so false does not prove the object was known at endDate. Without endDate nothing is checked and the field is absent. No drawing file: drawingFileFound false. A legacy (V1) file is not read and not converted: legacyFormat true, no layers or objects, and a warning tells to open and save it in the application. context selects the drawing context of the file (default Standard); availableContexts lists the ones the file holds. Read-only.

## Arguments (`inputSchema`)

| Argument | Type | Req | Constraints / Default | Description |
|---|---|---|---|---|
| `symbol` | `string` | Yes | - | Ticker symbol; '.' and '-' are interchangeable (7203.T and 7203-T are the same). |
| `timeFrame` | `string` | Yes | enum: `["D1","W1","MN1"]` | Time frame of the chart the drawings were made on (drawings are stored per daily, weekly and monthly chart). |
| `context` | `string` | No | - | Drawing context of the file: Standard (candle, bar and Heikin-Ashi charts; default), Linear, Kagi, Renko, PointAndFigure, ThreeLineBreak or ReverseWatch. Case-sensitive. |
| `endDate` | `string` | No | - | Evaluate the values at the last stored bar on or before this date (yyyy-MM-dd); later bars are never read. Default: the last stored bar. |
| `lookbackBars` | `integer` | No | default: `10000`, min: `1`, max: `10000` | Stored bars loaded up to endDate for the values of bar-based tools (regression trend, anchored VWAP, classic pivot points). Default and maximum 10000. |
| `allowUnregistered` | `boolean` | No | - | Accept a symbol that is not in the ticker list but has stored bars. Default false. |

## Example Call

```json
{
  "symbol": "7203-T",
  "timeFrame": "D1"
}
```

## Common Errors

- `DATA_UNAVAILABLE`: Required workspace, portfolio, notes, model, or drawing file is unavailable.
- `INVALID_ARGUMENT`: Missing required argument, wrong type/shape, invalid date range, or mutually exclusive arguments.
